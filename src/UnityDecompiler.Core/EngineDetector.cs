namespace UnityDecompiler.Core;

/// <summary>
/// When a dropped game is not a Unity game, works out which engine it actually is
/// so the tool can say something useful instead of a generic "not Unity" error.
/// </summary>
public static class EngineDetector
{
    public sealed record EngineGuess(string Engine, string Explanation);

    public static EngineGuess? Identify(string path)
    {
        try
        {
            var dir = File.Exists(path) ? Path.GetDirectoryName(path)! : path;
            if (!Directory.Exists(dir)) return null;

            // Walk a few levels so the key files (often nested under <Game>/Content/Paks) are seen,
            // capped so a huge install is not fully traversed.
            var (files, dirs) = SafeEnumerate(dir, 5);
            var names = files.Select(f => Path.GetFileName(f)).ToList();
            var lowerNames = names.Select(n => n.ToLowerInvariant()).ToHashSet();
            var dirPaths = dirs.Select(d => d.Replace('\\', '/')).ToList();

            bool Any(Func<string, bool> predicate) => names.Any(predicate);
            bool DirEndsWith(string suffix) => dirPaths.Any(d => d.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                                               || dirPaths.Any(d => d.Contains(suffix + "/", StringComparison.OrdinalIgnoreCase));

            // Unreal Engine: Content/Paks (+ .pak), Engine/Binaries, <Game>/Binaries/Win64.
            if (Any(n => n.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                || lowerNames.Contains("unrealengine.dll")
                || DirEndsWith("/Content/Paks")
                || DirEndsWith("/Engine/Binaries")
                || DirEndsWith("/Binaries/Win64"))
            {
                return new EngineGuess("Unreal Engine",
                    "This is an Unreal Engine game (it has .pak files / an Engine or Binaries folder). Unreal games aren't Unity and " +
                    "can't be decompiled to C#. Their logic is in native C++ plus Blueprints; tools like FModel, UE Viewer (umodel) " +
                    "and UnrealPak can unpack their assets instead.");
            }

            // Godot: .pck next to the exe, or a packed exe with project.godot.
            if (lowerNames.Any(n => n.EndsWith(".pck")) || lowerNames.Contains("project.godot"))
            {
                return new EngineGuess("Godot",
                    "This is a Godot game (.pck data file). It isn't Unity. Use Godot RE Tools (gdsdecomp) to extract its " +
                    "scenes and GDScript.");
            }

            // GameMaker: data.win / game.unx / a .yydebug.
            if (lowerNames.Contains("data.win") || lowerNames.Contains("game.unx") || Any(n => n.EndsWith(".yydebug", StringComparison.OrdinalIgnoreCase)))
            {
                return new EngineGuess("GameMaker",
                    "This is a GameMaker game (data.win). It isn't Unity. Use UndertaleModTool to open and edit it.");
            }

            // Electron / web apps: resources/app.asar.
            if (lowerNames.Contains("app.asar") || files.Any(f => f.Replace('\\', '/').EndsWith("/resources/app.asar", StringComparison.OrdinalIgnoreCase)))
            {
                return new EngineGuess("Electron",
                    "This is an Electron (web) app (app.asar). It isn't Unity. Use `npx asar extract app.asar out` to unpack it.");
            }

            // RPG Maker: www/data/*.json, Game.rpgproject.
            if (Any(n => n.EndsWith(".rpgproject", StringComparison.OrdinalIgnoreCase))
                || files.Any(f => f.Replace('\\', '/').Contains("/www/data/", StringComparison.OrdinalIgnoreCase)))
            {
                return new EngineGuess("RPG Maker",
                    "This is an RPG Maker game. It isn't Unity. Its scripts/data live in the www or data folder as JavaScript/JSON.");
            }

            // Source engine: gameinfo.txt + *.vpk.
            if (lowerNames.Contains("gameinfo.txt") || Any(n => n.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase)))
            {
                return new EngineGuess("Source Engine",
                    "This is a Source engine game. It isn't Unity. Use GCFScape / the Source SDK to open its .vpk content.");
            }
        }
        catch (Exception) { /* detection is best-effort */ }
        return null;
    }

    private static (List<string> Files, List<string> Dirs) SafeEnumerate(string root, int maxDepth)
    {
        var files = new List<string>();
        var dirs = new List<string>();
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && files.Count < 40000 && dirs.Count < 40000)
        {
            var (dir, depth) = queue.Dequeue();
            try
            {
                files.AddRange(Directory.EnumerateFiles(dir));
                if (depth < maxDepth)
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                    {
                        dirs.Add(sub);
                        queue.Enqueue((sub, depth + 1));
                    }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException) { }
        }
        return (files, dirs);
    }
}
