// End-to-end and protocol smoke tests for Kumo.
using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kumo.Converters;
using Kumo.Models;
using Kumo.Services;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("Java version parsing", () =>
    {
        Assert(JavaService.ParseMajorVersion("openjdk version \"21.0.2\" 2024-01-16") == 21, "Java 21");
        Assert(JavaService.ParseMajorVersion("java version \"1.8.0_411\"") == 8, "Java 8");
        return Task.CompletedTask;
    }),
    ("Fabric Java compatibility", () =>
    {
        var method = typeof(VersionInstaller).GetMethod(
            "NeedsFabricUpgradeForJava",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static)!;
        Assert((bool)method.Invoke(null, ["0.15.3", 25])!,
            "Fabric 0.15.3 cannot read Java 25 class files");
        Assert(!(bool)method.Invoke(null, ["0.16.14", 25])!,
            "Fabric 0.16.14 supports Java 25");
        Assert((bool)method.Invoke(null, ["0.16.14", 26])!,
            "Java 26 requires Fabric 0.17.3 or newer");
        Assert(!(bool)method.Invoke(null, ["0.19.5", 25])!,
            "Current stable Fabric must remain valid");
        Assert(!(bool)method.Invoke(null, ["0.15.3", 21])!,
            "Java 21 does not require the Java 25 loader floor");
        return Task.CompletedTask;
    }),
    ("Minecraft artwork version mapping", () =>
    {
        var catalog = JsonNode.Parse("""
            {"entries":[
              {"version":"26.3","image":{"url":"/v2/images/dappledcamp540x540.jpg"}},
              {"version":"1.21.1","image":{"url":"/v2/images/tricky-trials.jpg"}}
            ]}
            """) as JsonObject;
        var method = typeof(MinecraftArtworkService).GetMethod(
            "SelectImageUrl",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static)!;
        Assert((string?)method.Invoke(null, [catalog, "26.3"]) ==
               "/v2/images/dappledcamp540x540.jpg",
            "Release artwork must map by exact Minecraft version");
        Assert((string?)method.Invoke(null, [catalog, "1.21.1"]) ==
               "/v2/images/tricky-trials.jpg",
            "Older release artwork must map by exact Minecraft version");
        Assert(method.Invoke(null, [catalog, "missing"]) is null,
            "Unknown versions must not borrow another release image");
        return Task.CompletedTask;
    }),
    ("Hero artwork aspect ratio", () =>
    {
        var method = typeof(Kumo.MainWindow).GetMethod(
            "GetHeroHeightForAspect",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static)!;
        var sixteenByNine = (double)method.Invoke(null, [1600d, 16d / 9d])!;
        Assert(Math.Abs(sixteenByNine - 900) < 0.01,
            "A 16:9 hero image must scale to a 16:9 card");
        var fallback = (double)method.Invoke(null, [0d, 0d])!;
        Assert(fallback == 330, "Missing artwork must keep the original card height");
        return Task.CompletedTask;
    }),
    ("Classifier-only library has no generic artifact", () =>
    {
        var method = typeof(VersionInstaller).GetMethod(
            "ShouldUseMavenArtifactFallback",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static)!;
        var classifierOnly = JsonNode.Parse("""
            {"name":"net.java.jinput:jinput-platform:2.0.5",
             "downloads":{"classifiers":{"natives-windows":{"path":"natives.jar"}}}}
            """) as JsonObject;
        var mavenOnly = JsonNode.Parse("""
            {"name":"example:library:1.0"}
            """) as JsonObject;
        Assert(!(bool)method.Invoke(null, [classifierOnly])!,
            "A classifier-only library must not request a missing base jar");
        Assert((bool)method.Invoke(null, [mavenOnly])!,
            "Libraries declared only by Maven coordinates still need a fallback");
        return Task.CompletedTask;
    }),
    ("Offline UUID", () =>
    {
        var uuid = GameLauncher.CreateOfflineUuid("Kumo");
        Assert(uuid.Length == 32 && uuid.All(Uri.IsHexDigit), "UUID must be 32 hex characters");
        Assert(uuid == GameLauncher.CreateOfflineUuid("Kumo"), "UUID must be deterministic");
        Assert(uuid != GameLauncher.CreateOfflineUuid("OtherPlayer"), "Names must differ");
        return Task.CompletedTask;
    }),
    ("Home launch button without versions", () =>
    {
        var method = typeof(Kumo.MainWindow).GetMethod(
            "GetLaunchActionLabel",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static)!;
        Assert((string)method.Invoke(null, [false, false, false, false])! == "下载游戏",
            "A missing local version must expose the download action");
        Assert((string)method.Invoke(null, [true, true, false, false])! == "启动游戏",
            "An installed selected version must expose the launch action");
        Assert((string)method.Invoke(null, [false, true, false, false])! == "启动游戏",
            "Existing local versions must restore the launch action");
        return Task.CompletedTask;
    }),
    ("Theme mode resolution", () =>
    {
        Assert(ThemeService.ResolveDark(LauncherThemeMode.Dark), "Dark mode must resolve to dark");
        Assert(!ThemeService.ResolveDark(LauncherThemeMode.Light), "Light mode must resolve to light");
        return Task.CompletedTask;
    }),
    ("Application resources initialize", () =>
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Kumo.App();
                app.InitializeComponent();
                var styles = app.Resources.MergedDictionaries.First(dictionary =>
                    dictionary.Source?.OriginalString.EndsWith(
                        "Themes/Styles.xaml", StringComparison.OrdinalIgnoreCase) == true);
                Assert(styles.Contains("LoaderIconConverter"), "Loader icon converter resource is missing");
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new InvalidOperationException($"Application resource load failed: {failure.Message}", failure);
        return Task.CompletedTask;
    }),
    ("Loader detection and artwork", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "kumo-loader-" + Guid.NewGuid().ToString("N"));
        try
        {
            var profiles = new VersionProfileService(root, new MinecraftService(new HttpService(), root));
            Assert(profiles.DetectLoader("fabric-loader-0.16.9-1.21.1").Loader == ModLoader.Fabric,
                "Fabric loader detection");
            Assert(profiles.DetectLoader("1.20.1-forge-47.4.0").Loader == ModLoader.Forge,
                "Forge loader detection");
            Assert(profiles.DetectLoader("neoforge-21.1.80").Loader == ModLoader.NeoForge,
                "NeoForge loader detection");
            Assert(profiles.DetectLoader("quilt-loader-0.27.1-1.21.1").Loader == ModLoader.Quilt,
                "Quilt loader detection");
            Assert(profiles.DetectLoader("1.21.1-OptiFine_HD_U_J1").Loader == ModLoader.OptiFine,
                "OptiFine loader detection");
            using var bridge = typeof(VersionInstaller).Assembly.GetManifestResourceStream(
                "Kumo.Assets.OptiFineInstallerBridge.class");
            Assert(bridge is { Length: > 0 }, "OptiFine installer bridge resource");
            Assert(VersionInstaller.IsNativeArtifactCompatible(
                    "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-windows.jar"),
                "Current Windows native artifact");
            Assert(!VersionInstaller.IsNativeArtifactCompatible(
                    "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-windows-x86.jar"),
                "32-bit native must not enter x64 classpath");
            Assert(!VersionInstaller.IsNativeArtifactCompatible(
                    "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-windows-arm64.jar"),
                "ARM64 native must not enter x64 classpath");

            var forge = new InstalledVersionInfo { Id = "forge", Loader = ModLoader.Forge };
            var neoForge = new InstalledVersionInfo { Id = "neoforge", Loader = ModLoader.NeoForge };
            Assert(forge.HasLoaderIcon, "Forge must expose an icon");
            Assert(neoForge.HasLoaderIcon, "NeoForge must expose an icon");
            Assert(forge.SupportsMods && neoForge.SupportsMods,
                "Forge and NeoForge instances must support Modrinth installation");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return Task.CompletedTask;
    }),
    ("Unsupported loader versions treated as empty", () =>
    {
        var method = typeof(ModLoaderCatalogService).GetMethod(
            "IsNoLoaderVersionsStatus",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static)!;
        Assert((bool)method.Invoke(null, [HttpStatusCode.NotFound])!,
            "Unknown Minecraft versions may return HTTP 404");
        Assert((bool)method.Invoke(null, [HttpStatusCode.BadRequest])!,
            "Loader metadata may reject unsupported Minecraft versions with HTTP 400");
        Assert(!(bool)method.Invoke(null, [HttpStatusCode.InternalServerError])!,
            "Server errors must remain visible");
        Assert(!(bool)method.Invoke(null, [null])!,
            "Missing status codes must remain errors");
        return Task.CompletedTask;
    }),
    ("Loader icon resources", () =>
    {
        Exception? failure = null;
        var loaded = 0;
        var thread = new Thread(() =>
        {
            try
            {
                var converter = new LoaderIconConverter();
                foreach (var loader in new[]
                         {
                             ModLoader.Vanilla, ModLoader.Fabric, ModLoader.Forge,
                             ModLoader.NeoForge, ModLoader.Quilt
                         })
                {
                    var version = new InstalledVersionInfo { Id = loader.ToString(), Loader = loader };
                    Assert(version.HasLoaderIcon, $"{loader} must enable its embedded icon");
                    var bitmap = converter.Convert(
                        version,
                        typeof(System.Windows.Media.ImageSource),
                        null,
                        CultureInfo.InvariantCulture) as System.Windows.Media.Imaging.BitmapImage;
                    if (bitmap is null) throw new InvalidOperationException($"{loader} loader icon must load");
                    Assert(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0,
                        $"{loader} loader icon must decode");
                    loaded++;
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new InvalidOperationException($"Loader icon resource failed: {failure.Message}", failure);
        Assert(loaded == 5, "Vanilla and loader icon resources must be embedded");
        return Task.CompletedTask;
    }),
    ("Loader install keeps one self-contained version", (Func<Task>)(async () =>
    {
        var root = Path.Combine(
            Path.GetTempPath(), "kumo-loader-dependency-" + Guid.NewGuid().ToString("N"));
        var versions = Path.Combine(root, "versions");
        var vanillaDirectory = Path.Combine(versions, "1.21.1");
        var fabricDirectory = Path.Combine(versions, "fabric-loader-0.16.9-1.21.1");
        Directory.CreateDirectory(vanillaDirectory);
        Directory.CreateDirectory(fabricDirectory);
        var vanillaPath = Path.Combine(vanillaDirectory, "1.21.1.json");
        File.WriteAllText(vanillaPath, """
            {"id":"1.21.1","type":"release","mainClass":"net.minecraft.client.main.Main",
             "libraries":[]}
            """);
        File.WriteAllBytes(Path.Combine(vanillaDirectory, "1.21.1.jar"), [0x4B, 0x55, 0x4D, 0x4F]);
        File.WriteAllText(
            Path.Combine(fabricDirectory, "fabric-loader-0.16.9-1.21.1.json"),
            """
            {"id":"fabric-loader-0.16.9-1.21.1","inheritsFrom":"1.21.1",
             "mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient","libraries":[]}
            """);
        try
        {
            var http = new HttpService();
            var minecraft = new MinecraftService(http, root);
            var service = new VersionProfileService(root, minecraft);
            var installer = new VersionInstaller(http, minecraft, service, root);
            Assert(service.HasInheritedDependents("1.21.1"),
                "A profile using inheritsFrom must count as a hard dependency");
            await installer.NormalizeLoaderVersionsAsync(CancellationToken.None);
            var installed = service.GetInstalledVersions();
            Assert(installed.Count == 1 && installed[0].SupportsMods,
                "A loader install must leave one visible self-contained version");
            Assert(installed[0].BaseVersion == "1.21.1",
                "The loader must retain its real Minecraft base version");
            Assert(!Directory.Exists(vanillaDirectory),
                "The temporary vanilla version directory must leave versions");
            var loaderJson = JsonNode.Parse(File.ReadAllText(
                Path.Combine(fabricDirectory, "fabric-loader-0.16.9-1.21.1.json"))) as JsonObject;
            Assert(loaderJson?["inheritsFrom"] is null,
                "The loader profile must be self-contained");
            Assert(loaderJson?["kumoBaseVersion"]?.ToString() == "1.21.1",
                "The loader profile must retain base-version metadata");
            Assert(File.Exists(Path.Combine(fabricDirectory,
                    "fabric-loader-0.16.9-1.21.1.jar")),
                "The loader version must contain the merged client JAR");
            Assert(Directory.Exists(Path.Combine(
                    root, "cache", "version-dependencies", "1.21.1")),
                "The temporary vanilla files should be preserved in the dependency cache");
            Assert(!service.HasInheritedDependents("1.21.1"),
                "A self-contained loader version must not block vanilla deletion");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        await Task.CompletedTask;
    })),
    ("One Minecraft base supports multiple local variants", () =>
    {
        Assert(new InstalledVersionInfo
        {
            Id = "333",
            DisplayName = "333",
            BaseVersion = "1.21",
            Loader = ModLoader.Fabric
        }.FilterDisplayLabel == "333 -Fabric 1.21",
            "Resource filters must append loader and game version to custom names");
        Assert(new InstalledVersionInfo
        {
            Id = "1.21",
            DisplayName = "1.21",
            BaseVersion = "1.21",
            Loader = ModLoader.Vanilla,
            FilterTypeLabel = "正式版"
        }.FilterDisplayLabel == "1.21",
            "Remote catalog filter options must not append synthetic loader details");
        var remote = new RemoteVersion
        {
            Id = "1.21.1",
            Type = "release",
            Url = "https://example.invalid/version.json",
            ReleaseTime = DateTimeOffset.UtcNow
        };
        remote.SetInstallationCount(3);
        Assert(remote.IsInstalled, "A version family with local variants must be marked installed");
        Assert(remote.ActionLabel == "添加版本", "Installed version action label");
        Assert(remote.InstalledBadgeLabel == "已安装 3 个版本",
            "Installed version badge must expose multiple local variants");
        remote.SetInstallationCount(0);
        Assert(!remote.IsInstalled && remote.ActionLabel == "下载",
            "Removing every local variant must restore the download action");

        var familyMethod = typeof(Kumo.MainWindow).GetMethod(
            "BuildVersionFamilyCounts",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static)!;
        var counts = (Dictionary<string, int>)familyMethod.Invoke(null,
        [
            new List<InstalledVersionInfo>
            {
                new()
                {
                    Id = "1.21.1",
                    BaseVersion = "1.21.1",
                    Loader = ModLoader.Vanilla
                },
                new()
                {
                    Id = "fabric-loader-0.19.5-1.21.1",
                    BaseVersion = "1.21.1",
                    Loader = ModLoader.Fabric
                },
                new()
                {
                    Id = "neoforge-21.1.253",
                    BaseVersion = "1.21.1",
                    Loader = ModLoader.NeoForge
                }
            }
        ])!;
        Assert(counts["1.21.1"] == 3,
            "Vanilla plus two loaders must count as three versions for the base");
        return Task.CompletedTask;
    }),
    ("Named installs create independent instances", (Func<Task>)(async () =>
    {
        var root = Path.Combine(
            Path.GetTempPath(), "kumo-named-instance-" + Guid.NewGuid().ToString("N"));
        var sourceId = "fabric-loader-0.19.5-1.21.1";
        var sourceDirectory = Path.Combine(root, "versions", sourceId);
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "mods"));
        File.WriteAllText(Path.Combine(sourceDirectory, sourceId + ".json"),
            $$"""
              {"id":"{{sourceId}}","kumoBaseVersion":"1.21.1","mainClass":"test.Main"}
              """);
        File.WriteAllBytes(Path.Combine(sourceDirectory, sourceId + ".jar"), [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(sourceDirectory, "mods", "example.jar"), "mod");
        try
        {
            var service = new VersionProfileService(
                root, new MinecraftService(new HttpService(), root));
            Assert(VersionProfileService.TryNormalizeInstanceId(
                    "生存服 Fabric", out var normalized, out _) &&
                   normalized == "生存服 Fabric",
                "Chinese instance names must be preserved");
            Assert(!VersionProfileService.TryNormalizeInstanceId(
                    "bad/name", out _, out _),
                "Directory separators must be rejected");
            Assert(!VersionProfileService.TryNormalizeInstanceId(
                    "CON", out _, out _),
                "Windows reserved names must be rejected");

            var source = new InstalledVersionInfo
            {
                Id = sourceId,
                BaseVersion = "1.21.1",
                Loader = ModLoader.Fabric,
                LoaderVersion = "0.19.5",
                Ready = true
            };
            var copied = await service.MaterializeInstanceAsync(
                source, "生存服 Fabric", copySource: true, CancellationToken.None);
            Assert(copied.Id == "生存服 Fabric" &&
                   copied.LoaderVersion == "0.19.5" &&
                   Directory.Exists(sourceDirectory),
                "A custom copy must leave the original installation available");
            var moved = await service.MaterializeInstanceAsync(
                source, "测试服", copySource: false, CancellationToken.None);
            Assert(moved.Id == "测试服" &&
                   !Directory.Exists(sourceDirectory) &&
                   Directory.Exists(Path.Combine(root, "versions", "测试服")),
                "A newly installed loader can be moved to its custom instance id");

            var copiedJson = JsonNode.Parse(File.ReadAllText(
                Path.Combine(root, "versions", "生存服 Fabric", "生存服 Fabric.json"))) as JsonObject;
            Assert(copiedJson?["id"]?.ToString() == "生存服 Fabric" &&
                   copiedJson?["kumoBaseVersion"]?.ToString() == "1.21.1" &&
                   copiedJson?["kumoDisplayName"]?.ToString() == "生存服 Fabric",
                "Independent instance metadata must use the custom name and base version");
            Assert(File.Exists(Path.Combine(
                    root, "versions", "生存服 Fabric", "生存服 Fabric.jar")) &&
                   File.Exists(Path.Combine(
                    root, "versions", "生存服 Fabric", "mods", "example.jar")),
                "Custom instance must retain the client JAR and game files");
            Assert(service.GetInstanceIds().SetEquals(["生存服 Fabric", "测试服"]),
                "Both custom instance directories must be discoverable");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    })),
    ("Installed loader version matching", () =>
    {
        var root = Path.Combine(
            Path.GetTempPath(), "kumo-loader-version-" + Guid.NewGuid().ToString("N"));
        var versionsRoot = Path.Combine(root, "versions");
        void WriteProfile(string directory, string json)
        {
            var path = Path.Combine(versionsRoot, directory);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, directory + ".json"), json);
        }
        WriteProfile("Fabric 生存", """
            {"id":"Fabric 生存","kumoBaseVersion":"26.3",
             "mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient",
             "libraries":[{"name":"net.fabricmc:fabric-loader:0.19.5"}]}
            """);
        WriteProfile("Forge 生存", """
            {"id":"Forge 生存","kumoBaseVersion":"26.3",
             "mainClass":"net.minecraftforge.bootstrap.ForgeBootstrap",
             "libraries":[{"name":"net.minecraftforge:forge:26.3-66.0.13:universal"}]}
            """);
        WriteProfile("Neo 生存", """
            {"id":"Neo 生存","kumoBaseVersion":"26.3",
             "kumoLoaderVersion":"26.3.0.95",
             "mainClass":"net.neoforged.fml.startup.Client","libraries":[]}
            """);
        WriteProfile("The Winter Rescue", """
            {"id":"The Winter Rescue",
             "mainClass":"cpw.mods.bootstraplauncher.BootstrapLauncher",
             "libraries":[{"name":"net.minecraftforge:fmlloader:1.20.1-47.4.0"}]}
            """);
        try
        {
            var service = new VersionProfileService(
                root, new MinecraftService(new HttpService(), root));
            var installed = service.GetInstalledVersions();
            Assert(installed.Single(item => item.Id == "Fabric 生存").LoaderVersion == "0.19.5",
                "Fabric loader version must be inferred from libraries");
            Assert(installed.Single(item => item.Id == "Forge 生存").LoaderVersion == "66.0.13",
                "Forge loader version must be inferred from its Maven coordinate");
            Assert(installed.Single(item => item.Id == "Neo 生存").LoaderVersion == "26.3.0.95",
                "NeoForge loader version must be read from saved metadata");
            var legacyForgeModpack = installed.Single(item => item.Id == "The Winter Rescue");
            Assert(legacyForgeModpack.BaseVersion == "1.20.1" &&
                   legacyForgeModpack.LoaderVersion == "47.4.0",
                "Legacy self-contained Forge modpacks must infer their Minecraft and loader versions");

            var matcher = typeof(Kumo.MainWindow).GetMethod(
                "IsLoaderVersionInstalled",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static)!;
            var fabric = new LoaderVersionOption(ModLoader.Fabric, "0.19.5", "0.19.5");
            Assert((bool)matcher.Invoke(null, [installed, "26.3", fabric])!,
                "Matching base and loader version must be treated as installed");
            var newerFabric = new LoaderVersionOption(ModLoader.Fabric, "0.20.0", "0.20.0");
            Assert(!(bool)matcher.Invoke(null, [installed, "26.3", newerFabric])!,
                "A different loader version must remain installable");
            Assert(!(bool)matcher.Invoke(null, [installed, "26.2", fabric])!,
                "A loader version for another Minecraft version must remain installable");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return Task.CompletedTask;
    }),
    ("Installed Mod scan", () =>
    {
        var root = Path.Combine(
            Path.GetTempPath(), "kumo-installed-mods-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var archive = ZipFile.Open(Path.Combine(root, "enabled.jar"), ZipArchiveMode.Create))
            {
                var metadata = archive.CreateEntry("fabric.mod.json");
                using (var writer = new StreamWriter(metadata.Open()))
                {
                    writer.Write("""
                        {"id":"examplemod","name":"Example Mod","icon":"assets/icon.png"}
                        """);
                }
                var icon = archive.CreateEntry("assets/icon.png");
                using var iconStream = icon.Open();
                iconStream.Write([0x89, 0x50, 0x4E, 0x47]);
            }
            using (var archive = ZipFile.Open(
                       Path.Combine(root, "disabled.jar.disabled"), ZipArchiveMode.Create))
            {
                var metadata = archive.CreateEntry("fabric.mod.json");
                using var writer = new StreamWriter(metadata.Open());
                writer.Write("""{"id":"disabledmod","name":"Disabled Mod"}""");
            }
            File.WriteAllText(Path.Combine(root, "notes.txt"), "not a mod");

            var mods = InstalledModService.Scan(root, Path.Combine(root, "icons"));
            Assert(mods.Count == 2, "Only JAR and disabled JAR files should be listed");
            Assert(mods[0].FileName == "enabled.jar" && mods[0].IsEnabled,
                "Enabled mods should be listed first");
            Assert(mods[1].FileName == "disabled.jar.disabled" && !mods[1].IsEnabled,
                "Disabled mods should retain their disabled state");
            Assert(mods[0].ModId == "examplemod" && mods[0].LocalName == "Example Mod",
                "Fabric metadata should be read from the installed JAR");
            Assert(mods[0].CoverImagePath is { } iconPath && File.Exists(iconPath),
                "Embedded Mod icon should be extracted");

            var enabledPath = Path.Combine(root, "enabled.jar");
            var disabledPath = InstalledModService.SetEnabled(enabledPath, false);
            Assert(disabledPath.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(disabledPath),
                "Closing a Mod must rename its JAR file");

            var closedMods = InstalledModService.Scan(root, Path.Combine(root, "icons"));
            Assert(closedMods.Any(item =>
                    item.FileName == "enabled.jar.disabled" && !item.IsEnabled),
                "A closed Mod must remain visible in the installed Mod list");

            var restoredPath = InstalledModService.SetEnabled(disabledPath, true);
            Assert(restoredPath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(enabledPath),
                "Enabling a Mod must restore its JAR file name");
        }
        finally
        {
            Directory.Delete(root, true);
        }
        return Task.CompletedTask;
    }),
    ("Vanilla taskbar icon import", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "kumo-game-icon-" + Guid.NewGuid().ToString("N"));
        var previousIconOverride = Environment.GetEnvironmentVariable("KUMO_MINECRAFT_ICON");
        try
        {
            var versionDirectory = Path.Combine(root, "versions", "1.21.1");
            Directory.CreateDirectory(versionDirectory);
            File.WriteAllText(Path.Combine(versionDirectory, "1.21.1.json"), """
                {"id":"1.21.1","type":"release","mainClass":"net.minecraft.client.main.Main",
                 "libraries":[]}
                """);

            var iconPath = Path.Combine(root, "official-minecraft-icon.png");
            using (var source = new SkiaSharp.SKBitmap(16, 16))
            {
                source.Erase(new SkiaSharp.SKColor(0x58, 0x9A, 0x3D));
                using var image = SkiaSharp.SKImage.FromBitmap(source);
                using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                using var output = File.Create(iconPath);
                encoded.SaveTo(output);
            }
            File.WriteAllBytes(Path.Combine(versionDirectory, "1.21.1.jar"), [0]);

            Environment.SetEnvironmentVariable("KUMO_MINECRAFT_ICON", iconPath);
            var profiles = new VersionProfileService(root, new MinecraftService(new HttpService(), root));
            var vanilla = profiles.GetInstalledVersions().Single();
            Assert(vanilla.Loader == ModLoader.Vanilla, "Installed version must remain vanilla");
            Assert(vanilla.VanillaIconPath is not null && File.Exists(vanilla.VanillaIconPath),
                "Official Minecraft app icon must be copied");
            Assert(vanilla.HasLoaderIcon, "Vanilla taskbar icon must be available to the UI");

            Exception? failure = null;
            var loaded = false;
            var thread = new Thread(() =>
            {
                try
                {
                    var bitmap = new LoaderIconConverter().Convert(
                        vanilla,
                        typeof(System.Windows.Media.ImageSource),
                        null,
                        CultureInfo.InvariantCulture) as System.Windows.Media.Imaging.BitmapImage;
                    loaded = bitmap is { PixelWidth: > 0, PixelHeight: > 0 };
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure is not null) throw failure;
            Assert(loaded, "Imported Minecraft taskbar icon must decode");
        }
        finally
        {
            Environment.SetEnvironmentVariable("KUMO_MINECRAFT_ICON", previousIconOverride);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return Task.CompletedTask;
    }),
    ("Installed version can be copied and renamed", async () =>
    {
        var root = Path.Combine(
            Path.GetTempPath(), "kumo-version-copy-" + Guid.NewGuid().ToString("N"));
        var sourceId = "fabric-loader-0.19.5-1.21";
        var destinationId = sourceId + "-copy";
        var source = Path.Combine(root, sourceId);
        var destination = Path.Combine(root, destinationId);
        Directory.CreateDirectory(Path.Combine(source, "mods"));
        File.WriteAllText(Path.Combine(source, sourceId + ".json"),
            """{"id":"fabric-loader-0.19.5-1.21","kumoBaseVersion":"1.21","mainClass":"test.Main"}""");
        File.WriteAllBytes(Path.Combine(source, sourceId + ".jar"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(source, "mods", "example.jar"), "mod");
        try
        {
            var method = typeof(Kumo.MainWindow).GetMethod(
                "CloneVersionDirectory",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static)!;
            method.Invoke(null,
            [
                source, destination, sourceId, destinationId, "生存副本",
                CancellationToken.None
            ]);
            Assert(File.Exists(Path.Combine(destination, destinationId + ".jar")),
                "Copied version JAR is missing");
            Assert(File.Exists(Path.Combine(destination, "mods", "example.jar")),
                "Copied version Mods are missing");
            var json = JsonNode.Parse(File.ReadAllText(
                Path.Combine(destination, destinationId + ".json"))) as JsonObject;
            Assert(json?["id"]?.ToString() == destinationId,
                "Copied version ID was not updated");
            Assert(json?["kumoDisplayName"]?.ToString() == "生存副本",
                "Copied version display name was not stored");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        await Task.CompletedTask;
    }),
    ("DPAPI token round-trip", () =>
    {
        var token = "refresh-token-测试-" + Guid.NewGuid().ToString("N");
        try
        {
            var protectedValue = DpapiProtector.Protect(token);
            Assert(protectedValue != token, "Token must not remain plaintext");
            Assert(DpapiProtector.Unprotect(protectedValue) == token, "Token must decrypt");
        }
        catch (Win32Exception ex) when (Environment.GetEnvironmentVariable("CODEX_CI") == "1" &&
                                        ex.NativeErrorCode == 2)
        {
            Console.WriteLine("      SKIP: DPAPI is unavailable inside the restricted CI desktop.");
        }
        return Task.CompletedTask;
    }),
    ("Settings load with empty data directory", () =>
    {
        var root = Path.Combine(
            Path.GetTempPath(), "kumo-settings-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var configPath = Path.Combine(root, "launcher.json");
            var defaultGameDirectory = Path.Combine(root, "Kumo", ".minecraft");
            File.WriteAllText(configPath, """{"DataDirectory":""}""");
            var service = new SettingsService(
                root, configPath, defaultGameDirectory);

            service.Load();

            Assert(
                string.Equals(
                    service.Settings.DataDirectory,
                    defaultGameDirectory,
                    StringComparison.OrdinalIgnoreCase),
                "Empty data directory did not fall back to the Kumo default");
            Assert(Directory.Exists(defaultGameDirectory),
                "Default game directory was not created");
            Assert(File.ReadAllText(configPath).Contains(
                    defaultGameDirectory.Replace("\\", "\\\\"),
                    StringComparison.OrdinalIgnoreCase),
                "Default game directory was not saved");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return Task.CompletedTask;
    }),
    ("Settings migrate custom directory into .minecraft", () =>
    {
        var root = Path.Combine(
            Path.GetTempPath(), "kumo-settings-custom-" + Guid.NewGuid().ToString("N"));
        var appData = Path.Combine(root, "appdata");
        var customParent = Path.Combine(root, "CustomLauncher");
        var customVersions = Path.Combine(customParent, "versions", "1.21.1");
        var customInstance = Path.Combine(customParent, "instances", "1.21.1");
        Directory.CreateDirectory(appData);
        Directory.CreateDirectory(customVersions);
        Directory.CreateDirectory(customInstance);
        File.WriteAllText(Path.Combine(customVersions, "1.21.1.json"), "{}");
        File.WriteAllText(Path.Combine(customInstance, "options.txt"), "renderDistance:12");
        var configPath = Path.Combine(appData, "launcher.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(
            new { DataDirectory = customParent }));
        try
        {
            var defaultGameDirectory = Path.Combine(appData, ".minecraft");
            var service = new SettingsService(
                appData, configPath, defaultGameDirectory);
            service.Load();

            var expectedRoot = Path.Combine(customParent, ".minecraft");
            Assert(string.Equals(service.Settings.DataDirectory, expectedRoot,
                    StringComparison.OrdinalIgnoreCase),
                "Custom data directory did not move under .minecraft");
            Assert(!Directory.Exists(Path.Combine(customParent, "versions")),
                "Old versions directory remained outside .minecraft");
            Assert(File.Exists(Path.Combine(expectedRoot, "versions", "1.21.1", "1.21.1.json")),
                "Installed version was not moved into the .minecraft directory");
            Assert(!Directory.Exists(Path.Combine(expectedRoot, "instances")),
                "Legacy instances directory must not remain");
            Assert(File.Exists(Path.Combine(expectedRoot, "versions", "1.21.1", "options.txt")),
                "Legacy instance game data was not merged into the version directory");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return Task.CompletedTask;
    }),
    ("Portable data directory overrides saved path", () =>
    {
        var root = Path.Combine(
            Path.GetTempPath(), "kumo-settings-portable-" + Guid.NewGuid().ToString("N"));
        var appData = Path.Combine(root, "appdata");
        var savedDirectory = Path.Combine(root, "old-data");
        var portableDirectory = Path.Combine(root, "Launcher", ".minecraft");
        Directory.CreateDirectory(appData);
        Directory.CreateDirectory(savedDirectory);
        var configPath = Path.Combine(appData, "launcher.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(
            new { DataDirectory = savedDirectory }));
        try
        {
            var service = new SettingsService(
                appData,
                configPath,
                portableDirectory,
                forceDefaultGameDirectory: true);
            service.Load();

            Assert(string.Equals(
                    service.Settings.DataDirectory,
                    portableDirectory,
                    StringComparison.OrdinalIgnoreCase),
                "Portable mode must ignore the saved game directory");
            Assert(Directory.Exists(portableDirectory),
                "Portable .minecraft directory was not created");
            Assert(File.ReadAllText(configPath).Contains(
                    portableDirectory.Replace("\\", "\\\\"),
                    StringComparison.OrdinalIgnoreCase),
                "Portable .minecraft directory was not saved");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return Task.CompletedTask;
    }),
    ("Portable game directory detection", () =>
    {
        var method = typeof(AppPaths).GetMethod(
            "ResolvePortableGameDirectory",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static)!;
        var launcherDirectory = Path.Combine(Path.GetTempPath(), "Kumo Launcher");
        var nestedMinecraft = Path.Combine(launcherDirectory, ".minecraft");

        Assert(string.Equals(
                (string)method.Invoke(null, [launcherDirectory])!,
                nestedMinecraft,
                StringComparison.OrdinalIgnoreCase),
            "A launcher folder must use its child .minecraft directory");
        Assert(string.Equals(
                (string)method.Invoke(null, [nestedMinecraft])!,
                nestedMinecraft,
                StringComparison.OrdinalIgnoreCase),
            "A launcher already inside .minecraft must not create a nested .minecraft");
        return Task.CompletedTask;
    }),
    ("Instance metadata directory", () =>
    {
        var instance = Path.Combine(
            Path.GetTempPath(), "kumo-metadata-" + Guid.NewGuid().ToString("N"));
        var current = Path.Combine(instance, ".kumo");
        Directory.CreateDirectory(instance);
        try
        {
            Assert(string.Equals(
                    AppPaths.GetInstanceMetadataDirectory(instance),
                    current,
                    StringComparison.OrdinalIgnoreCase),
                "Instances must use the .kumo metadata directory");
        }
        finally
        {
            Directory.Delete(instance, true);
        }
        return Task.CompletedTask;
    }),
    ("Minecraft profile cache round-trip", () =>
    {
        var root = Path.Combine(
            Path.GetTempPath(), "kumo-profile-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new MinecraftProfileService(new HttpService(), root);
            var account = new AccountInfo
            {
                Id = "cache-test-account",
                Name = "CachePlayer",
                Kind = AccountKind.Microsoft
            };
            var profile = new MinecraftProfileSnapshot(
                "CachePlayer",
                "https://textures.minecraft.net/texture/cache-skin",
                "SLIM",
                [
                    new MinecraftCapeInfo(
                        "cape-id", "Migrator", "https://textures.minecraft.net/texture/cape", true)
                ]);

            service.SaveCachedProfile(account, profile);
            var cached = service.GetCachedProfile(account);

            if (cached is null)
                throw new InvalidOperationException("Cached profile must be readable");
            Assert(cached.Profile.Name == profile.Name, "Cached profile name changed");
            Assert(cached.Profile.SkinVariant == "SLIM", "Cached skin variant changed");
            Assert(cached.Profile.Capes.Count == 1, "Cached cape list changed");
            Assert(cached.Profile.Capes[0].IsActive, "Cached active cape was lost");
            Assert(cached.CachedAt > DateTimeOffset.UtcNow.AddMinutes(-1),
                "Cached timestamp is invalid");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return Task.CompletedTask;
    }),
    ("Global game options merge", () =>
    {
        var root = Path.Combine(
            Path.GetTempPath(), "kumo-global-options-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var optionsPath = Path.Combine(root, "options.txt");
            File.WriteAllLines(optionsPath,
            [
                "lang:en_us",
                "renderDistance:8",
                "maxFps:60",
                "key_key.forward:17",
                "key_key.inventory:18",
                "unknownSetting:keep-this"
            ]);
            var settings = new GlobalMinecraftOptions
            {
                Enabled = true,
                Language = "ja_jp",
                RenderDistance = 16,
                SimulationDistance = 10,
                MaxFps = 0,
                GuiScale = 2,
                Fullscreen = true,
                EnableVsync = false,
                PauseOnLostFocus = false,
                SyncKeybindings = true,
                Keybindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["key_key.forward"] = "30",
                    ["key_key.jump"] = "57"
                }
            };

            Assert(GlobalGameOptionsService.Apply(root, settings), "Global options must be applied");
            var lines = File.ReadAllLines(optionsPath);
            var values = lines
                .Select(line => line.Split(':', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1],
                    StringComparer.OrdinalIgnoreCase);
            Assert(values["lang"] == "ja_jp", "Language was not applied");
            Assert(values["renderDistance"] == "16", "Render distance was not applied");
            Assert(values["simulationDistance"] == "10", "Simulation distance was not applied");
            Assert(values["maxFps"] == "0", "Unlimited frame rate was not applied");
            Assert(values["guiScale"] == "2", "GUI scale was not applied");
            Assert(values["fullscreen"] == "true", "Fullscreen was not applied");
            Assert(values["enableVsync"] == "false", "VSync was not applied");
            Assert(values["pauseOnLostFocus"] == "false", "Pause behavior was not applied");
            Assert(values["key_key.forward"] == "30", "Existing keybinding was not replaced");
            Assert(values["key_key.jump"] == "57", "New keybinding was not added");
            Assert(values["key_key.inventory"] == "18", "Unrelated keybinding was not preserved");
            Assert(values["unknownSetting"] == "keep-this", "Unknown options must be preserved");
            var importedKeybindings = GlobalGameOptionsService.ReadKeybindings(optionsPath);
            Assert(importedKeybindings.Count == 3, "Keybindings were not readable");

            settings.Enabled = false;
            File.WriteAllText(optionsPath, "maxFps:30");
            Assert(!GlobalGameOptionsService.Apply(root, settings),
                "Disabled global options must not modify the file");
            Assert(File.ReadAllText(optionsPath).Trim() == "maxFps:30",
                "Disabled global options changed options.txt");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return Task.CompletedTask;
    }),
    ("Mojang rule evaluation", () =>
    {
        var windowsRule = JsonNode.Parse("""
            [{"action":"allow","os":{"name":"windows"}}]
            """) as JsonArray;
        Assert(VersionInstaller.RulesAllow(windowsRule), "Windows rule must allow");
        var linuxRule = JsonNode.Parse("""
            [{"action":"allow","os":{"name":"linux"}}]
            """) as JsonArray;
        Assert(!VersionInstaller.RulesAllow(linuxRule), "Linux rule must not allow on Windows");
        return Task.CompletedTask;
    }),
    ("Route URL mapping", (Func<Task>)(async () =>
    {
        var asset = "https://resources.download.minecraft.net/b6/b62ca8ec10d07e6bf5ac8dae0c8c1d2e6a1e3356";
        var mirrorAsset = DownloadRouteService.GetMirrorUrl("1.21.1", asset,
            "objects/b6/b62ca8ec10d07e6bf5ac8dae0c8c1d2e6a1e3356", DownloadFileKind.Asset);
        Assert(mirrorAsset.StartsWith(DownloadRouteService.MirrorRoot + "/assets/b6/"),
            "Asset mirror URL was not mapped");
        var library = "https://libraries.minecraft.net/org/example/demo/1.0/demo-1.0.jar";
        var mirrorLibrary = DownloadRouteService.GetMirrorUrl("1.21.1", library,
            "org/example/demo/1.0/demo-1.0.jar", DownloadFileKind.Library);
        Assert(mirrorLibrary == DownloadRouteService.MirrorRoot +
               "/maven/org/example/demo/1.0/demo-1.0.jar",
            "Library mirror URL was not mapped");
        var router = new DownloadRouteService(new HttpService());
        Assert((await router.SelectAsync(DownloadRouteMode.Official, [], null,
            CancellationToken.None)).Route == DownloadRoute.Official, "Official route override");
        Assert((await router.SelectAsync(DownloadRouteMode.DomesticMirror, [], null,
            CancellationToken.None)).Route == DownloadRoute.BmclApi, "Mirror route override");
    })),
    ("Range resume download", (Func<Task>)(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "kumo-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var content = new byte[1024 * 1024];
        for (var i = 0; i < content.Length; i++) content[i] = (byte)(i * 31 % 251);
        const int offset = 256 * 1024;
        var destination = Path.Combine(root, "client.jar");
        await File.WriteAllBytesAsync(destination + ".part", content[..offset]);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            string? line;
            var range = "";
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
            {
                if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                    range = line["Range:".Length..].Trim();
            }
            Assert(range == $"bytes={offset}-", $"Unexpected Range header: {range}");
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 206 Partial Content\r\nContent-Length: {content.Length - offset}\r\n" +
                $"Content-Range: bytes {offset}-{content.Length - 1}/{content.Length}\r\n" +
                "Accept-Ranges: bytes\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers);
            await stream.WriteAsync(content.AsMemory(offset));
        });
        try
        {
            var hash = Convert.ToHexString(SHA1.HashData(content));
            var http = new HttpService();
            await http.DownloadFileAsync($"http://127.0.0.1:{port}/client.jar", destination,
                hash, content.Length, null, CancellationToken.None);
            await server;
            Assert((await File.ReadAllBytesAsync(destination)).SequenceEqual(content),
                "Resumed file content was incorrect");
            Assert(!File.Exists(destination + ".part"), "Temporary file should be moved");
        }
        finally
        {
            listener.Stop();
            Directory.Delete(root, true);
        }
    })),
    ("Version inheritance merge", (Func<Task>)(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "kumo-smoke-" + Guid.NewGuid().ToString("N"));
        var baseDir = Path.Combine(root, "versions", "1.21.1");
        var childDir = Path.Combine(root, "versions", "fabric-loader-0.1-1.21.1");
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(childDir);
        await File.WriteAllTextAsync(Path.Combine(baseDir, "1.21.1.json"), """
            {"id":"1.21.1","type":"release","mainClass":"net.minecraft.client.main.Main",
             "libraries":[{"name":"example:base:1"}],
             "arguments":{"game":["--username","${auth_player_name}"],"jvm":["-cp","${classpath}"]}}
            """);
        await File.WriteAllTextAsync(Path.Combine(childDir, "fabric-loader-0.1-1.21.1.json"), """
            {"id":"fabric-loader-0.1-1.21.1","inheritsFrom":"1.21.1",
             "mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient",
             "libraries":[{"name":"fabric-loader:1"}],
             "arguments":{"game":["--fabric"],"jvm":[]}}
            """);
        try
        {
            var minecraft = new MinecraftService(new HttpService(), root);
            var profiles = new VersionProfileService(root, minecraft);
            var merged = await profiles.ResolveAsync("fabric-loader-0.1-1.21.1");
            Assert(merged["mainClass"]!.GetValue<string>().Contains("fabric"), "Child main class must win");
            Assert(merged["libraries"] is JsonArray libs && libs.Count == 2, "Libraries must be merged");
            var argumentObject = merged["arguments"] as JsonObject;
            var gameArguments = argumentObject?["game"] as JsonArray;
            Assert(gameArguments?.Count == 3,
                $"Game arguments must be merged (actual {gameArguments?.Count ?? -1})");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    })),
    ("Classpath keeps newest duplicate artifact", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "kumo-classpath-" + Guid.NewGuid().ToString("N"));
        var baseId = "1.21.10";
        var oldAsm = Path.Combine(
            root, "libraries", "org", "ow2", "asm", "asm", "9.6", "asm-9.6.jar");
        var newAsm = Path.Combine(
            root, "libraries", "org", "ow2", "asm", "asm", "9.10.1", "asm-9.10.1.jar");
        var clientJar = Path.Combine(root, "versions", baseId, baseId + ".jar");
        Directory.CreateDirectory(Path.GetDirectoryName(oldAsm)!);
        Directory.CreateDirectory(Path.GetDirectoryName(newAsm)!);
        Directory.CreateDirectory(Path.GetDirectoryName(clientJar)!);
        File.WriteAllBytes(oldAsm, [1]);
        File.WriteAllBytes(newAsm, [2]);
        File.WriteAllBytes(clientJar, [3]);
        try
        {
            var profile = (JsonObject)JsonNode.Parse("""
                {"libraries":[
                  {"name":"org.ow2.asm:asm:9.6"},
                  {"name":"org.ow2.asm:asm:9.10.1"}
                ]}
                """)!;
            var minecraft = new MinecraftService(new HttpService(), root);
            var profiles = new VersionProfileService(root, minecraft);
            var installer = new VersionInstaller(new HttpService(), minecraft, profiles, root);
            var launcher = new GameLauncher(root, profiles, installer,
                new JavaService(new HttpService(), root));
            var method = typeof(GameLauncher).GetMethod(
                "BuildClasspath",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)!;
            var classpath = (string)method.Invoke(launcher, [profile, baseId])!;
            var entries = classpath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            Assert(entries.Any(path => path.EndsWith(
                    "asm-9.10.1.jar", StringComparison.OrdinalIgnoreCase)),
                "Fabric ASM must remain on the classpath");
            Assert(!entries.Any(path => path.EndsWith(
                    "asm-9.6.jar", StringComparison.OrdinalIgnoreCase)),
                "The inherited ASM version must be replaced");
        }
        finally
        {
            Directory.Delete(root, true);
        }
        return Task.CompletedTask;
    }),
    ("Duplicate NeoForge game arguments", () =>
    {
        var method = typeof(GameLauncher).GetMethod(
            "DeduplicateGameArguments",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static)!;
        var repeated = new[]
        {
            "--username", "3miao", "--version", "skyexplorer", "--gameDir", "old",
            "--assetsDir", "assets", "--assetIndex", "17", "--uuid", "uuid",
            "--accessToken", "token", "--clientId", "kumo", "--xuid", "",
            "--userType", "msa", "--versionType", "release"
        };
        var arguments = repeated.Concat(repeated).Concat(
            ["--launchTarget", "forgeclient", "--fml.neoForgeVersion", "21.1.249"]).ToList();

        method.Invoke(null, [arguments, 0]);

        Assert(arguments.Count(argument => argument == "--gameDir") == 1,
            "Duplicate --gameDir must be removed");
        Assert(arguments.Count(argument => argument == "--username") == 1,
            "Duplicate --username must be removed");
        Assert(arguments[arguments.IndexOf("--gameDir") + 1] == "old",
            "The last option value must win");
        Assert(arguments.Contains("--fml.neoForgeVersion"),
            "NeoForge-specific arguments must be retained");
        return Task.CompletedTask;
    }),
    ("Offline Java launch pipeline", (Func<Task>)(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "kumo-launch-" + Guid.NewGuid().ToString("N"));
        var versionId = "kumo-smoke";
        var versionRoot = Path.Combine(root, "versions", versionId);
        var classes = Path.Combine(root, "classes");
        var source = Path.Combine(root, "KumoSmokeMain.java");
        Directory.CreateDirectory(versionRoot);
        Directory.CreateDirectory(classes);
        await File.WriteAllTextAsync(source, """
            package cubic.smoke;
            public final class KumoSmokeMain {
                public static void main(String[] args) {
                    System.out.println("KUMO_SMOKE_OK");
                    System.out.println("KUMO_PROP=" + System.getProperty("kumo.smoke"));
                    for (String arg : args) System.out.println("KUMO_ARG=" + arg);
                }
            }
            """);
        var javac = await FindExecutableAsync("javac.exe");
        var javaPath = Path.Combine(Path.GetDirectoryName(javac)!, "java.exe");
        var javaVersionText = await RunProcessCaptureAsync(javaPath, "-version");
        var javaMajor = JavaService.ParseMajorVersion(javaVersionText);
        await RunProcessAsync(javac, $"-encoding UTF8 -d \"{classes}\" \"{source}\"");
        var jarPath = Path.Combine(versionRoot, versionId + ".jar");
        ZipFile.CreateFromDirectory(classes, jarPath);
        await File.WriteAllTextAsync(Path.Combine(versionRoot, versionId + ".json"), $$"""
            {
              "id":"{{versionId}}",
              "type":"release",
              "mainClass":"cubic.smoke.KumoSmokeMain",
              "javaVersion":{"component":"jre-legacy","majorVersion":{{javaMajor}}},
              "arguments":{"game":[],"jvm":["-cp","${classpath}"]}
            }
            """);
        try
        {
            var minecraft = new MinecraftService(new HttpService(), root);
            var profiles = new VersionProfileService(root, minecraft);
            var installer = new VersionInstaller(new HttpService(), minecraft, profiles, root);
            var java = new JavaService(new HttpService(), root);
            var launcher = new GameLauncher(root, profiles, installer, java);
            var account = new AccountInfo
            {
                Name = "SmokePlayer",
                MinecraftUuid = GameLauncher.CreateOfflineUuid("SmokePlayer"),
                Kind = AccountKind.Offline,
                AccessToken = "offline"
            };
            var lines = new ConcurrentQueue<string>();
            var prepared = await launcher.LaunchAsync(versionId, account, 1024, javaPath, null,
                line => lines.Enqueue(line), CancellationToken.None,
                "-Dkumo.smoke=true", "--kumo-game-flag");
            await prepared.Process.WaitForExitAsync();
            await Task.Delay(100);
            Assert(prepared.Process.ExitCode == 0,
                $"Java process exited with {prepared.Process.ExitCode}: {string.Join(" | ", lines)}");
            Assert(lines.Any(x => x.Contains("KUMO_SMOKE_OK")),
                "Java classpath and main class were not passed correctly");
            Assert(lines.Any(x => x.Contains("KUMO_PROP=true")),
                "Custom JVM arguments were not passed");
            Assert(lines.Any(x => x.Contains("KUMO_ARG=--kumo-game-flag")),
                "Custom game arguments were not passed");
            prepared.Process.Dispose();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    })),
    ("Modrinth JSON contract", (Func<Task>)(() =>
    {
        const string json = """
            {"id":"abc","project_id":"project","name":"Sodium","version_number":"1.2.3",
             "version_type":"release","game_versions":["1.21.1"],"loaders":["fabric"],
             "date_published":"2026-01-02T03:04:05Z",
             "gallery":["https://cdn.example.invalid/cover.webp"],
             "files":[{"url":"https://example.invalid/mod.jar","filename":"mod.jar","primary":true,
                       "size":123,"hashes":{"sha1":"abc"}}],
             "dependencies":[{"project_id":"dep","dependency_type":"required"}]}
            """;
        var version = JsonSerializer.Deserialize<ModVersion>(json, HttpService.JsonOptions)!;
        Assert(version.ProjectId == "project", "project_id");
        Assert(version.Files.Single().Primary, "primary file");
        Assert(version.Dependencies.Single().DependencyType == "required", "dependency type");
        Assert(version.GameVersions.Single() == "1.21.1", "game_versions");
        var coverHit = JsonSerializer.Deserialize<ModSearchHit>(
            """{"title":"Sodium","gallery":["https://cdn.example.invalid/cover.webp"]}""",
            HttpService.JsonOptions)!;
        Assert(coverHit.Gallery.Single().EndsWith("cover.webp"), "gallery cover");
        var curseForgeHit = new ModSearchHit
        {
            Title = "Sodium",
            Source = ModSource.CurseForge,
            HasChineseDescription = true
        };
        Assert(curseForgeHit.SourceLabel == "CurseForge", "CurseForge source label");
        Assert(curseForgeHit.InfoCaption.StartsWith("中文说明", StringComparison.Ordinal),
            "CurseForge Chinese description label");
        Assert(ModrinthService.GetLoaderNames(ModLoader.Forge).SequenceEqual(new[] { "forge" }),
            "Forge Modrinth loader mapping");
        Assert(ModrinthService.GetLoaderNames(ModLoader.NeoForge).SequenceEqual(new[] { "neoforge" }),
            "NeoForge Modrinth loader mapping");
        Assert(ModrinthService.GetLoaderFacets(ModLoader.Forge).Contains("categories:forge"),
            "Forge Modrinth facet mapping");
        Assert(ModrinthService.GetLoaderFacets(ModLoader.NeoForge).Contains("categories:neoforge"),
            "NeoForge Modrinth facet mapping");
        Assert(ModrinthService.GetLoaderNames(ModLoader.Vanilla).Length == 0,
            "All-loader modpack queries must not pass a vanilla loader name");
        Assert(ModrinthService.GetLoaderFacets(ModLoader.Vanilla).Length == 0,
            "All-loader modpack queries must not pass a vanilla loader facet");
        var transientErrorMethod = typeof(ModrinthService).GetMethod(
            "IsTransientSearchError",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static)!;
        Assert((bool)transientErrorMethod.Invoke(
                null,
                [new HttpRequestException("Modrinth returned: searching projects")])!,
            "Transient Modrinth project-search failures must be retried");
        return Task.CompletedTask;
    })),
    ("CurseForge and MC百科 contracts", () =>
    {
        const string projectJson = """
            {"id":238222,"title":"Just Enough Items (JEI)","type":"Mods",
             "summary":"View Items and Recipes",
             "urls":{"curseforge":"https://www.curseforge.com/minecraft/mc-mods/jei"},
             "downloads":{"total":123456},"thumbnail":"https://media.example.invalid/jei.png",
             "members":[{"username":"mezz"}],"categories":["Map and Information"],
             "files":[
               {"id":9000001,"name":"jei-1.20.1-fabric.jar","type":"release",
                "filesize":100,"versions":["Fabric","1.20.1"],"uploaded_at":"2026-01-01T00:00:00Z"},
               {"id":9267533,"name":"jei-1.21.1-fabric-19.69.0.457.jar","type":"beta",
                "filesize":7397649,"versions":["Fabric","1.21.1"],"uploaded_at":"2026-10-20T14:43:38Z"}
             ]}
            """;
        var projectType = typeof(CurseForgeService).Assembly.GetType("Kumo.Services.CurseForgeProject")!;
        var project = JsonSerializer.Deserialize(projectJson, projectType, HttpService.JsonOptions)!;
        var findMethod = typeof(CurseForgeService).GetMethod("FindCompatibleFile",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var file = findMethod.Invoke(
            null, [project, "1.21.1", ModLoader.Fabric, ContentKind.Mod]);
        Assert(file is not null, "CurseForge compatible file was not found");
        Assert((long)file!.GetType().GetProperty("Id")!.GetValue(file)! == 9267533,
            "CurseForge file selection");
        Assert(CurseForgeService.GetDirectDownloadUrl(
                9267533, "jei-1.21.1-fabric-19.69.0.457.jar") ==
            "https://edge.forgecdn.net/files/9267/533/jei-1.21.1-fabric-19.69.0.457.jar",
            "CurseForge CDN URL");
        Assert(CurseForgeService.GetDirectDownloadUrl(
                4559072, "fabric-api-0.83.0+1.20.jar") ==
            "https://edge.forgecdn.net/files/4559/72/fabric-api-0.83.0%2B1.20.jar",
            "Legacy CurseForge CDN path");
        Assert(McmodService.TryGetKnownDescription("Sodium", out var chineseDescription) &&
               chineseDescription.Contains("渲染优化", StringComparison.Ordinal),
            "Chinese MC百科 description");

        const string searchHtml = """
            <div class="result-item"><div class="head">
              <a target="_blank" href="https://www.mcmod.cn/class/459.html">[JEI] JEI物品管理器 (Just Enough Items)</a>
            </div><div class="body">[h1=概述]JEI 是一个用于查看物品及配方的模组。</div>
            <div class="foot"></div></div>
            """;
        var parseMethod = typeof(McmodService).GetMethod("ParseSearchHtml",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var parsed = (IReadOnlyList<McmodModInfo>)parseMethod.Invoke(null, [searchHtml, 10])!;
        Assert(parsed.Count == 1 && parsed[0].EnglishTitle == "Just Enough Items",
            "MC百科 search title parsing");
        Assert(parsed[0].Description.Contains("查看物品及配方", StringComparison.Ordinal),
            "MC百科 Chinese description parsing");

        const string detailHtml = """
            <a data-original-title="CurseForge" href="//link.mcmod.cn/target/aHR0cHM6Ly93d3cuY3Vyc2Vmb3JnZS5jb20vbWluZWNyYWZ0L21jLW1vZHMvamVp">CurseForge</a>
            """;
        var slugMethod = typeof(McmodService).GetMethod("ParseCurseForgeSlug",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert((string?)slugMethod.Invoke(null, [detailHtml]) == "jei",
            "MC百科 CurseForge link decoding");
        return Task.CompletedTask;
    }),
    ("Content type catalog contracts", () =>
    {
        var expectedFacets = new Dictionary<ContentKind, string>
        {
            [ContentKind.Mod] = "mod",
            [ContentKind.Modpack] = "modpack",
            [ContentKind.DataPack] = "datapack",
            [ContentKind.ResourcePack] = "resourcepack",
            [ContentKind.Shader] = "shader"
        };
        var subdirectoryMethod = typeof(Kumo.MainWindow).GetMethod("ContentSubdirectory",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        foreach (var pair in expectedFacets)
        {
            Assert(ModrinthService.ContentKindFacet(pair.Key) == pair.Value,
                $"Modrinth facet for {pair.Key}");
            Assert((string)subdirectoryMethod.Invoke(null, [pair.Key])! == (pair.Key switch
                {
                    ContentKind.DataPack => "datapacks",
                    ContentKind.ResourcePack => "resourcepacks",
                    ContentKind.Shader => "shaderpacks",
                    ContentKind.Modpack => ".",
                    _ => "mods"
                }), $"Download directory for {pair.Key}");
        }

        var sectionMethod = typeof(CurseForgeService).GetMethod("GetContentSection",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert((string)sectionMethod.Invoke(null, [ContentKind.Mod])! == "mc-mods",
            "CurseForge mod section");
        Assert((string)sectionMethod.Invoke(null, [ContentKind.Modpack])! == "modpacks",
            "CurseForge modpack section");
        Assert((string)sectionMethod.Invoke(null, [ContentKind.DataPack])! == "data-packs",
            "CurseForge data-pack section");
        Assert((string)sectionMethod.Invoke(null, [ContentKind.ResourcePack])! == "texture-packs",
            "CurseForge resource-pack section");
        Assert((string)sectionMethod.Invoke(null, [ContentKind.Shader])! == "shaders",
            "CurseForge shader section");

        var extensionMethod = typeof(CurseForgeService).GetMethod("HasContentExtension",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert((bool)extensionMethod.Invoke(null, ["example.jar", ContentKind.Mod])!,
            "Mods use JAR files");
        Assert((bool)extensionMethod.Invoke(null, ["example.zip", ContentKind.ResourcePack])!,
            "Resource packs use ZIP files");
        Assert((bool)extensionMethod.Invoke(null, ["example.zip", ContentKind.Shader])!,
            "Shaders use ZIP files");
        Assert((bool)extensionMethod.Invoke(null, ["example.zip", ContentKind.DataPack])!,
            "Data packs must support ZIP files");
        Assert((bool)extensionMethod.Invoke(null, ["example.jar", ContentKind.DataPack])!,
            "Modrinth data packs may provide JAR-backed distributions");
        Assert(new ModSearchHit { ContentKind = ContentKind.Modpack }.PrimaryActionLabel == "安装",
            "Modpacks must expose an install action instead of a download action");
        Assert(new ModSearchHit { ContentKind = ContentKind.ResourcePack }.PrimaryActionLabel == "下载",
            "Resource packs must retain the download action");
        var loaderChoicesField = typeof(Kumo.MainWindow).GetField(
            "ContentLoaderChoices",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static)!;
        var loaderChoices = (LoaderChoiceOption[])loaderChoicesField.GetValue(null)!;
        Assert(loaderChoices.Select(item => item.Loader).SequenceEqual(
            [
                ModLoader.Vanilla,
                ModLoader.Fabric,
                ModLoader.Forge,
                ModLoader.NeoForge,
                ModLoader.Quilt
            ]), "Mod and modpack loader filters must expose all supported loaders");
        var fileChoice = new ModFileChoice(
            "1.2.3", "example.jar", "https://example.invalid/example.jar",
            null, 2L * 1024 * 1024, "正式版",
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), "Modrinth");
        Assert(fileChoice.DateLabel == "2026-10-01" &&
               fileChoice.DetailLabel.Contains("2.0 MB", StringComparison.Ordinal),
            "Historical mod file labels must include date and size");
        var filterBuilder = typeof(Kumo.MainWindow).GetMethod(
            "BuildModpackVersionFilters",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static)!;
        var remoteVersions = new List<RemoteVersion>
        {
            new()
            {
                Id = "1.21.1", Type = "release",
                Url = "https://example.invalid/1.21.1.json",
                ReleaseTime = DateTimeOffset.UtcNow
            },
            new()
            {
                Id = "26.3", Type = "snapshot",
                Url = "https://example.invalid/26.3.json",
                ReleaseTime = DateTimeOffset.UtcNow
            }
        };
        var filters = (IReadOnlyList<InstalledVersionInfo>)filterBuilder.Invoke(null,
        [
            remoteVersions,
            null
        ])!;
        Assert(filters.Count == 2 &&
               filters.All(item => item.Loader == ModLoader.Vanilla) &&
               filters.Select(item => item.BaseVersion).SequenceEqual(["1.21.1", "26.3"]) &&
               filters[0].FilterTypeLabel == "正式版" &&
               filters[1].FilterTypeLabel == "快照版",
            "Modpack filters must expose every remote Minecraft version");
        var releaseFilters = (IReadOnlyList<InstalledVersionInfo>)filterBuilder.Invoke(null,
            [remoteVersions, "release"])!;
        Assert(releaseFilters.Count == 1 && releaseFilters[0].Id == "1.21.1",
            "Selecting a version type must narrow the modpack version list");
        return Task.CompletedTask;
    }),
    ("Forge installer artifact URL handling", () =>
    {
        var libraries = JsonNode.Parse("""
            [
              {
                "name":"net.minecraftforge:forge:26.3-66.0.13:client",
                "downloads":{"artifact":{
                  "path":"net/minecraftforge/forge/26.3-66.0.13/forge-26.3-66.0.13-client.jar",
                  "url":"","sha1":"9581d4977f841f7ce33bde86954d586981a30c5b","size":82957733
                }}
              },
              {
                "name":"net.minecraftforge:forge:26.3-66.0.13:universal",
                "downloads":{"artifact":{
                  "path":"net/minecraftforge/forge/26.3-66.0.13/forge-26.3-66.0.13-universal.jar",
                  "url":"","sha1":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":10
                }},
                "url":"https://maven.minecraftforge.net/"
              },
              {
                "name":"example:relative:1.0",
                "downloads":{"artifact":{
                  "path":"example/relative/1.0/relative-1.0.jar",
                  "url":"example/relative/1.0/relative-1.0.jar","size":10
                }},
                "url":"https://maven.minecraftforge.net/"
              }
            ]
            """)!.AsArray();
        var taskType = typeof(VersionInstaller).Assembly.GetType(
            "Kumo.Services.VersionInstaller+ExternalLibraryTask")!;
        var dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(string), taskType);
        var tasks = Activator.CreateInstance(dictionaryType)!;
        var method = typeof(VersionInstaller).GetMethod(
            "AddExternalLibraries",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static)!;
        method.Invoke(null, [libraries, tasks, ModLoader.Forge]);

        var dictionary = (System.Collections.IDictionary)tasks;
        Assert(dictionary.Count == 1,
            "Empty Forge artifact URLs must be treated as installer-generated files");
        var task = dictionary.Values.Cast<object>().Single();
        var officialUrl = (string)taskType.GetProperty("OfficialUrl")!.GetValue(task)!;
        Assert(Uri.TryCreate(officialUrl, UriKind.Absolute, out _),
            "Relative installer library URLs must be resolved to absolute URLs");
        return Task.CompletedTask;
    }),
    ("Content save dialog file-name safety", () =>
    {
        var method = typeof(Kumo.MainWindow).GetMethod(
            "SanitizeModFileName",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static)!;
        string Sanitize(string name) =>
            (string)method.Invoke(null, [name, ".jar"])!;

        Assert(Sanitize("forge:client?.jar") == "forge_client_.jar",
            "Invalid Windows filename characters must be replaced");
        Assert(Sanitize("CON.jar") == "_CON.jar",
            "Reserved Windows device names must be escaped");
        Assert(Sanitize("mod") == "mod.jar",
            "A missing download extension must use the selected content default");
        Assert(Sanitize("....") == "download.jar",
            "A filename made only from trailing dots must fall back safely");
        return Task.CompletedTask;
    }),
    ("Automatic Mod source selection", () =>
    {
        var fasterCurseForge = ModSourceSpeedTestService.ChooseFastest(
        [
            new ModSourceProbe(ModSource.Modrinth, TimeSpan.FromMilliseconds(180), null),
            new ModSourceProbe(ModSource.CurseForge, TimeSpan.FromMilliseconds(45), null)
        ]);
        Assert(fasterCurseForge.Source == ModSource.CurseForge,
            "Automatic source selection must choose the lower latency");
        Assert(!fasterCurseForge.UsedFallback, "Measured source is not a fallback");

        var fallback = ModSourceSpeedTestService.ChooseFastest(
        [
            new ModSourceProbe(ModSource.Modrinth, null, "offline"),
            new ModSourceProbe(ModSource.CurseForge, null, "offline")
        ]);
        Assert(fallback.Source == ModSource.Modrinth && fallback.UsedFallback,
            "Automatic source selection must fall back to Modrinth");
        var descriptionHit = new ModSearchHit { Title = "Description test" };
        var descriptionNotifications = 0;
        descriptionHit.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ModSearchHit.Description) or nameof(ModSearchHit.InfoCaption))
                descriptionNotifications++;
        };
        descriptionHit.Description = "中文说明";
        descriptionHit.HasChineseDescription = true;
        Assert(descriptionNotifications >= 2, "Chinese descriptions must update bound Mod cards");
        var fallbackHit = new ModSearchHit
        {
            Title = "Completely Unlisted Mod",
            Description = "Original mod description",
            FallbackDescription = "Original mod description"
        };
        McmodService.ApplyChineseDescription(fallbackHit, null);
        Assert(fallbackHit.Description == "Original mod description" &&
               !fallbackHit.HasChineseDescription,
            "Missing MC百科 descriptions must use the original Mod description");
        return Task.CompletedTask;
    }),
    ("Mod description cache and links", (Func<Task>)(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "kumo-descriptions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "cache"));
        try
        {
            var cachePath = Path.Combine(root, "cache", "mcmod-descriptions-v1.json");
            var payload = new Dictionary<string, object>
            {
                ["sodium"] = new
                {
                    Description = "缓存中的中文说明",
                    McmodUrl = "https://www.mcmod.cn/class/2785.html",
                    CachedAt = DateTimeOffset.UtcNow
                }
            };
            await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(payload));
            var service = new McmodService(new HttpService(), root);
            var hit = new ModSearchHit
            {
                Title = "Sodium",
                Description = "Original Mod description",
                Source = ModSource.Modrinth
            };
            await service.EnrichModrinthHitsAsync([hit], string.Empty, CancellationToken.None);
            Assert(hit.Description == "缓存中的中文说明", "Persistent Chinese description cache");
            Assert(hit.McmodUrl == "https://www.mcmod.cn/class/2785.html", "Cached MC百科 link");

            var target = Convert.ToBase64String(Encoding.UTF8.GetBytes("https://modrinth.com/mod/jei"));
            var html =
                $"<a data-original-title=\"Modrinth\" href=\"//link.mcmod.cn/target/{target}\">Modrinth</a>";
            var parseMethod = typeof(McmodService).GetMethod("ParseModrinthSlug",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            Assert((string?)parseMethod.Invoke(null, [html]) == "jei", "MC百科 Modrinth link decoding");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    })),
    ("CurseForge live search", (Func<Task>)(async () =>
    {
        if (Environment.GetEnvironmentVariable("KUMO_LIVE_NETWORK") != "1")
        {
            Console.WriteLine("      SKIP: set KUMO_LIVE_NETWORK=1 to test live APIs.");
            return;
        }
        var root = Path.Combine(Path.GetTempPath(), "kumo-curseforge-" + Guid.NewGuid().ToString("N"));
        void WriteLiveLog(string line) => Console.WriteLine("      LOG " + line);
        LogService.Initialize(Path.Combine(root, "logs"));
        LogService.LineWritten += WriteLiveLog;
        try
        {
            var http = new HttpService("KumoLauncher/0.21");
            var service = new CurseForgeService(http, new McmodService(http, root), root);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var cases = new[]
            {
                (ContentKind.Mod, ModLoader.Fabric),
                (ContentKind.Modpack, ModLoader.NeoForge),
                (ContentKind.DataPack, ModLoader.Vanilla),
                (ContentKind.ResourcePack, ModLoader.Vanilla),
                (ContentKind.Shader, ModLoader.Vanilla)
            };
            foreach (var (kind, loader) in cases)
            {
                var result = await service.SearchAsync(
                    "", "1.21.1", loader, 0, 4, timeout.Token, kind);
                Assert(result.Hits.Count > 0, $"Live CurseForge search returned no {kind}");
                Assert(result.Hits.All(hit =>
                        hit.Source == ModSource.CurseForge && hit.ContentKind == kind),
                    $"Live CurseForge labels for {kind}");
                var file = await service.ResolveCompatibleFileAsync(
                    result.Hits[0], "1.21.1", loader, timeout.Token);
                if (kind == ContentKind.Mod)
                {
                    var choices = await service.GetFileChoicesAsync(
                        result.Hits[0], "1.21.1", loader, timeout.Token);
                    Assert(choices.Count > 0,
                        "Live CurseForge history must expose compatible file choices");
                }
                var validExtension = kind switch
                {
                    ContentKind.Mod => file.FileName.EndsWith(
                        ".jar", StringComparison.OrdinalIgnoreCase),
                    ContentKind.Modpack => file.FileName.EndsWith(
                        ".zip", StringComparison.OrdinalIgnoreCase),
                    _ => file.FileName.EndsWith(
                        ".zip", StringComparison.OrdinalIgnoreCase)
                };
                Assert(validExtension, $"Live CurseForge {kind} extension");
            }
            var modResult = await service.SearchAsync(
                "", "1.21.1", ModLoader.Fabric, 0, 6, timeout.Token, ContentKind.Mod);
            Assert(modResult.Hits.Any(hit => hit.HasChineseDescription),
                "Live CurseForge mods must include MC百科 Chinese descriptions");
        }
        finally
        {
            LogService.LineWritten -= WriteLiveLog;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    })),
    ("Modrinth live search", (Func<Task>)(async () =>
    {
        if (Environment.GetEnvironmentVariable("KUMO_LIVE_NETWORK") != "1")
        {
            Console.WriteLine("      SKIP: set KUMO_LIVE_NETWORK=1 to test live APIs.");
            return;
        }
        var root = Path.Combine(Path.GetTempPath(), "kumo-modrinth-" + Guid.NewGuid().ToString("N"));
        try
        {
            var http = new HttpService();
            var service = new ModrinthService(http, root);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var cases = new[]
            {
                (ContentKind.Mod, ModLoader.Fabric, "mod"),
                (ContentKind.Modpack, ModLoader.NeoForge, "modpack"),
                (ContentKind.DataPack, ModLoader.Vanilla, "datapack"),
                (ContentKind.ResourcePack, ModLoader.Vanilla, "resourcepack"),
                (ContentKind.Shader, ModLoader.Vanilla, "shader")
            };
            foreach (var (kind, loader, path) in cases)
            {
                var result = await service.SearchAsync(
                    "", "1.21.1", loader, 0, 4, timeout.Token, kind);
                Assert(result.Hits.Count > 0, $"Live Modrinth search returned no {kind}");
                Assert(result.Hits.All(hit =>
                        hit.ContentKind == kind &&
                        hit.ProjectUrl.StartsWith(
                            $"https://modrinth.com/{path}/", StringComparison.Ordinal)),
                    $"Live Modrinth labels and URLs for {kind}");
                var file = await service.ResolveCompatibleFileAsync(
                    result.Hits[0], "1.21.1", loader, timeout.Token);
                if (kind == ContentKind.Mod)
                {
                    var choices = await service.GetFileChoicesAsync(
                        result.Hits[0], "1.21.1", loader, timeout.Token);
                    Assert(choices.Count > 0,
                        "Live Modrinth history must expose compatible file choices");
                }
                var validExtension = kind switch
                {
                    ContentKind.Mod => file.FileName.EndsWith(
                        ".jar", StringComparison.OrdinalIgnoreCase),
                    ContentKind.Modpack => file.FileName.EndsWith(
                        ".mrpack", StringComparison.OrdinalIgnoreCase),
                    ContentKind.DataPack =>
                        file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                        file.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase),
                    _ => file.FileName.EndsWith(
                        ".zip", StringComparison.OrdinalIgnoreCase)
                };
                Assert(validExtension, $"Live Modrinth {kind} extension");
                Assert(Uri.TryCreate(file.Url, UriKind.Absolute, out _),
                    $"Live Modrinth {kind} download URL is invalid");
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    })),
    ("WebP cover conversion", (Func<Task>)(async () =>
    {
        var requestedSource = Environment.GetEnvironmentVariable("KUMO_COVER_SOURCE");
        var sample = string.IsNullOrWhiteSpace(requestedSource)
            ? Path.GetFullPath(Path.Combine("work", "mod-cover-sample.webp"))
            : Path.GetFullPath(requestedSource);
        if (!File.Exists(sample))
        {
            Console.WriteLine("      SKIP: optional WebP fixture is not present.");
            return;
        }
        var requestedOutput = Environment.GetEnvironmentVariable("KUMO_COVER_OUTPUT");
        var output = string.IsNullOrWhiteSpace(requestedOutput)
            ? Path.Combine(Path.GetTempPath(), "kumo-cover-" + Guid.NewGuid().ToString("N") + ".png")
            : Path.GetFullPath(requestedOutput);
        try
        {
            var method = typeof(ModCoverCacheService).GetMethod("ConvertToDisplayPng",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var converted = (bool)method.Invoke(null, [sample, output])!;
            Assert(converted, "WebP conversion failed");
            var signature = (await File.ReadAllBytesAsync(output)).Take(8).ToArray();
            Assert(signature.SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
                "Output is not PNG");
            using var bitmap = SkiaSharp.SKBitmap.Decode(output);
            var corner = bitmap.GetPixel(0, 0);
            Assert(corner.Alpha == 255, "Display image must not retain an alpha channel");
        }
        finally
        {
            if (string.IsNullOrWhiteSpace(requestedOutput) && File.Exists(output)) File.Delete(output);
        }
    })),
    ("Cover cache uses display image", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "kumo-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "cover.webp");
        var display = source + ".display.png";
        var lightDisplay = source + ".display-light.png";
        try
        {
            File.WriteAllBytes(source, [1, 2, 3]);
            File.WriteAllBytes(display, [4, 5, 6]);
            File.WriteAllBytes(lightDisplay, [7, 8, 9]);
            var method = typeof(ModCoverCacheService).GetMethod("GetCachedDisplayPath",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var selected = (string?)method.Invoke(null, [source]);
            Assert(selected == display, "Cached display image must take priority over the raw WebP");
            var themeMethod = typeof(ModCoverCacheService).GetMethod("GetCachedThemeDisplayPath",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var lightSelected = (string?)themeMethod.Invoke(null, [source, true]);
            Assert(lightSelected == lightDisplay, "Light theme must use its own display image");
        }
        finally
        {
            Directory.Delete(root, true);
        }
        return Task.CompletedTask;
    }),
    ("Offline player avatar", (Func<Task>)(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "kumo-avatar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var versionDirectory = Path.Combine(root, "versions", "1.21.1");
            Directory.CreateDirectory(versionDirectory);
            var skinPath = Path.Combine(root, "fixture-skin.png");
            using (var skin = new SkiaSharp.SKBitmap(64, 64))
            using (var canvas = new SkiaSharp.SKCanvas(skin))
            {
                canvas.Clear(new SkiaSharp.SKColor(0x80, 0x80, 0x80));
                using var image = SkiaSharp.SKImage.FromBitmap(skin);
                using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                using var output = File.Create(skinPath);
                encoded.SaveTo(output);
            }
            using (var archive = ZipFile.Open(Path.Combine(versionDirectory, "1.21.1.jar"), ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(
                    skinPath, "assets/minecraft/textures/entity/player/wide/steve.png");
                archive.CreateEntryFromFile(
                    skinPath, "assets/minecraft/textures/entity/player/slim/alex.png");
            }

            var service = new PlayerAvatarService(new HttpService(), root);
            var steve = new AccountInfo
            {
                Name = "AvatarTest",
                MinecraftUuid = GameLauncher.CreateOfflineUuid("AvatarTest"),
                Kind = AccountKind.Offline,
                OfflineSkinModel = OfflineSkinModel.Steve
            };
            var alex = new AccountInfo
            {
                Name = "AvatarTest",
                MinecraftUuid = GameLauncher.CreateOfflineUuid("AvatarTest"),
                Kind = AccountKind.Offline,
                OfflineSkinModel = OfflineSkinModel.Alex
            };
            var stevePath = await service.GetAvatarAsync(steve, CancellationToken.None);
            var alexPath = await service.GetAvatarAsync(alex, CancellationToken.None);
            Assert(stevePath is not null && File.Exists(stevePath), "Steve avatar file was not created");
            Assert(alexPath is not null && File.Exists(alexPath), "Alex avatar file was not created");
            Assert(stevePath != alexPath, "Steve and Alex must use different avatar caches");
            using var bitmap = SkiaSharp.SKBitmap.Decode(stevePath!);
            Assert(bitmap is { Width: 64, Height: 64 }, "Avatar must be a 64x64 Minecraft head");
            Assert(bitmap.GetPixel(0, 0).Alpha == 255, "Default avatar must use an opaque background");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    })),
    ("Minecraft skin avatar crop", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "kumo-skin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var skinPath = Path.Combine(root, "skin.png");
        var avatarPath = Path.Combine(root, "avatar.png");
        try
        {
            using (var skin = new SkiaSharp.SKBitmap(64, 64))
            using (var canvas = new SkiaSharp.SKCanvas(skin))
            {
                canvas.Clear(new SkiaSharp.SKColor(0x22, 0x66, 0xAA));
                using var facePaint = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0xCC, 0x44, 0x33) };
                canvas.DrawRect(8, 8, 8, 8, facePaint);
                using var output = File.Create(skinPath);
                using var image = SkiaSharp.SKImage.FromBitmap(skin);
                using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                encoded.SaveTo(output);
            }

            var cropMethod = typeof(PlayerAvatarService).GetMethod("CropHead",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            cropMethod.Invoke(null, [skinPath, avatarPath]);
            using var avatar = SkiaSharp.SKBitmap.Decode(avatarPath);
            Assert(avatar is { Width: 64, Height: 64 }, "Cropped avatar dimensions are incorrect");

            var normalizeMethod = typeof(PlayerAvatarService).GetMethod("NormalizeTextureUrl",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var normalized = (string?)normalizeMethod.Invoke(null,
                ["http://textures.minecraft.net/texture/example"]);
            Assert(normalized?.StartsWith("https://textures.minecraft.net/", StringComparison.Ordinal) == true,
                "Official skin URL was not upgraded to HTTPS");
        }
        finally
        {
            Directory.Delete(root, true);
        }
        return Task.CompletedTask;
    }),
    ("Minecraft front and elytra preview render", async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "kumo-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var skinPath = Path.Combine(root, "skin.png");
        var capePath = Path.Combine(root, "cape.png");
        try
        {
            using (var skin = new SkiaSharp.SKBitmap(64, 64))
            using (var canvas = new SkiaSharp.SKCanvas(skin))
            {
                canvas.Clear(new SkiaSharp.SKColor(0x66, 0x88, 0xAA));
                using var paint = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0xCC, 0x55, 0x44) };
                canvas.DrawRect(8, 8, 8, 8, paint);
                using var output = File.Create(skinPath);
                using var image = SkiaSharp.SKImage.FromBitmap(skin);
                using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                encoded.SaveTo(output);
            }
            using (var cape = new SkiaSharp.SKBitmap(128, 64))
            using (var canvas = new SkiaSharp.SKCanvas(cape))
            {
                canvas.Clear(SkiaSharp.SKColors.Transparent);
                using var paint = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0x70, 0x46, 0xC8) };
                canvas.DrawRect(0, 0, 20, 32, paint);
                paint.Color = new SkiaSharp.SKColor(0xE8, 0x3E, 0x3E);
                canvas.DrawRect(16, 0, 4, 32, paint);
                using var output = File.Create(capePath);
                using var image = SkiaSharp.SKImage.FromBitmap(cape);
                using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                encoded.SaveTo(output);
            }

            var service = new MinecraftAppearancePreviewService(new HttpService(), root);
            var previews = await service.CreateLocalPreviewAsync(
                skinPath, capePath, "smoke-test", "Classic", CancellationToken.None);
            Assert(previews.FrontPath is not null && File.Exists(previews.FrontPath),
                "Front appearance preview was not created");
            Assert(previews.ElytraPath is not null && File.Exists(previews.ElytraPath),
                "Elytra appearance preview was not created");
            Assert(previews.CapeFrontPath is not null && File.Exists(previews.CapeFrontPath),
                "Cape front preview was not created");
            Assert(previews.CapeBackPath is not null && File.Exists(previews.CapeBackPath),
                "Cape back preview was not created");
            using var front = SkiaSharp.SKBitmap.Decode(previews.FrontPath);
            using var elytra = SkiaSharp.SKBitmap.Decode(previews.ElytraPath);
            using var capeFront = SkiaSharp.SKBitmap.Decode(previews.CapeFrontPath);
            Assert(front is { Width: 128, Height: 184 }, "Front preview dimensions are incorrect");
            Assert(elytra is { Width: 128, Height: 184 }, "Elytra preview dimensions are incorrect");
            Assert(front.GetPixel(64, 100).Alpha > 0, "Front preview is empty");
            Assert(elytra.GetPixel(30, 90).Alpha > 0, "Elytra preview is empty");
            var frontHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(previews.FrontPath!)));
            var elytraHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(previews.ElytraPath!)));
            Assert(frontHash != elytraHash, "Front and elytra previews must be distinct images");
            var foundRightEdge = false;
            for (var y = 0; y < capeFront.Height && !foundRightEdge; y++)
            for (var x = 0; x < capeFront.Width; x++)
            {
                var color = capeFront.GetPixel(x, y);
                if (color.Red <= 200 || color.Green >= 100 || color.Blue >= 100) continue;
                foundRightEdge = true;
                break;
            }
            Assert(foundRightEdge, "High-resolution cape right edge was cropped");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }),
    ("Modpack custom instance name", () =>
    {
        var plan = new ModpackInstallPlan(
            "示例整合包",
            "1.2.3",
            "1.21.1",
            ModLoader.Fabric,
            "0.16.9",
            "Modrinth",
            "example.mrpack",
            [],
            []);
        var method = typeof(Kumo.MainWindow).GetMethod(
            "ResolveModpackInstanceId",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static)!;
        Assert((string)method.Invoke(null, [plan, "我的整合包"])! == "我的整合包",
            "A custom modpack name must become the independent instance id");
        var generated = (string)method.Invoke(null, [plan, null])!;
        Assert(generated.Contains("1.21.1") && generated.Contains("fabric"),
            "A blank modpack name must keep the generated default id");
        try
        {
            method.Invoke(null, [plan, "bad/name"]);
            throw new InvalidOperationException("Invalid modpack name was accepted");
        }
        catch (System.Reflection.TargetInvocationException ex)
            when (ex.InnerException is InvalidOperationException)
        {
            // Expected validation failure.
        }
        return Task.CompletedTask;
    }),
    ("Modpack manifest parsing", async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "kumo-modpack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var mrpack = Path.Combine(root, "example.mrpack");
            using (var archive = ZipFile.Open(mrpack, ZipArchiveMode.Create))
            {
                var index = archive.CreateEntry("modrinth.index.json");
                await using (var writer = new StreamWriter(index.Open()))
                {
                    await writer.WriteAsync("""
                        {
                          "name":"Kumo Test Pack","versionId":"2.0",
                          "dependencies":{"minecraft":"1.20.1","fabric-loader":"0.15.3"},
                          "files":[{"path":"mods/example.jar","downloads":["https://example.com/example.jar"],
                            "hashes":{"sha1":"0000000000000000000000000000000000000000"},"fileSize":12,
                            "env":{"client":"required"}}]
                        }
                        """);
                }
                var overrides = archive.CreateEntry("overrides/config/test.txt");
                await using var overrideWriter = new StreamWriter(overrides.Open());
                await overrideWriter.WriteAsync("test");
            }

            var curseZip = Path.Combine(root, "curse.zip");
            using (var archive = ZipFile.Open(curseZip, ZipArchiveMode.Create))
            {
                var manifest = archive.CreateEntry("manifest.json");
                await using var writer = new StreamWriter(manifest.Open());
                await writer.WriteAsync("""
                    {
                      "name":"Curse Test Pack","version":"1.4",
                      "minecraft":{"version":"1.20.1","modLoaders":[{"id":"forge-47.2.0","primary":true}]},
                      "files":[{"projectID":123456,"fileID":7890123,"required":true}],
                      "overrides":"overrides"
                    }
                    """);
            }

            var curse = new CurseForgeService(new HttpService(),
                new McmodService(new HttpService(), root), root);
            var service = new ModpackService(new HttpService(), curse, root);
            var modrinthPlan = await service.InspectAsync(mrpack, CancellationToken.None);
            Assert(modrinthPlan.Name == "Kumo Test Pack", "Modrinth pack name");
            Assert(modrinthPlan.Loader == ModLoader.Fabric && modrinthPlan.LoaderVersion == "0.15.3",
                "Modrinth loader metadata");
            Assert(modrinthPlan.Files.Count == 1 && modrinthPlan.OverrideRoots.Contains("overrides"),
                "Modrinth files and overrides");

            var cursePlan = await service.InspectAsync(curseZip, CancellationToken.None);
            Assert(cursePlan.Loader == ModLoader.Forge && cursePlan.LoaderVersion == "47.2.0",
                "CurseForge loader metadata");
            Assert(cursePlan.Files.Single().CurseProjectId == 123456,
                "CurseForge project metadata");

            var pclPackage = Path.Combine(root, "pcl-wrapper.zip");
            using (var wrapper = ZipFile.Open(pclPackage, ZipArchiveMode.Create))
            {
                var nested = wrapper.CreateEntry("modpack.mrpack");
                await using var source = File.OpenRead(mrpack);
                await using var target = nested.Open();
                await source.CopyToAsync(target);
            }
            var pclPlan = await service.InspectAsync(pclPackage, CancellationToken.None);
            Assert(pclPlan.Name == "Kumo Test Pack" && pclPlan.Loader == ModLoader.Fabric,
                "PCL2 nested mrpack metadata");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }),
    ("skinview3d renderer bundle", () =>
    {
        using var stream = typeof(PlayerAvatarService).Assembly.GetManifestResourceStream(
            "Kumo.Assets.skinview3d.bundle.js");
        Assert(stream is { Length: > 400_000 }, "skinview3d renderer bundle is missing");
        return Task.CompletedTask;
    })
};

var passed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        passed++;
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL  {test.Name}: {ex.Message}");
        Environment.ExitCode = 1;
    }
}
Console.WriteLine($"{passed}/{tests.Count} smoke tests passed.");

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task RunProcessAsync(string file, string arguments)
{
    await RunProcessCaptureAsync(file, arguments, throwOnError: true);
}

static async Task<string> RunProcessCaptureAsync(
    string file,
    string arguments,
    bool throwOnError = false)
{
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }
    };
    process.Start();
    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var output = await outputTask;
    var error = await errorTask;
    if (throwOnError && process.ExitCode != 0)
        throw new InvalidOperationException($"{file} failed: {output} {error}");
    return output + error;
}

static async Task<string> FindExecutableAsync(string name)
{
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo("where.exe", name)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }
    };
    process.Start();
    var output = await process.StandardOutput.ReadToEndAsync();
    await process.WaitForExitAsync();
    var first = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    return process.ExitCode == 0 && first is not null
        ? first.Trim()
        : throw new FileNotFoundException($"{name} was not found on PATH.");
}
