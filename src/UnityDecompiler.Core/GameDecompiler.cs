using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.Solution;

namespace UnityDecompiler.Core;

public sealed class DecompileOptions
{
    /// <summary>The game .exe, game folder, .zip, _Data folder or a .dll.</summary>
    public required string InputPath { get; init; }

    /// <summary>Where to write the result. Null = "Documents/Unity Decompiler/&lt;Game&gt;".</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>Also decompile UnityEngine.*, System.*, mscorlib etc. (slow, rarely useful).</summary>
    public bool IncludeFrameworkAssemblies { get; init; }

    /// <summary>Copy the StreamingAssets folder into the output (skipped if it is huge).</summary>
    public bool CopyStreamingAssets { get; init; } = true;

    public long MaxStreamingAssetsBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Scan the decompiled output for hardcoded credentials and write SECURITY-SCAN.txt.</summary>
    public bool ScanForSecrets { get; init; } = true;
}

public sealed record DecompileProgress(string Message, double? Fraction = null, LogLevel Level = LogLevel.Info);

public enum LogLevel { Info, Success, Warning, Error }

public sealed record DecompileResult(UnityGame Game, string OutputDirectory, string? SolutionPath, int AssembliesDecompiled, int AssembliesFailed)
{
    /// <summary>Hardcoded credentials found in the decompiled output (empty if none or scanning was off).</summary>
    public IReadOnlyList<SecretFinding> Secrets { get; init; } = Array.Empty<SecretFinding>();
}

public static class GameDecompiler
{
    public static string DefaultOutputRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Unity Decompiler");

    private static readonly string[] FrameworkPrefixes =
    {
        "UnityEngine", "UnityEditor", "Unity.", "System", "mscorlib", "netstandard", "Mono.", "Microsoft.",
        "Boo.Lang", "UnityScript", "nunit.framework",
    };

    public static bool IsFrameworkAssembly(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        return FrameworkPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    public static DecompileResult Run(DecompileOptions options, IProgress<DecompileProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        void Log(string message, LogLevel level = LogLevel.Info, double? fraction = null) =>
            progress?.Report(new DecompileProgress(message, fraction, level));

        string? tempDir = null;
        try
        {
            var input = options.InputPath.Trim().Trim('"');
            if (File.Exists(input) && Path.GetExtension(input).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                tempDir = Path.Combine(Path.GetTempPath(), "UnityDecompiler_" + Guid.NewGuid().ToString("N")[..8]);
                Log($"Extracting {Path.GetFileName(input)}...", fraction: 0);
                ZipFile.ExtractToDirectory(input, tempDir);
                input = tempDir;
            }

            Log("Looking for the Unity game...", fraction: 0);
            var game = GameLocator.Locate(input);
            Log($"Found: {game.DisplayName}" + (game.UnityVersion != null ? $" (Unity {game.UnityVersion})" : ""), LogLevel.Success);
            Log($"Scripting backend: {game.Backend}");

            var outDir = PrepareOutputDirectory(options.OutputDirectory, game);
            Log($"Output folder: {outDir}");

            string? solution = null;
            int ok = 0, failed = 0;
            switch (game.Backend)
            {
                case ScriptingBackend.Mono:
                    (solution, ok, failed) = DecompileMono(game, outDir, options, Log, cancellationToken);
                    break;
                case ScriptingBackend.IL2CPP:
                    HandleIl2Cpp(game, outDir, Log);
                    break;
                default:
                    Log("No game scripts were found in this build (no Managed folder and no IL2CPP data).", LogLevel.Warning);
                    break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (options.CopyStreamingAssets && game.DataDirectory != null)
                CopyStreamingAssets(game, outDir, options.MaxStreamingAssetsBytes, Log);

            IReadOnlyList<SecretFinding> secrets = Array.Empty<SecretFinding>();
            if (options.ScanForSecrets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Log("Scanning the decompiled code for leaked credentials...");
                secrets = SecretScanner.ScanAndReport(outDir, cancellationToken);
                if (secrets.Count == 0)
                    Log("No hardcoded credentials found.", LogLevel.Success);
                else
                {
                    var high = secrets.Count(s => s.Severity == SecretSeverity.High);
                    Log($"Found {secrets.Count} possible secret(s){(high > 0 ? $", {high} high severity" : "")} - see SECURITY-SCAN.txt.",
                        high > 0 ? LogLevel.Error : LogLevel.Warning);
                }
            }

            WriteGameInfo(game, outDir, ok, failed, secrets);
            Log(failed == 0 ? "Done!" : $"Done, but {failed} assembl{(failed == 1 ? "y" : "ies")} failed.",
                failed == 0 ? LogLevel.Success : LogLevel.Warning, 1);
            return new DecompileResult(game, outDir, solution, ok, failed) { Secrets = secrets };
        }
        finally
        {
            if (tempDir != null)
            {
                try { Directory.Delete(tempDir, recursive: true); } catch (Exception) { /* best effort */ }
            }
        }
    }

    private static string PrepareOutputDirectory(string? requested, UnityGame game)
    {
        var baseDir = string.IsNullOrWhiteSpace(requested)
            ? Path.Combine(DefaultOutputRoot, SafeFileName(game.DisplayName))
            : requested!;

        // Never mix results with an older run: pick "Name (2)", "Name (3)", ...
        var dir = baseDir;
        for (var i = 2; Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any(); i++)
            dir = $"{baseDir} ({i})";
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (string? Solution, int Ok, int Failed) DecompileMono(
        UnityGame game, string outDir, DecompileOptions options, Action<string, LogLevel, double?> log, CancellationToken ct)
    {
        var assemblies = game.LooseAssemblies.Count > 0
            ? game.LooseAssemblies.ToList()
            : Directory.EnumerateFiles(game.ManagedDirectory!, "*.dll")
                .Where(f => options.IncludeFrameworkAssemblies || !IsFrameworkAssembly(f))
                .OrderBy(f => Path.GetFileName(f).StartsWith("Assembly-CSharp", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();

        if (assemblies.Count == 0)
        {
            log("No game assemblies found in the Managed folder.", LogLevel.Warning, null);
            return (null, 0, 0);
        }

        log($"Decompiling {assemblies.Count} assembl{(assemblies.Count == 1 ? "y" : "ies")}...", LogLevel.Info, null);

        var scriptsDir = Path.Combine(outDir, "Scripts");
        var projects = new List<ProjectItem>();
        int ok = 0, failed = 0;
        for (var i = 0; i < assemblies.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = assemblies[i];
            var asmName = Path.GetFileNameWithoutExtension(file);
            double baseFraction = (double)i / assemblies.Count, slice = 1.0 / assemblies.Count;
            log($"[{i + 1}/{assemblies.Count}] {asmName}", LogLevel.Info, baseFraction);

            try
            {
                var project = DecompileAssembly(file, Path.Combine(scriptsDir, SafeFileName(asmName)), game.ManagedDirectory, ct,
                    (done, total) => log($"[{i + 1}/{assemblies.Count}] {asmName}: {done}/{total} files", LogLevel.Info,
                        baseFraction + slice * (total == 0 ? 0 : (double)done / total)));
                if (project == null)
                {
                    log($"    skipped {Path.GetFileName(file)} (not a .NET assembly)", LogLevel.Warning, null);
                    continue;
                }
                projects.Add(project);
                ok++;
                log($"    {asmName} decompiled", LogLevel.Success, baseFraction + slice);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                failed++;
                log($"    failed to decompile {asmName}: {e.Message}", LogLevel.Error, null);
            }
        }

        string? solutionPath = null;
        if (projects.Count > 0)
        {
            solutionPath = Path.Combine(outDir, SafeFileName(game.Name) + ".sln");
            try { SolutionCreator.WriteSolutionFile(solutionPath, projects); }
            catch (Exception e)
            {
                log($"Could not write the .sln file: {e.Message}", LogLevel.Warning, null);
                solutionPath = null;
            }
        }
        return (solutionPath, ok, failed);
    }

    /// <summary>Decompiles one assembly into a C# project. Returns null when the file is not a managed assembly.</summary>
    private static ProjectItem? DecompileAssembly(string file, string targetDir, string? managedDir, CancellationToken ct, Action<int, int> onProgress)
    {
        PEFile module;
        try
        {
            using var fs = File.OpenRead(file);
            module = new PEFile(file, fs, PEStreamOptions.PrefetchEntireImage);
        }
        catch (Exception e) when (e is BadImageFormatException or MetadataFileNotSupportedException)
        {
            return null;
        }

        using (module)
        {
            var resolver = new UniversalAssemblyResolver(file, false, module.DetectTargetFrameworkId(), module.DetectRuntimePack(),
                PEStreamOptions.PrefetchMetadata, MetadataReaderOptions.None);
            if (managedDir != null) resolver.AddSearchDirectory(managedDir);
            resolver.AddSearchDirectory(Path.GetDirectoryName(file)!);

            var settings = new DecompilerSettings(LanguageVersion.CSharp9_0)
            {
                ThrowOnAssemblyResolveErrors = false,
                UseSdkStyleProjectFormat = true,
                UseNestedDirectoriesForNamespaces = true,
                RemoveDeadCode = false,
            };

            Directory.CreateDirectory(targetDir);
            var decompiler = new WholeProjectDecompiler(settings, resolver, null, null, null)
            {
                ProgressIndicator = new SyncProgress<DecompilationProgress>(p => onProgress(p.UnitsCompleted, p.TotalUnits)),
            };
            var projectFile = Path.Combine(targetDir, Path.GetFileName(targetDir) + ".csproj");
            ProjectId id;
            using (var writer = new StreamWriter(projectFile))
                id = decompiler.DecompileProject(module, targetDir, writer, ct);
            return new ProjectItem(projectFile, id.PlatformName, id.Guid, id.TypeGuid);
        }
    }

    private static void HandleIl2Cpp(UnityGame game, string outDir, Action<string, LogLevel, double?> log)
    {
        var il2cppDir = Path.Combine(outDir, "IL2CPP");
        Directory.CreateDirectory(il2cppDir);

        int? metadataVersion = null;
        if (game.Il2CppMetadataPath != null)
        {
            metadataVersion = ReadIl2CppMetadataVersion(game.Il2CppMetadataPath);
            File.Copy(game.Il2CppMetadataPath, Path.Combine(il2cppDir, "global-metadata.dat"), overwrite: true);
        }
        if (game.GameAssemblyPath != null)
            File.Copy(game.GameAssemblyPath, Path.Combine(il2cppDir, Path.GetFileName(game.GameAssemblyPath)), overwrite: true);

        var text = new StringBuilder()
            .AppendLine($"{game.DisplayName} was built with IL2CPP.")
            .AppendLine()
            .AppendLine("IL2CPP compiles the game's C# into native machine code (GameAssembly.dll), so the")
            .AppendLine("original C# method bodies no longer exist and cannot be decompiled back to C# directly.")
            .AppendLine()
            .AppendLine($"Metadata version: {(metadataVersion?.ToString() ?? "unknown / possibly encrypted")}")
            .AppendLine()
            .AppendLine("The two files needed by IL2CPP tools have been copied into this folder:")
            .AppendLine("  - GameAssembly.dll    (the native code)")
            .AppendLine("  - global-metadata.dat (class, method and field names)")
            .AppendLine()
            .AppendLine("To recover class/method names and signatures, run one of these on them:")
            .AppendLine("  - Cpp2IL       https://github.com/SamboyCoding/Cpp2IL")
            .AppendLine("  - Il2CppDumper https://github.com/Perfare/Il2CppDumper")
            .ToString();
        File.WriteAllText(Path.Combine(il2cppDir, "README.txt"), text);

        log("This game uses IL2CPP: its C# was compiled to native code and cannot be turned back into C# source.", LogLevel.Warning, null);
        log($"Copied GameAssembly + global-metadata.dat to {il2cppDir} (metadata v{metadataVersion?.ToString() ?? "?"}). See README.txt there.", LogLevel.Info, null);
    }

    public static int? ReadIl2CppMetadataVersion(string path)
    {
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            if (reader.BaseStream.Length < 8 || reader.ReadUInt32() != 0xFAB11BAF) return null;
            return reader.ReadInt32();
        }
        catch (IOException) { return null; }
    }

    private static void CopyStreamingAssets(UnityGame game, string outDir, long maxBytes, Action<string, LogLevel, double?> log)
    {
        var source = Path.Combine(game.DataDirectory!, "StreamingAssets");
        if (!Directory.Exists(source)) return;

        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToList();
        var size = files.Sum(f => new FileInfo(f).Length);
        if (size > maxBytes)
        {
            log($"StreamingAssets is {size / (1024 * 1024)} MB, skipping the copy.", LogLevel.Warning, null);
            return;
        }

        log("Copying StreamingAssets...", LogLevel.Info, null);
        var target = Path.Combine(outDir, "StreamingAssets");
        foreach (var file in files)
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static void WriteGameInfo(UnityGame game, string outDir, int ok, int failed, IReadOnlyList<SecretFinding> secrets)
    {
        var sb = new StringBuilder()
            .AppendLine($"Game:            {game.DisplayName}")
            .AppendLine($"Company:         {game.CompanyName ?? "unknown"}")
            .AppendLine($"Unity version:   {game.UnityVersion ?? "unknown"}")
            .AppendLine($"Backend:         {game.Backend}")
            .AppendLine($"Game folder:     {game.RootDirectory}")
            .AppendLine($"Data folder:     {game.DataDirectory ?? "-"}")
            .AppendLine($"Executable:      {game.ExecutablePath ?? "-"}");
        if (game.Backend == ScriptingBackend.Mono)
            sb.AppendLine($"Assemblies:      {ok} decompiled, {failed} failed");
        if (secrets.Count > 0)
            sb.AppendLine($"Secrets found:   {secrets.Count} (see SECURITY-SCAN.txt)");
        sb.AppendLine($"Decompiled on:   {DateTime.Now:yyyy-MM-dd HH:mm}");
        File.WriteAllText(Path.Combine(outDir, "GameInfo.txt"), sb.ToString());
    }

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).ToHashSet();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "Game" : cleaned;
    }

    /// <summary>IProgress that reports on the calling thread (Progress&lt;T&gt; would post asynchronously).</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
