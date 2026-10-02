using System.Text;
using System.Text.RegularExpressions;

namespace UnityDecompiler.Core;

public enum SecretSeverity { Low, Medium, High }

public sealed record SecretFinding(
    string Rule,
    SecretSeverity Severity,
    string File,
    int Line,
    string Match,
    string? Context);

/// <summary>
/// Scans decompiled C# and the game's text config/asset files for credentials a developer left
/// hardcoded in the build: API keys, tokens, passwords, connection strings and private keys.
///
/// This is a defensive audit: it only reads files already produced into the output folder and
/// reports where a secret sits so the owner can rotate or remove it. Matches are redacted in the
/// report so the report itself does not become a new place the secret leaks.
/// </summary>
public static class SecretScanner
{
    private sealed record Rule(string Name, SecretSeverity Severity, Regex Pattern, Func<Match, bool>? Validate = null);

    // Patterns are intentionally specific to keep false positives low. Each captures the secret in group 1
    // (or the whole match) so it can be redacted.
    private static readonly Rule[] Rules =
    {
        new("AWS Access Key ID", SecretSeverity.High, Re(@"\b(A(?:KIA|SIA|GPA|IDA|ROA|NPA|NVA)[0-9A-Z]{16})\b")),
        new("AWS Secret Access Key", SecretSeverity.High,
            Re(@"(?i)aws.{0,20}?(?:secret|key).{0,20}?[=:\s""']+([A-Za-z0-9/+]{40})(?![A-Za-z0-9/+])")),
        new("Google API Key", SecretSeverity.High, Re(@"\b(AIza[0-9A-Za-z\-_]{35})\b")),
        new("Google OAuth Client Secret", SecretSeverity.High, Re(@"\b(GOCSPX-[0-9A-Za-z\-_]{28})\b")),
        new("Firebase Cloud Messaging Key", SecretSeverity.High, Re(@"\b(AAAA[A-Za-z0-9_-]{7}:[A-Za-z0-9_-]{140})\b")),
        new("Slack Token", SecretSeverity.High, Re(@"\b(xox[baprs]-[0-9A-Za-z-]{10,72})\b")),
        new("Slack Webhook", SecretSeverity.High, Re(@"(https://hooks\.slack\.com/services/T[0-9A-Z]+/B[0-9A-Z]+/[0-9A-Za-z]{24})")),
        new("Discord Bot Token", SecretSeverity.High,
            Re(@"\b([MNO][A-Za-z0-9_-]{23,25}\.[A-Za-z0-9_-]{6}\.[A-Za-z0-9_-]{27,40})\b")),
        new("Discord Webhook", SecretSeverity.Medium,
            Re(@"(https://(?:ptb\.|canary\.)?discord(?:app)?\.com/api/webhooks/\d+/[A-Za-z0-9_-]+)")),
        new("GitHub Token", SecretSeverity.High, Re(@"\b((?:ghp|gho|ghu|ghs|ghr|github_pat)_[A-Za-z0-9_]{36,255})\b")),
        new("Stripe Secret Key", SecretSeverity.High, Re(@"\b((?:sk|rk)_live_[0-9A-Za-z]{24,99})\b")),
        new("Twilio API Key", SecretSeverity.High, Re(@"\b(SK[0-9a-fA-F]{32})\b")),
        new("SendGrid API Key", SecretSeverity.High, Re(@"\b(SG\.[A-Za-z0-9_-]{22}\.[A-Za-z0-9_-]{43})\b")),
        new("OpenAI API Key", SecretSeverity.High, Re(@"\b(sk-(?:proj-)?[A-Za-z0-9_-]{20,}T3BlbkFJ[A-Za-z0-9_-]{20,})\b")),
        new("JSON Web Token", SecretSeverity.Medium, Re(@"\b(eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,})\b")),
        new("Private Key Block", SecretSeverity.High, Re(@"(-----BEGIN (?:RSA |EC |DSA |OPENSSH |PGP )?PRIVATE KEY-----)")),
        new("Database Connection String", SecretSeverity.High,
            Re(@"((?:mongodb(?:\+srv)?|postgres(?:ql)?|mysql|redis|amqp|mssql):\/\/[^\s:@/]+:[^\s:@/]+@[^\s""']+)")),
        new("Generic Secret Assignment", SecretSeverity.Medium,
            Re(@"(?i)\b(?:api[_-]?key|apikey|secret|passwd|password|token|auth[_-]?token|access[_-]?token|client[_-]?secret|private[_-]?key)\b\s*[=:]\s*[""']([^""'\s]{8,120})[""']"),
            m => LooksLikeSecret(m.Groups[1].Value)),
    };

    private static Regex Re(string pattern) => new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Text file types worth scanning. Binary assets are skipped.
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".json", ".xml", ".txt", ".ini", ".cfg", ".config", ".yaml", ".yml", ".csv",
        ".js", ".html", ".htm", ".css", ".md", ".env", ".properties", ".toml", ".sql", ".asset", ".shader",
    };

    private const long MaxFileBytes = 8L * 1024 * 1024;
    private const int MaxFindings = 2000;

    /// <summary>Scan a produced output folder. Returns findings sorted by severity.</summary>
    public static IReadOnlyList<SecretFinding> ScanDirectory(string directory, CancellationToken ct = default)
    {
        var findings = new List<SecretFinding>();
        if (!Directory.Exists(directory)) return findings;

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (findings.Count >= MaxFindings) break;
            if (!TextExtensions.Contains(Path.GetExtension(file))) continue;

            FileInfo info;
            try { info = new FileInfo(file); } catch (IOException) { continue; }
            if (info.Length is 0 or > MaxFileBytes) continue;

            var relative = Path.GetRelativePath(directory, file);
            try { ScanFile(file, relative, findings, ct); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* skip unreadable */ }
        }

        return findings
            .GroupBy(f => (f.Rule, f.File, f.Line, f.Match))
            .Select(g => g.First())
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Line)
            .ToList();
    }

    private static void ScanFile(string path, string relative, List<SecretFinding> findings, CancellationToken ct)
    {
        // Binary sniff: a NUL byte in the first chunk means it is not really text.
        using var stream = File.OpenRead(path);
        var head = new byte[Math.Min(1024, (int)stream.Length)];
        var read = stream.Read(head, 0, head.Length);
        if (Array.IndexOf(head, (byte)0, 0, read) >= 0) return;
        stream.Position = 0;

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            if (findings.Count >= MaxFindings) return;
            if (line.Length > 4000) continue; // skip minified/embedded blobs

            foreach (var rule in Rules)
            {
                foreach (Match match in rule.Pattern.Matches(line))
                {
                    ct.ThrowIfCancellationRequested();
                    if (rule.Validate != null && !rule.Validate(match)) continue;
                    var secret = match.Groups.Count > 1 && match.Groups[1].Success ? match.Groups[1].Value : match.Value;
                    findings.Add(new SecretFinding(rule.Name, rule.Severity, relative, lineNumber,
                        Redact(secret), Snippet(line, match.Index)));
                    if (findings.Count >= MaxFindings) return;
                }
            }
        }
    }

    /// <summary>Filters obvious placeholders so the generic rule does not flag every example value.</summary>
    private static bool LooksLikeSecret(string value)
    {
        if (value.Length < 8) return false;
        var lower = value.ToLowerInvariant();
        string[] placeholders =
        {
            "your", "example", "changeme", "placeholder", "xxxx", "0000", "1234", "none", "null", "empty",
            "test", "sample", "dummy", "password", "secret", "redacted", "insert", "todo", "<", "{", "}",
        };
        if (placeholders.Any(p => lower.Contains(p))) return false;
        if (value.Distinct().Count() < 5) return false; // "aaaaaaaa"

        // Require some entropy: a healthy mix of character classes or a long-ish random-looking string.
        var hasUpper = value.Any(char.IsUpper);
        var hasLower = value.Any(char.IsLower);
        var hasDigit = value.Any(char.IsDigit);
        var classes = (hasUpper ? 1 : 0) + (hasLower ? 1 : 0) + (hasDigit ? 1 : 0);
        return classes >= 2 && ShannonEntropy(value) >= 3.0;
    }

    private static double ShannonEntropy(string s)
    {
        var counts = new Dictionary<char, int>();
        foreach (var c in s) counts[c] = counts.GetValueOrDefault(c) + 1;
        double entropy = 0, len = s.Length;
        foreach (var count in counts.Values)
        {
            var p = count / len;
            entropy -= p * Math.Log2(p);
        }
        return entropy;
    }

    /// <summary>Show enough to recognise the secret without reprinting it in full.</summary>
    private static string Redact(string secret)
    {
        secret = secret.Trim();
        if (secret.StartsWith("-----BEGIN", StringComparison.Ordinal)) return secret; // marker only, not the key body
        if (secret.Length <= 8) return new string('*', secret.Length);
        var keep = Math.Min(4, secret.Length / 4);
        return secret[..keep] + new string('*', Math.Min(12, secret.Length - keep * 2)) + secret[^keep..];
    }

    private static string Snippet(string line, int index)
    {
        var start = Math.Max(0, index - 24);
        var snippet = line.Substring(start, Math.Min(line.Length - start, 96)).Trim();
        return start > 0 ? "…" + snippet : snippet;
    }

    /// <summary>Writes SECURITY-SCAN.txt into the folder and returns the findings.</summary>
    public static IReadOnlyList<SecretFinding> ScanAndReport(string directory, CancellationToken ct = default)
    {
        var findings = ScanDirectory(directory, ct);
        var sb = new StringBuilder();
        sb.AppendLine("Credential / secret scan");
        sb.AppendLine("========================");
        sb.AppendLine($"Scanned: {directory}");
        sb.AppendLine($"When:    {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        if (findings.Count == 0)
        {
            sb.AppendLine("No hardcoded credentials were found in the scanned text files.");
            sb.AppendLine("(Note: this scans text only. Encrypted or IL2CPP-compiled games may hide secrets this scan cannot see.)");
        }
        else
        {
            sb.AppendLine($"Found {findings.Count} possible secret(s). Secrets are partly redacted below.");
            sb.AppendLine("If any are real, treat them as compromised: rotate/revoke them at the provider.");
            sb.AppendLine();
            foreach (var group in findings.GroupBy(f => f.Severity).OrderByDescending(g => g.Key))
            {
                sb.AppendLine($"-- {group.Key} severity ({group.Count()}) --");
                foreach (var f in group)
                {
                    sb.AppendLine($"  [{f.Rule}] {f.File}:{f.Line}");
                    sb.AppendLine($"      {f.Match}");
                    if (f.Context != null) sb.AppendLine($"      in: {f.Context}");
                }
                sb.AppendLine();
            }
        }

        try { File.WriteAllText(Path.Combine(directory, "SECURITY-SCAN.txt"), sb.ToString()); }
        catch (IOException) { /* report file is best-effort */ }
        return findings;
    }
}
