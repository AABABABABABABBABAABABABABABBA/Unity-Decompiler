# Unity Decompiler

Turns a Unity game back into C# source code. Run it as a **one-click Windows app**, from the
**command line**, or as a **web server** on a VPS where people just upload a game in their browser.

**Drag the game onto the window. That's it.** The decompiled scripts open in Explorer when it's done.

Every run also **scans the decompiled code for hardcoded credentials** (API keys, tokens, passwords,
connection strings) and writes the findings to `SECURITY-SCAN.txt`, so you can tell at a glance whether
the developers left secrets in the build.

## Three ways to run it

| | For | Download |
|---|---|---|
| **Desktop app** (`UnityDecompiler.exe`) | Windows, drag-and-drop | Releases / Actions artifact `UnityDecompiler` |
| **Command line** (`unity-decompiler`) | scripts, any OS | Actions artifact `unity-decompiler-cli`, or `dotnet run` |
| **Web server** | a VPS / dedicated server; others upload via browser | Docker, or artifact `unity-decompiler-server-linux-x64` |

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
├── SECURITY-SCAN.txt              ← hardcoded credentials found in the code (if any)
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

### Not a Unity game?

If you point it at an **Unreal** (e.g. Tower Unite), **Godot**, **GameMaker**, **Electron**, **RPG Maker**
or **Source** game, it recognises the engine and tells you so — plus which tool to use instead — rather
than giving a confusing error. Only Unity games can be decompiled to C# here.

## Credential / secret scan

After decompiling, the tool scans the C# and any text config/asset files for secrets that developers
sometimes hardcode into a build: cloud API keys (AWS, Google, Firebase), service tokens (Discord, Slack,
GitHub, Stripe, SendGrid, OpenAI), JWTs, private keys and database connection strings. Results go in
`SECURITY-SCAN.txt`, with each secret **partly redacted** so the report itself isn't a new leak.

This is a defensive audit for games you own or are authorised to test. If it flags a real secret, treat it
as compromised and rotate it at the provider.

## Web server (run it on a VPS / dedicated server)

Others open it in a browser, upload a game `.zip`, and download the decompiled source — the same engine,
no install on their side.

**With Docker (easiest):**
```
docker compose up -d        # then open http://your-server:8080
```
or
```
docker build -t unity-decompiler .
docker run -d -p 8080:8080 -v ud-data:/data unity-decompiler
```

**Without Docker:** grab `unity-decompiler-server-linux-x64` from the Actions artifacts / a release, then:
```
DATA_DIR=/var/ud ASPNETCORE_URLS=http://0.0.0.0:8080 ./UnityDecompiler.Server
```

**Settings (environment variables, all optional):**

| Variable | Default | What it does |
|----------|---------|--------------|
| `ACCESS_PASSWORD` | *(none)* | Require a password. Leave unset to keep it open on a trusted network. |
| `MAX_UPLOAD_MB` | `2048` | Largest upload accepted. |
| `RETENTION_HOURS` | `6` | How long results are kept before they're deleted. |
| `COPY_STREAMING_ASSETS` | `false` | Include the game's StreamingAssets in the download. |
| `MAX_ACTIVE_JOBS` | `20` | Reject new uploads while this many are queued/running. |
| `DATA_DIR` | `/data` | Where uploads and results live. |

Uploads and results are deleted automatically after `RETENTION_HOURS`. Put it behind a reverse proxy
(Caddy/nginx) for HTTPS, and set `ACCESS_PASSWORD` if it's reachable from the internet.

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
dotnet run --project src/UnityDecompiler.Server                  # web server on http://localhost:5000
```

The projects: `UnityDecompiler.Core` (the engine), `.App` (Windows GUI), `.Cli` (terminal), `.Server` (web/VPS).

Decompilation is done by [ICSharpCode.Decompiler](https://github.com/icsharpcode/ILSpy) (the engine behind ILSpy).

## Please be respectful

Only decompile games you own or have permission to study, and follow the game's EULA and
the law where you live. Don't redistribute other people's code or assets.
