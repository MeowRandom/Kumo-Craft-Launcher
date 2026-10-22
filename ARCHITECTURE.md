# Architecture

Kumo uses a small set of focused services behind a WPF shell.

## Application layers

- `Models/` contains serializable settings, version metadata, account state, and Mod search results.
- `Services/` owns network access, installation, authentication, Java discovery, launch argument construction, and cover caching.
- `MainWindow.xaml` defines the application shell and page templates.
- `MainWindow.xaml.cs` coordinates UI events and service calls.
- `Themes/` contains the light and dark theme dictionaries plus reusable WPF styles.

## Data layout

```text
%APPDATA%\Kumo\
  launcher.json

<launcher directory>\
  .minecraft\
    assets\
    libraries\
    versions\
    runtime\
    cache\minecraft-profiles\
    cache\appearance-previews-v1\
    cache\covers-v2\
```

With version isolation enabled, each game profile stores its worlds, Mods, and
configuration directly inside its standard `versions/<version-id>/` directory.
With isolation disabled, all profiles use the `.minecraft` root.

## External APIs

- Mojang version and asset metadata
- Mojang Java runtimes
- BMCLAPI mirror for supported Minecraft resources
- Microsoft device authorization and Xbox Live services
- Modrinth search, project, and version APIs
- CurseForge project and file metadata through the public CFWidget API
- Public MC百科 search and item pages for Chinese Mod titles and descriptions
- Mojang session profiles for player skin textures
- Mojang Java Edition client resources for the official Steve and Alex defaults

Downloaded content is verified with the hashes supplied by its source whenever
available.
