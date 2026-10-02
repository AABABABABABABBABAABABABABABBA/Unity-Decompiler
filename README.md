# Unity Decompiler

A one-click Windows app that turns a Unity game back into C# source code.

**Drag the game onto the window. That's it.** The decompiled scripts open in Explorer when it's done.

## Download

Get `UnityDecompiler.exe` from the [Releases](../../releases) page, or from the
**Actions** tab (latest run → *Artifacts* → `UnityDecompiler`).
It's a single self-contained `.exe`, so you don't need to install .NET or anything else.

## How to use

1. Run `UnityDecompiler.exe`.
2. Drag in any of these:
   - the game's `.exe` (e.g. `MyGame.exe`)
   - the game folder (the one with `MyGame_Data` in it)
   - a `.zip` of the game
   - the `MyGame_Data` folder, its `Managed` folder, or a single `Assembly-CSharp.dll`
3. Wait a few seconds. The output folder opens automatically.

You can also drag a game straight onto the `UnityDecompiler.exe` icon.

## What you get

By default results go to `Documents\Unity Decompiler\<Game name>` (you can change this with *Save to*):

```
<Game name>/
├── <Game>.sln                      ← open in Visual Studio / Rider
├── GameInfo.txt                    ← Unity version, company, backend...
├── Scripts/
│   └── Assembly-CSharp/            ← one C# project per game assembly
│       ├── Assembly-CSharp.csproj
│       └── <Namespace>/<Class>.cs
└── StreamingAssets/                ← copied as-is (skipped if over 512 MB)
```

Only the game's own code is decompiled by default (Assembly-CSharp, its asmdefs and
third-party plugins). Tick **Also decompile Unity / System DLLs** to include `UnityEngine.*`,
`System.*` etc.

## Mono vs IL2CPP

Unity builds games in one of two ways:

| Backend | How to tell | What this tool does |
|---------|-------------|---------------------|
| **Mono** | `MyGame_Data/Managed/Assembly-CSharp.dll` exists | Full C# source for every script. |
| **IL2CPP** | `GameAssembly.dll` + `MyGame_Data/il2cpp_data` exist | Code is compiled to native machine code, so C# **cannot** be recovered. The tool detects it, copies `GameAssembly.dll` and `global-metadata.dat` to an `IL2CPP` folder, and explains how to use [Cpp2IL](https://github.com/SamboyCoding/Cpp2IL) or [Il2CppDumper](https://github.com/Perfare/Il2CppDumper) on them to recover class and method names. |

Games with encrypted or obfuscated assemblies will decompile, but the output may be unreadable.

## Command-line version

`unity-decompiler.exe` does the same thing in a terminal (and works on Linux/macOS via `dotnet run`):

```
unity-decompiler "C:\Games\MyGame\MyGame.exe" -o C:\out\MyGame
unity-decompiler MyGame.zip --all        # include Unity/System DLLs
```

## Building from source

Requires the .NET 8 SDK.

```
dotnet publish src/UnityDecompiler.App -c Release -o publish   # → publish/UnityDecompiler.exe (Windows)
dotnet run --project src/UnityDecompiler.Cli -- <game>           # CLI, any OS
```

Decompilation is done by [ICSharpCode.Decompiler](https://github.com/icsharpcode/ILSpy) (the engine behind ILSpy).

## Please be respectful

Only decompile games you own or have permission to study, and follow the game's EULA and
the law where you live. Don't redistribute other people's code or assets.
