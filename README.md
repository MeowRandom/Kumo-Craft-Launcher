# Kumo Craft Launcher / 库莫

Kumo is a Windows Minecraft: Java Edition launcher built with WPF and .NET.

Current stable version: `1.0`.

## Features

- Mojang release, snapshot, and historical version installation
- Automatic Java runtime selection and download
- Fabric, Quilt, Forge, NeoForge, OptiFine, and OptiFabric installation
- Per-instance world, Mod, and configuration isolation
- Microsoft and offline account sign-in
- Minecraft skin avatars, including Steve and Alex offline defaults
- Cached 3D skin and cape customization
- Modrinth and CurseForge catalogs with cover images and Chinese metadata
- Dependency-aware Mod installation
- PCL2, Modrinth, and CurseForge Modpack archive import
- Windows smooth wheel scrolling with native wheel-speed settings
- Light, dark, and system-following themes
- Automatic settings persistence
- Global Minecraft game options and complete keybinding sync across versions

## Run

The release build is a self-contained Windows x64 single executable.

1. Extract the release ZIP.
2. Run `Kumo Craft Launcher.exe`.
3. No administrator privileges are required.

Requirements:

- Windows 10 or newer, x64
- Microsoft Edge WebView2 Runtime
- Internet access for version, Mod, and account services

WebView2 is included with current Windows releases and Microsoft Edge. If 3D
skin previews cannot load, install the WebView2 Evergreen Runtime from
[Microsoft](https://developer.microsoft.com/microsoft-edge/webview2/).

The launcher uses the `.minecraft` directory next to its executable:

```text
<launcher directory>\.minecraft
```

If the executable is already inside a `.minecraft` directory, that directory is
used directly. A different parent directory can be selected in Settings; Kumo
creates and uses `.minecraft` inside it. Settings are saved automatically.

## Development

Source builds require the .NET 9 SDK. Java 17 or newer is required for the
smoke tests.

```powershell
.\build.ps1
```

The build script publishes a self-contained single executable to:

```text
publish\Kumo Craft Launcher.exe
```

For a regular development build:

```powershell
dotnet restore .\Kumo.csproj --configfile .\NuGet.Config
dotnet build .\Kumo.csproj -c Release --no-restore
```

## Test

```powershell
dotnet run --project .\tests\Kumo.SmokeTests\Kumo.SmokeTests.csproj `
  -c Release --configfile .\tests\Kumo.SmokeTests\NuGet.Config
```

## Accounts and privacy

Official Minecraft Java profile access requires Microsoft and Xbox Live
authentication. Cached appearance data is displayed locally so opening the
customization screen does not require a network request.

Microsoft refresh tokens are encrypted with Windows DPAPI. Short-lived
Minecraft access tokens are kept in memory and are not written to the settings
file as plaintext.

## Data sources

- Mojang version metadata, assets, Java runtimes, and session profiles
- BMCLAPI mirror for compatible Minecraft resources
- Modrinth project and version APIs
- CurseForge metadata through the public CFWidget API
- Public MC百科 pages for Chinese Mod titles and descriptions

Downloaded files are verified with source-provided hashes when available.

## License

MIT. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

Kumo is not affiliated with Mojang Studios, Microsoft, Modrinth, CurseForge,
or MC百科. Minecraft is a trademark of Mojang Studios.
