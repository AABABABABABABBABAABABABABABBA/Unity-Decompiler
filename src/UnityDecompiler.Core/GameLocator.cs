using System.Text;
using System.Text.RegularExpressions;

namespace UnityDecompiler.Core;

/// <summary>
/// Finds the Unity data folder (e.g. "MyGame_Data") from whatever the user dropped:
/// the game .exe, the game folder, the _Data folder, the Managed folder or a single .dll.
/// </summary>
public static class GameLocator
{
    private const int MaxSearchDepth = 4;

    private static readonly Regex UnityVersionRegex =
        new(@"\b(20\d{2}|[3-6])\.\d{1,2}\.\d{1,3}[abfpx]\d{1,3}\b", RegexOptions.Compiled);

    public static UnityGame Locate(string inputPath)
    {
        inputPath = Path.GetFullPath(inputPath.Trim().Trim('"'));

        if (File.Exists(inputPath))
        {
            var ext = Path.GetExtension(inputPath).ToLowerInvariant();
            if (ext == ".dll")
                return FromDll(inputPath);

            // Any other file (normally the game .exe): look next to it.
            var dir = Path.GetDirectoryName(inputPath)!;
            var exeName = Path.GetFileNameWithoutExtension(inputPath);
            return FromDirectory(dir, exeName, ext == ".exe" ? inputPath : null);
        }

        if (Directory.Exists(inputPath))
            return FromDirectory(inputPath, null, null);

        throw new UnityGameNotFoundException($"Could not find \"{inputPath}\".");
    }

    private static UnityGame FromDll(string dllPath)
    {
        var dir = Path.GetDirectoryName(dllPath)!;
        // Dropping Assembly-CSharp.dll from inside a game: treat it as the whole game.
        if (string.Equals(Path.GetFileName(dir), "Managed", StringComparison.OrdinalIgnoreCase))
        {
            try { return FromDirectory(dir, null, null); }
            catch (UnityGameNotFoundException) { /* fall through to loose mode */ }
        }

        return new UnityGame
        {
            Name = Path.GetFileNameWithoutExtension(dllPath),
            RootDirectory = dir,
            ManagedDirectory = dir,
            Backend = ScriptingBackend.Mono,
            LooseAssemblies = new[] { dllPath },
        };
    }

    private static UnityGame FromDirectory(string dir, string? preferredName, string? exePath)
    {
        var dataDir = FindDataDirectory(dir, preferredName)
            ?? throw new UnityGameNotFoundException(
                "This does not look like a Unity game: no \"<Game>_Data\" folder (with Managed, il2cpp_data or globalgamemanagers) was found.");

        // Root = the folder that holds "<Game>_Data" (on macOS: the folder holding the .app).
        var root = Path.GetDirectoryName(dataDir)!;
        var dataName = Path.GetFileName(dataDir);
        string name;
        if (dataName.EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
            name = dataName[..^"_Data".Length];
        else if (preferredName != null)
            name = preferredName;
        else
            name = FindAppBundleName(dataDir) ?? Path.GetFileName(root);

        exePath ??= FirstExisting(Path.Combine(root, name + ".exe"), Path.Combine(root, name + ".x86_64"));

        var managed = Path.Combine(dataDir, "Managed");
        if (!Directory.Exists(managed)) managed = null!;

        var metadata = Path.Combine(dataDir, "il2cpp_data", "Metadata", "global-metadata.dat");
        var gameAssembly = FirstExisting(
            Path.Combine(root, "GameAssembly.dll"),
            Path.Combine(root, "GameAssembly.so"),
            Path.Combine(root, "GameAssembly.dylib"));

        var backend = File.Exists(metadata) || gameAssembly != null
            ? ScriptingBackend.IL2CPP
            : managed != null && Directory.EnumerateFiles(managed, "*.dll").Any()
                ? ScriptingBackend.Mono
                : ScriptingBackend.Unknown;

        var (company, product) = ReadAppInfo(dataDir);

        return new UnityGame
        {
            Name = name,
            RootDirectory = root,
            DataDirectory = dataDir,
            ExecutablePath = exePath,
            ManagedDirectory = managed,
            UnityVersion = DetectUnityVersion(dataDir, root),
            CompanyName = company,
            ProductName = product,
            Il2CppMetadataPath = File.Exists(metadata) ? metadata : null,
            GameAssemblyPath = gameAssembly,
            Backend = backend,
        };
    }

    /// <summary>Breadth-first search for a folder that looks like a Unity data folder.</summary>
    private static string? FindDataDirectory(string start, string? preferredName)
    {
        // The user may have dropped the _Data folder itself, or its Managed subfolder.
        if (IsDataDirectory(start)) return start;
        var parent = Path.GetDirectoryName(start);
        if (parent != null && string.Equals(Path.GetFileName(start), "Managed", StringComparison.OrdinalIgnoreCase)
            && IsDataDirectory(parent))
            return parent;

        if (preferredName != null)
        {
            var preferred = Path.Combine(start, preferredName + "_Data");
            if (IsDataDirectory(preferred)) return preferred;
        }

        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((start, 0));
        var found = new List<string>();
        while (queue.Count > 0 && found.Count == 0)
        {
            var levelDepth = queue.Peek().Depth;
            while (queue.Count > 0 && queue.Peek().Depth == levelDepth)
            {
                var (dir, depth) = queue.Dequeue();
                IEnumerable<string> children;
                try { children = Directory.EnumerateDirectories(dir).ToList(); }
                catch (Exception e) when (e is UnauthorizedAccessException or IOException) { continue; }

                foreach (var child in children)
                {
                    if (IsDataDirectory(child)) found.Add(child);
                    else if (depth + 1 < MaxSearchDepth) queue.Enqueue((child, depth + 1));
                }
            }
        }

        // Prefer "<Name>_Data" folders, then the one with the most to decompile.
        return found
            .OrderByDescending(d => d.EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(d => Directory.Exists(Path.Combine(d, "Managed")))
            .FirstOrDefault();
    }

    private static bool IsDataDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return false;
        return File.Exists(Path.Combine(dir, "Managed", "Assembly-CSharp.dll"))
            || File.Exists(Path.Combine(dir, "il2cpp_data", "Metadata", "global-metadata.dat"))
            || File.Exists(Path.Combine(dir, "globalgamemanagers"))
            || File.Exists(Path.Combine(dir, "data.unity3d"))
            || File.Exists(Path.Combine(dir, "mainData"))
            || (dir.EndsWith("_Data", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(dir, "Managed")));
    }

    private static string? FindAppBundleName(string dataDir)
    {
        // macOS: Foo.app/Contents/Resources/Data
        for (var d = new DirectoryInfo(dataDir); d != null; d = d.Parent)
            if (d.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return d.Name[..^4];
        return null;
    }

    private static (string? Company, string? Product) ReadAppInfo(string dataDir)
    {
        var path = Path.Combine(dataDir, "app.info");
        if (!File.Exists(path)) return (null, null);
        try
        {
            var lines = File.ReadAllLines(path);
            return (lines.ElementAtOrDefault(0)?.Trim(), lines.ElementAtOrDefault(1)?.Trim());
        }
        catch (IOException) { return (null, null); }
    }

    private static string? DetectUnityVersion(string dataDir, string root)
    {
        // The version string sits in the header of these files.
        foreach (var file in new[] { "globalgamemanagers", "data.unity3d", "mainData", "level0", "unity default resources" })
        {
            var path = Path.Combine(dataDir, file);
            if (!File.Exists(path)) continue;
            try
            {
                using var fs = File.OpenRead(path);
                var buffer = new byte[4096];
                var read = fs.Read(buffer, 0, buffer.Length);
                var text = Encoding.ASCII.GetString(buffer, 0, read);
                var match = UnityVersionRegex.Match(text);
                if (match.Success) return match.Value;
            }
            catch (IOException) { }
        }

        var player = Path.Combine(root, "UnityPlayer.dll");
        if (File.Exists(player))
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(player);
            if (!string.IsNullOrWhiteSpace(info.ProductVersion)) return info.ProductVersion;
        }
        return null;
    }

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);
}

public sealed class UnityGameNotFoundException(string message) : Exception(message);
