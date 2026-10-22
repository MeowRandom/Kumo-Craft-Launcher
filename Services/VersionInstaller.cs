using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Kumo.Models;

namespace Kumo.Services;

public sealed class VersionInstaller
{
    private readonly HttpService _http;
    private readonly MinecraftService _minecraft;
    private readonly VersionProfileService _profiles;
    private readonly DownloadRouteService _routeService;
    private readonly Func<DownloadRouteMode> _routeModeProvider;
    private readonly string _dataDirectory;

    public VersionInstaller(
        HttpService http,
        MinecraftService minecraft,
        VersionProfileService profiles,
        string dataDirectory,
        Func<DownloadRouteMode>? routeModeProvider = null)
    {
        _http = http;
        _minecraft = minecraft;
        _profiles = profiles;
        _routeService = new DownloadRouteService(http);
        _routeModeProvider = routeModeProvider ?? (() => DownloadRouteMode.Automatic);
        _dataDirectory = dataDirectory;
    }

    public async Task InstallOfficialAsync(
        ManifestVersion version,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken,
        bool asDependency = false)
    {
        progress?.Report(new DownloadProgress("正在读取版本信息", 0, 1, version.Id));
        var raw = await _http.GetStringAsync(version.Url, cancellationToken);
        var json = JsonNode.Parse(raw) as JsonObject
                   ?? throw new InvalidDataException("Mojang 返回的版本信息无效。");

        var path = _minecraft.GetVersionJsonPath(version.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        VersionMetadata.Set(json, VersionMetadata.Dependency, asDependency);
        VersionMetadata.Set(json, VersionMetadata.Explicit, !asDependency);
        await File.WriteAllTextAsync(path, json.ToJsonString(), cancellationToken);
        LogService.Info($"开始安装 Minecraft {version.Id}");
        await InstallFilesAsync(version.Id, progress, cancellationToken);
        LogService.Info($"Minecraft {version.Id} 安装完成");
    }

    public async Task EnsureVersionAsync(
        string versionId,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        await InstallFilesAsync(versionId, progress, cancellationToken);
    }

    public async Task NormalizeLoaderVersionsAsync(CancellationToken cancellationToken)
    {
        foreach (var version in _profiles.GetInstalledVersions())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (version.Loader == ModLoader.Vanilla ||
                string.Equals(version.Id, version.BaseVersion,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            await FlattenLoaderVersionAsync(
                version.Id, version.BaseVersion, cancellationToken);
        }
    }

    public async Task<InstalledVersionInfo> InstallFabricAsync(
        string baseVersion,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new DownloadProgress("正在查找 Fabric Loader", 0, 1, baseVersion));
        var loaders = await _http.GetJsonAsync<List<FabricLoaderEntry>>(
            $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(baseVersion)}",
            cancellationToken);
        var loader = loaders.FirstOrDefault(x => x.Loader.Stable) ?? loaders.FirstOrDefault()
                     ?? throw new InvalidOperationException($"Fabric 不支持 Minecraft {baseVersion}。");
        return await InstallFabricVersionAsync(baseVersion, loader.Loader.Version, progress, cancellationToken);
    }

    public async Task<InstalledVersionInfo> InstallFabricVersionAsync(
        string baseVersion,
        string loaderVersion,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new DownloadProgress("正在准备 Fabric Loader", 0, 1, loaderVersion));
        var profileUrl =
            $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(baseVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json";
        var profileJson = await _http.GetStringAsync(profileUrl, cancellationToken);
        var profile = JsonNode.Parse(profileJson) as JsonObject
                      ?? throw new InvalidDataException("Fabric 返回的配置无效。");
        var id = profile.Str("id") ?? $"fabric-loader-{loaderVersion}-{baseVersion}";
        _profiles.SaveLocal(id, profileJson);
        await InstallFilesAsync(id, progress, cancellationToken);
        await FlattenLoaderVersionAsync(id, baseVersion, cancellationToken);
        await SaveLoaderVersionMetadataAsync(id, loaderVersion, cancellationToken);
        LogService.Info($"已安装 {baseVersion} 的 Fabric Loader {loaderVersion}");
        return new InstalledVersionInfo
        {
            Id = id,
            BaseVersion = baseVersion,
            Loader = ModLoader.Fabric,
            Ready = true,
            LastWriteTime = DateTimeOffset.UtcNow
        };
    }

    public async Task RepairFabricVersionAsync(
        string versionId,
        string baseVersion,
        string loaderVersion,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new DownloadProgress(
            "正在升级旧版 Fabric Loader", 0, 1, loaderVersion));
        var originalProfile = await _profiles.LoadLocalAsync(versionId, cancellationToken);
        var profileUrl =
            $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(baseVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json";
        var profileJson = await _http.GetStringAsync(profileUrl, cancellationToken);
        var profile = JsonNode.Parse(profileJson) as JsonObject
                      ?? throw new InvalidDataException("Fabric 返回的配置无效。");
        profile["id"] = versionId;
        _profiles.SaveLocal(versionId, profile.ToJsonString());

        await InstallFilesAsync(versionId, progress, cancellationToken);
        await FlattenLoaderVersionAsync(versionId, baseVersion, cancellationToken);
        await SaveLoaderVersionMetadataAsync(versionId, loaderVersion, cancellationToken);

        var repairedProfile = await _profiles.LoadLocalAsync(versionId, cancellationToken);
        foreach (var pair in originalProfile)
        {
            if (!VersionMetadata.IsOwnedKey(pair.Key) ||
                pair.Key is VersionMetadata.BaseVersion or VersionMetadata.LoaderVersion)
                continue;
            repairedProfile[pair.Key] = pair.Value?.DeepClone();
        }
        _profiles.SaveLocal(versionId, repairedProfile.ToJsonString());
        LogService.Info(
            $"已原地升级 {versionId} 的 Fabric Loader 到 {loaderVersion}");
    }

    public async Task<InstalledVersionInfo> InstallQuiltAsync(
        string baseVersion,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new DownloadProgress("正在查找 Quilt Loader", 0, 1, baseVersion));
        var loaders = await _http.GetJsonAsync<List<QuiltLoaderEntry>>(
            $"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(baseVersion)}",
            cancellationToken);
        var loader = loaders.FirstOrDefault(x => x.Loader.Stable) ?? loaders.FirstOrDefault()
                     ?? throw new InvalidOperationException($"Quilt 不支持 Minecraft {baseVersion}。");
        return await InstallQuiltVersionAsync(baseVersion, loader.Loader.Version, progress, cancellationToken);
    }

    public async Task<InstalledVersionInfo> InstallQuiltVersionAsync(
        string baseVersion,
        string loaderVersion,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new DownloadProgress("正在准备 Quilt Loader", 0, 1, loaderVersion));
        var profileUrl =
            $"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(baseVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json";
        var profileJson = await _http.GetStringAsync(profileUrl, cancellationToken);
        var profile = JsonNode.Parse(profileJson) as JsonObject
                      ?? throw new InvalidDataException("Quilt 返回的配置无效。");
        var id = profile.Str("id") ?? $"quilt-loader-{loaderVersion}-{baseVersion}";
        _profiles.SaveLocal(id, profileJson);
        await InstallFilesAsync(id, progress, cancellationToken);
        await FlattenLoaderVersionAsync(id, baseVersion, cancellationToken);
        await SaveLoaderVersionMetadataAsync(id, loaderVersion, cancellationToken);
        LogService.Info($"已安装 {baseVersion} 的 Quilt Loader {loaderVersion}");
        return new InstalledVersionInfo
        {
            Id = id,
            BaseVersion = baseVersion,
            Loader = ModLoader.Quilt,
            Ready = true,
            LastWriteTime = DateTimeOffset.UtcNow
        };
    }

    public async Task<InstalledVersionInfo> InstallWithInstallerAsync(
        string baseVersion,
        LoaderVersionOption option,
        string javaExecutable,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (option.Loader is not (ModLoader.Forge or ModLoader.NeoForge))
            throw new InvalidOperationException($"{option.Loader} 不使用独立安装器。");
        if (string.IsNullOrWhiteSpace(option.InstallerUrl))
            throw new InvalidOperationException("加载器安装器地址为空。");

        Directory.CreateDirectory(_dataDirectory);
        await EnsureLauncherProfileAsync(cancellationToken);
        var installerPath = await DownloadInstallerAsync(option, progress, cancellationToken);

        string versionId;
        using (var archive = ZipFile.OpenRead(installerPath))
        {
            var installProfile = ReadArchiveJson(archive, "install_profile.json");
            var versionJson = ReadArchiveJson(archive, "version.json") ??
                              installProfile?.Obj("versionInfo") ??
                              throw new InvalidDataException("官方安装器缺少版本配置。");
            versionId = versionJson.Str("id") ?? installProfile.Str("version")
                        ?? throw new InvalidDataException("官方安装器没有声明客户端版本名称。");
            if (Path.GetFileName(versionId) != versionId)
                throw new InvalidDataException("官方安装器返回了不安全的版本名称。");

            await PrepareInstallerLibrariesAsync(archive, option.Loader, progress, cancellationToken);
        }

        progress?.Report(new DownloadProgress(
            $"正在执行 {LoaderName(option.Loader)} 官方安装器",
            0,
            1,
            option.Version));
        await RunLoaderInstallerAsync(javaExecutable, installerPath, cancellationToken);

        var profilePath = _minecraft.GetVersionJsonPath(versionId);
        if (!File.Exists(profilePath))
            throw new InvalidOperationException(
                $"{LoaderName(option.Loader)} 安装器已结束，但没有生成版本 {versionId}。");

        await InstallFilesAsync(versionId, progress, cancellationToken);
        await FlattenLoaderVersionAsync(versionId, baseVersion, cancellationToken);
        await SaveLoaderVersionMetadataAsync(versionId, option.Version, cancellationToken);
        LogService.Info($"已安装 {baseVersion} 的 {LoaderName(option.Loader)} {option.Version}");
        return new InstalledVersionInfo
        {
            Id = versionId,
            BaseVersion = baseVersion,
            Loader = option.Loader,
            Ready = _minecraft.IsVersionInstalled(versionId),
            LastWriteTime = DateTimeOffset.UtcNow
        };
    }

    public async Task<InstalledVersionInfo> InstallOptiFineAsync(
        string baseVersion,
        LoaderVersionOption option,
        string javaExecutable,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (option.Loader != ModLoader.OptiFine)
            throw new InvalidOperationException("所选版本不是 OptiFine。");
        Directory.CreateDirectory(_dataDirectory);
        await EnsureLauncherProfileAsync(cancellationToken);
        var installerPath = await DownloadInstallerAsync(option, progress, cancellationToken);
        var bridgeDirectory = Path.Combine(_dataDirectory, "cache", "optifine-bridge");
        Directory.CreateDirectory(bridgeDirectory);
        var bridgePath = Path.Combine(bridgeDirectory, "OptiFineInstallerBridge.class");
        await using (var source = typeof(VersionInstaller).Assembly.GetManifestResourceStream(
                         "Kumo.Assets.OptiFineInstallerBridge.class")
                     ?? throw new InvalidOperationException("OptiFine 安装桥接文件缺失。"))
        await using (var destination = File.Create(bridgePath))
            await source.CopyToAsync(destination, cancellationToken);

        progress?.Report(new DownloadProgress(
            "正在执行 OptiFine 安装器", 0, 1, option.DisplayName));
        await RunOptiFineInstallerAsync(
            javaExecutable, installerPath, bridgeDirectory, cancellationToken);

        var expectedId = $"{baseVersion}-OptiFine_{option.Version}";
        var versionId = File.Exists(_minecraft.GetVersionJsonPath(expectedId))
            ? expectedId
            : FindInstalledOptiFineVersion(baseVersion);
        if (string.IsNullOrWhiteSpace(versionId))
            throw new InvalidOperationException("OptiFine 安装器已结束，但没有生成对应版本。");
        await InstallFilesAsync(versionId, progress, cancellationToken);
        await FlattenLoaderVersionAsync(versionId, baseVersion, cancellationToken);
        await SaveLoaderVersionMetadataAsync(versionId, option.Version, cancellationToken);
        LogService.Info($"已安装 Minecraft {baseVersion} 的 OptiFine {option.DisplayName}");
        return new InstalledVersionInfo
        {
            Id = versionId,
            BaseVersion = baseVersion,
            Loader = ModLoader.OptiFine,
            Ready = _minecraft.IsVersionInstalled(versionId),
            LastWriteTime = DateTimeOffset.UtcNow
        };
    }

    private string? FindInstalledOptiFineVersion(string baseVersion)
    {
        var versionsDirectory = Path.Combine(_dataDirectory, "versions");
        if (!Directory.Exists(versionsDirectory)) return null;
        return Directory.EnumerateDirectories(versionsDirectory)
            .Where(path => Path.GetFileName(path).Contains(
                $"-OptiFine_", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .Select(Path.GetFileName)
            .FirstOrDefault(id => id is not null &&
                                  id.StartsWith(baseVersion + "-OptiFine_", StringComparison.OrdinalIgnoreCase));
    }

    private async Task SaveLoaderVersionMetadataAsync(
        string versionId,
        string loaderVersion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(loaderVersion)) return;
        var profile = await _profiles.LoadLocalAsync(versionId, cancellationToken);
        VersionMetadata.Set(profile, VersionMetadata.LoaderVersion, loaderVersion);
        _profiles.SaveLocal(versionId, profile.ToJsonString());
    }

    internal static bool NeedsFabricUpgradeForJava(string? loaderVersion, int javaMajor)
    {
        if (javaMajor < 25 || !Version.TryParse(loaderVersion, out var parsed))
            return false;
        var minimum = javaMajor switch
        {
            >= 26 => new Version(0, 17, 3),
            _ => new Version(0, 16, 14)
        };
        return parsed < minimum;
    }

    private async Task<string> DownloadInstallerAsync(
        LoaderVersionOption option,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var fileName =
            $"{LoaderName(option.Loader).ToLowerInvariant()}-{option.Version}-installer.jar";
        var directory = Path.Combine(_dataDirectory, "cache", "loaders");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        if (IsInstallerReady(path, option.InstallerSha1)) return path;

        var urls = new[] { option.InstallerFallbackUrl, option.InstallerUrl }
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (_routeModeProvider() == DownloadRouteMode.Official)
            urls.Reverse();

        Exception? lastError = null;
        foreach (var url in urls)
        {
            try
            {
                progress?.Report(new DownloadProgress(
                    $"正在下载 {LoaderName(option.Loader)} 安装器",
                    0,
                    1,
                    option.Version));
                await _http.DownloadFileAsync(url, path, option.InstallerSha1, null, null, cancellationToken);
                if (IsInstallerReady(path, option.InstallerSha1)) return path;
                lastError = new InvalidDataException("安装器文件校验失败。");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                LogService.Warn($"加载器安装器下载失败，尝试备用地址：{ex.Message}");
            }
        }

        throw new InvalidOperationException(
            $"无法下载 {LoaderName(option.Loader)} 安装器：{lastError?.Message ?? "未知错误"}",
            lastError);
    }

    private async Task PrepareInstallerLibrariesAsync(
        ZipArchive archive,
        ModLoader loader,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var tasks = new Dictionary<string, ExternalLibraryTask>(StringComparer.OrdinalIgnoreCase);
        var versionJson = ReadArchiveJson(archive, "version.json");
        var installProfile = ReadArchiveJson(archive, "install_profile.json");
        versionJson ??= installProfile?.Obj("versionInfo");
        AddExternalLibraries(versionJson?.Arr("libraries"), tasks, loader);
        AddExternalLibraries(installProfile?.Arr("libraries"), tasks, loader);
        var legacyInstall = installProfile?.Obj("install");
        AddExternalLibraries(legacyInstall?.Arr("libraries"), tasks, loader);
        AddExternalLibraries(legacyInstall?.Obj("versionInfo")?.Arr("libraries"), tasks, loader);
        AddProcessorLibraries(installProfile, tasks, loader);

        var librariesRoot = Path.GetFullPath(Path.Combine(_dataDirectory, "libraries"));
        var root = librariesRoot + Path.DirectorySeparatorChar;
        var pending = new List<(ExternalLibraryTask Task, string Path)>();
        foreach (var task in tasks.Values)
        {
            var destination = Path.GetFullPath(Path.Combine(librariesRoot, task.RelativePath));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("加载器安装清单包含不安全的依赖路径。");
            if (!IsExternalLibraryComplete(task)) pending.Add((task, destination));
        }
        if (pending.Count == 0) return;

        progress?.Report(new DownloadProgress(
            $"正在准备 {LoaderName(loader)} 安装依赖",
            0,
            pending.Count,
            pending[0].Task.RelativePath));
        var completed = 0;
        var errors = new ConcurrentQueue<Exception>();
        using var gate = new SemaphoreSlim(12);
        await Task.WhenAll(pending.Select(async item =>
        {
            var task = item.Task;
            var destination = item.Path;
            await gate.WaitAsync(cancellationToken);
            try
            {
                var mirrorUrl = $"{DownloadRouteService.MirrorRoot}/maven/{task.RelativePath}";
                try
                {
                    await _http.DownloadFileAsync(mirrorUrl, destination, task.Sha1, task.Size, null,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception mirrorError)
                {
                    LogService.Warn($"安装依赖镜像下载失败，回退官方地址：{mirrorError.Message}");
                    await _http.DownloadFileAsync(task.OfficialUrl, destination, task.Sha1, task.Size, null,
                        cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Enqueue(new IOException($"{Path.GetFileName(destination)}：{ex.Message}", ex));
            }
            finally
            {
                gate.Release();
                var done = Interlocked.Increment(ref completed);
                progress?.Report(new DownloadProgress(
                    $"正在准备 {LoaderName(loader)} 安装依赖",
                    done,
                    pending.Count,
                    task.RelativePath));
            }
        }));
        if (!errors.IsEmpty)
            throw new AggregateException($"有 {errors.Count} 个 {LoaderName(loader)} 安装依赖下载失败。", errors);
    }

    private static void AddExternalLibraries(
        JsonArray? libraries,
        IDictionary<string, ExternalLibraryTask> tasks,
        ModLoader loader)
    {
        if (libraries is null) return;
        foreach (var node in libraries)
        {
            if (node is not JsonObject library) continue;
            var artifact = library.Obj("downloads")?.Obj("artifact");
            var name = library.Str("name");
            var relative = artifact?.Str("path") ??
                           (!string.IsNullOrWhiteSpace(name) ? JsonHelpers.ToMavenPath(name) : null);
            if (string.IsNullOrWhiteSpace(relative)) continue;
            var baseUrl = library.Str("url") ??
                          (loader == ModLoader.NeoForge
                              ? "https://maven.neoforged.net/releases/"
                              : "https://maven.minecraftforge.net/");
            var artifactUrl = artifact?.Str("url");
            if (artifact is null || !string.IsNullOrWhiteSpace(artifactUrl))
            {
                var url = ResolveLibraryUrl(
                    artifactUrl, library.Str("url"), baseUrl, relative);
                AddExternalLibrary(tasks, relative, url, artifact?.Str("sha1"),
                    artifact?["size"]?.GetValue<long>());
            }
            else
            {
                // Forge installer profiles contain generated artifacts (for example the
                // client JAR) with an empty URL. They are created by the installer itself.
                LogService.Info($"跳过安装器生成文件：{relative}");
            }

            if (library.Obj("downloads")?.Obj("classifiers") is not JsonObject classifiers) continue;
            foreach (var pair in classifiers)
            {
                if (!IsWindowsNative(pair.Key) || pair.Value is not JsonObject classifier) continue;
                if (classifier.Str("path") is not { } classifierPath ||
                    string.IsNullOrWhiteSpace(classifier.Str("url"))) continue;
                var classifierUrl = ResolveLibraryUrl(
                    classifier.Str("url"), library.Str("url"), baseUrl, classifierPath);
                AddExternalLibrary(tasks, classifierPath, classifierUrl, classifier.Str("sha1"),
                    classifier["size"]?.GetValue<long>());
            }
        }
    }

    internal static string ResolveLibraryUrl(
        string? artifactUrl,
        string? libraryRoot,
        string defaultRoot,
        string relativePath)
    {
        var root = !string.IsNullOrWhiteSpace(libraryRoot) &&
                   Uri.TryCreate(libraryRoot, UriKind.Absolute, out var libraryUri)
            ? libraryUri
            : new Uri(defaultRoot, UriKind.Absolute);
        if (!string.IsNullOrWhiteSpace(artifactUrl))
        {
            if (Uri.TryCreate(artifactUrl, UriKind.Absolute, out var absolute))
                return absolute.AbsoluteUri;
            if (Uri.TryCreate(EnsureTrailingSlash(root), artifactUrl, out var combined))
                return combined.AbsoluteUri;
        }
        if (Uri.TryCreate(EnsureTrailingSlash(root), relativePath, out var fallback))
            return fallback.AbsoluteUri;
        throw new InvalidDataException($"无法解析 Maven 依赖地址：{relativePath}");
    }

    private static Uri EnsureTrailingSlash(Uri root) =>
        root.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? root
            : new Uri(root.AbsoluteUri + "/", UriKind.Absolute);

    private static void AddProcessorLibraries(
        JsonObject? installProfile,
        IDictionary<string, ExternalLibraryTask> tasks,
        ModLoader loader)
    {
        if (installProfile?.Arr("processors") is not JsonArray processors) return;
        var baseUrl = loader == ModLoader.NeoForge
            ? "https://maven.neoforged.net/releases/"
            : "https://maven.minecraftforge.net/";
        foreach (var node in processors.OfType<JsonObject>())
        {
            if (node.Str("jar") is { } jar) AddMavenLibrary(tasks, jar, baseUrl);
            if (node.Arr("classpath") is not JsonArray classpath) continue;
            foreach (var item in classpath)
            {
                if (item?.GetValue<string>() is { } coordinate)
                    AddMavenLibrary(tasks, coordinate, baseUrl);
            }
        }
    }

    private static void AddMavenLibrary(
        IDictionary<string, ExternalLibraryTask> tasks,
        string coordinate,
        string baseUrl)
    {
        var relative = JsonHelpers.ToMavenPath(coordinate);
        AddExternalLibrary(tasks, relative, baseUrl.TrimEnd('/') + "/" + relative, null, null);
    }

    private static void AddExternalLibrary(
        IDictionary<string, ExternalLibraryTask> tasks,
        string relative,
        string url,
        string? sha1,
        long? size)
    {
        var safeRelative = relative.Replace('/', Path.DirectorySeparatorChar);
        tasks[safeRelative] = new ExternalLibraryTask(safeRelative, url, sha1, size);
    }

    private bool IsExternalLibraryComplete(ExternalLibraryTask task)
    {
        var path = Path.Combine(_dataDirectory, "libraries", task.RelativePath);
        if (!File.Exists(path)) return false;
        var length = new FileInfo(path).Length;
        if (length == 0) return false;
        if (task.Size is > 0 && length != task.Size.Value) return false;
        if (string.IsNullOrWhiteSpace(task.Sha1)) return true;
        try
        {
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(stream));
            return hash.Equals(task.Sha1, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static JsonObject? ReadArchiveJson(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path);
        if (entry is null) return null;
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return JsonNode.Parse(reader.ReadToEnd()) as JsonObject;
    }

    private async Task EnsureLauncherProfileAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_dataDirectory, "launcher_profiles.json");
        if (File.Exists(path)) return;
        await File.WriteAllTextAsync(path,
            """{"profiles":{},"settings":{},"version":3}""", cancellationToken);
    }

    private async Task RunLoaderInstallerAsync(
        string javaExecutable,
        string installerPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(javaExecutable))
            throw new FileNotFoundException("找不到用于运行加载器安装器的 Java。", javaExecutable);
        var startInfo = new ProcessStartInfo(javaExecutable)
        {
            WorkingDirectory = Path.GetDirectoryName(installerPath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-jar");
        startInfo.ArgumentList.Add(installerPath);
        startInfo.ArgumentList.Add("--installClient");
        startInfo.ArgumentList.Add(_dataDirectory);
        startInfo.ArgumentList.Add("--offline");

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("无法启动加载器安装器。");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            throw;
        }

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode == 0) return;

        var details = string.Join(Environment.NewLine,
            (output + Environment.NewLine + error)
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .TakeLast(24));
        throw new InvalidOperationException(
            $"加载器安装器执行失败（退出码 {process.ExitCode}）。{Environment.NewLine}{details}");
    }

    private async Task RunOptiFineInstallerAsync(
        string javaExecutable,
        string installerPath,
        string bridgeDirectory,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(javaExecutable))
            throw new FileNotFoundException("找不到用于运行 OptiFine 安装器的 Java。", javaExecutable);
        var startInfo = new ProcessStartInfo(javaExecutable)
        {
            WorkingDirectory = Path.GetDirectoryName(installerPath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-Djava.awt.headless=true");
        startInfo.ArgumentList.Add("-cp");
        startInfo.ArgumentList.Add(installerPath + Path.PathSeparator + bridgeDirectory);
        startInfo.ArgumentList.Add("OptiFineInstallerBridge");
        startInfo.ArgumentList.Add(_dataDirectory);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("无法启动 OptiFine 安装器。");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            throw;
        }

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode == 0) return;
        var details = string.Join(Environment.NewLine,
            (output + Environment.NewLine + error)
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .TakeLast(24));
        throw new InvalidOperationException(
            $"OptiFine 安装失败（退出码 {process.ExitCode}）。{Environment.NewLine}{details}");
    }

    private static bool IsInstallerReady(string path, string? sha1)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return false;
        if (string.IsNullOrWhiteSpace(sha1)) return true;
        try
        {
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(stream));
            return actual.Equals(sha1, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string LoaderName(ModLoader loader) => loader switch
    {
        ModLoader.Fabric => "Fabric",
        ModLoader.Forge => "Forge",
        ModLoader.NeoForge => "NeoForge",
        ModLoader.Quilt => "Quilt",
        ModLoader.OptiFine => "OptiFine",
        _ => "原版"
    };

    private async Task InstallFilesAsync(
        string versionId,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var profile = await _profiles.ResolveAsync(versionId, cancellationToken);
        var localProfile = await _profiles.LoadLocalAsync(versionId, cancellationToken);
        var parentVersion = localProfile.Str("inheritsFrom");
        if (!string.IsNullOrWhiteSpace(parentVersion) && !_minecraft.IsVersionInstalled(parentVersion) &&
            !await TryInstallParentFromManifestAsync(parentVersion, progress, cancellationToken))
            throw new InvalidOperationException($"请先安装原版 {parentVersion}。");

        progress?.Report(new DownloadProgress("正在整理资源列表", 0, 1, versionId));
        var tasks = new Dictionary<string, DownloadTask>(StringComparer.OrdinalIgnoreCase);

        AddVersionArtifacts(profile, versionId, tasks, string.IsNullOrWhiteSpace(parentVersion),
            cancellationToken);
        AddLibraries(profile, tasks);
        await AddAssetsAsync(profile, tasks, cancellationToken);

        await RunDownloadTasksAsync(tasks.Values, progress, versionId, cancellationToken);
    }

    private async Task FlattenLoaderVersionAsync(
        string versionId,
        string dependencyVersionId,
        CancellationToken cancellationToken)
    {
        var localProfile = await _profiles.LoadLocalAsync(versionId, cancellationToken);
        if (string.IsNullOrWhiteSpace(localProfile.Str("inheritsFrom")))
        {
            if (string.IsNullOrWhiteSpace(
                    VersionMetadata.GetString(localProfile, VersionMetadata.BaseVersion)) &&
                !string.IsNullOrWhiteSpace(dependencyVersionId))
            {
                VersionMetadata.Set(
                    localProfile, VersionMetadata.BaseVersion, dependencyVersionId);
                _profiles.SaveLocal(versionId, localProfile.ToJsonString());
            }
            return;
        }

        var sourceJar = _minecraft.GetVersionJarPath(dependencyVersionId);
        if (!File.Exists(sourceJar) || new FileInfo(sourceJar).Length == 0)
        {
            LogService.Warn(
                $"无法合并 {versionId}：原版 {dependencyVersionId} 的客户端 JAR 缺失。");
            return;
        }

        var resolved = await _profiles.ResolveAsync(versionId, cancellationToken);
        resolved["id"] = versionId;
        VersionMetadata.Set(resolved, VersionMetadata.BaseVersion, dependencyVersionId);
        resolved.Remove("inheritsFrom");
        VersionMetadata.Remove(resolved, VersionMetadata.Dependency);
        VersionMetadata.Remove(resolved, VersionMetadata.Explicit);
        var targetJar = _minecraft.GetVersionJarPath(versionId);
        Directory.CreateDirectory(Path.GetDirectoryName(targetJar)!);
        if (!File.Exists(targetJar) || new FileInfo(targetJar).Length == 0)
            File.Copy(sourceJar, targetJar, true);
        _profiles.SaveLocal(versionId, resolved.ToJsonString());
        MoveVanillaDependencyIfUnused(dependencyVersionId);
    }

    private void MoveVanillaDependencyIfUnused(string dependencyVersionId)
    {
        try
        {
            var dependencyPath = _minecraft.GetVersionJsonPath(dependencyVersionId);
            if (!File.Exists(dependencyPath)) return;
            var dependencyJson =
                JsonNode.Parse(File.ReadAllText(dependencyPath)) as JsonObject;
            if (dependencyJson is null ||
                VersionMetadata.GetBoolean(dependencyJson, VersionMetadata.Explicit) ||
                _profiles.DetectLoader(dependencyVersionId, dependencyJson).Loader !=
                ModLoader.Vanilla)
                return;

            var versionsRoot = Path.GetFullPath(Path.Combine(_dataDirectory, "versions"));
            foreach (var directory in Directory.EnumerateDirectories(versionsRoot))
            {
                var id = Path.GetFileName(directory);
                if (string.Equals(id, dependencyVersionId, StringComparison.OrdinalIgnoreCase))
                    continue;
                var jsonPath = Path.Combine(directory, id + ".json");
                if (!File.Exists(jsonPath)) continue;
                var json = JsonNode.Parse(File.ReadAllText(jsonPath)) as JsonObject;
                if (string.Equals(json.Str("inheritsFrom"), dependencyVersionId,
                        StringComparison.OrdinalIgnoreCase))
                    return;
            }

            var source = Path.GetDirectoryName(dependencyPath)!;
            var cacheRoot = Path.Combine(_dataDirectory, "cache", "version-dependencies");
            Directory.CreateDirectory(cacheRoot);
            var destination = Path.Combine(cacheRoot, dependencyVersionId);
            if (Directory.Exists(destination))
                destination += "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            Directory.Move(source, destination);
            LogService.Info($"已将自动下载的原版 {dependencyVersionId} 移出 versions 目录");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       System.Text.Json.JsonException)
        {
            LogService.Warn($"无法移动原版依赖 {dependencyVersionId}：{ex.Message}");
        }
    }

    private async Task<bool> TryInstallParentFromManifestAsync(
        string versionId,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var manifest = await _minecraft.GetManifestAsync(cancellationToken: cancellationToken);
            var remote = manifest.Versions.FirstOrDefault(x => x.Id == versionId);
            if (remote is null) return false;
            await InstallOfficialAsync(remote, progress, cancellationToken, asDependency: true);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Warn($"自动安装父版本 {versionId} 失败：{ex.Message}");
            return false;
        }
    }

    private void AddVersionArtifacts(
        JsonObject profile,
        string versionId,
        IDictionary<string, DownloadTask> tasks,
        bool includeClientJar,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var downloads = profile.Obj("downloads") ?? new JsonObject();
        if (includeClientJar && downloads.Obj("client") is { } client &&
            client.Str("url") is { } clientUrl)
        {
            var path = _minecraft.GetVersionJarPath(versionId);
            AddTask(tasks, clientUrl, path, client.Str("sha1"), client["size"]?.GetValue<long>(),
                DownloadFileKind.Client, string.Empty);
        }

        if (downloads.Obj("client_mappings") is { } mappings &&
            mappings.Str("url") is { } mappingUrl && mappings.Str("sha1") is { } mappingHash)
        {
            var path = Path.Combine(_dataDirectory, "versions", versionId, versionId + "-mappings.txt");
            AddTask(tasks, mappingUrl, path, mappingHash, mappings["size"]?.GetValue<long>(),
                DownloadFileKind.Other, string.Empty);
        }

        if (profile.Obj("logging")?.Obj("client")?.Obj("file") is { } logFile &&
            logFile.Str("url") is { } logUrl)
        {
            var fileName = Path.GetFileName(new Uri(logUrl).LocalPath);
            var path = Path.Combine(_dataDirectory, "assets", "log_configs", fileName);
            AddTask(tasks, logUrl, path, logFile.Str("sha1"), logFile["size"]?.GetValue<long>(),
                DownloadFileKind.LoggingConfig, string.Empty);
        }
    }

    private void AddLibraries(JsonObject profile, IDictionary<string, DownloadTask> tasks)
    {
        if (profile.Arr("libraries") is not JsonArray libraries) return;
        foreach (var node in libraries)
        {
            if (node is not JsonObject library || !RulesAllow(library.Arr("rules"))) continue;
            var libraryName = library.Str("name");
            if (!string.IsNullOrWhiteSpace(libraryName) &&
                !IsNativeArtifactCompatible(libraryName))
                continue;
            if (library.Obj("downloads")?.Obj("artifact") is { } artifact &&
                artifact.Str("path") is { } artifactPath)
            {
                if (!IsNativeArtifactCompatible(artifactPath)) continue;
                var artifactUrl = artifact.Str("url");
                if (string.IsNullOrWhiteSpace(artifactUrl))
                {
                    var generatedPath = Path.Combine(_dataDirectory, "libraries", artifactPath);
                    if (!File.Exists(generatedPath) || new FileInfo(generatedPath).Length == 0)
                        throw new InvalidDataException(
                            $"加载器安装器没有生成预期的文件：{artifactPath}");
                }
                else
                {
                    var url = ResolveLibraryUrl(
                        artifactUrl, library.Str("url"),
                        "https://libraries.minecraft.net/", artifactPath);
                    AddTask(tasks, url, Path.Combine(_dataDirectory, "libraries", artifactPath),
                        artifact.Str("sha1"), artifact["size"]?.GetValue<long>(),
                        DownloadFileKind.Library, artifactPath);
                }
            }
            else if (ShouldUseMavenArtifactFallback(library) && libraryName is { } name)
            {
                var mavenPath = JsonHelpers.ToMavenPath(name);
                var url = ResolveLibraryUrl(
                    null, library.Str("url"),
                    "https://libraries.minecraft.net/", mavenPath);
                AddTask(tasks, url, Path.Combine(_dataDirectory, "libraries", mavenPath),
                    library.Str("sha1"), library["size"]?.GetValue<long>(),
                    DownloadFileKind.Library, mavenPath);
            }

            if (library.Obj("downloads")?.Obj("classifiers") is not JsonObject classifiers) continue;
            foreach (var pair in classifiers)
            {
                if (!IsWindowsNative(pair.Key) || pair.Value is not JsonObject classifier) continue;
                if (classifier.Str("path") is not { } path ||
                    string.IsNullOrWhiteSpace(classifier.Str("url"))) continue;
                var url = ResolveLibraryUrl(
                    classifier.Str("url"), library.Str("url"),
                    "https://libraries.minecraft.net/", path);
                AddTask(tasks, url, Path.Combine(_dataDirectory, "libraries", path),
                    classifier.Str("sha1"), classifier["size"]?.GetValue<long>(),
                    DownloadFileKind.Library, path);
            }
        }
    }

    internal static bool ShouldUseMavenArtifactFallback(JsonObject library)
    {
        var downloads = library.Obj("downloads");
        return downloads is null ||
               (downloads.Obj("artifact") is null &&
                downloads.Obj("classifiers") is null);
    }

    private async Task AddAssetsAsync(
        JsonObject profile,
        IDictionary<string, DownloadTask> tasks,
        CancellationToken cancellationToken)
    {
        if (profile.Obj("assetIndex") is not { } index || index.Str("url") is not { } indexUrl) return;
        var indexId = index.Str("id") ?? index.Str("assetIndex") ?? profile.Str("assets") ?? "legacy";
        var indexPath = Path.Combine(_dataDirectory, "assets", "indexes", indexId + ".json");
        await _http.DownloadFileAsync(indexUrl, indexPath, index.Str("sha1"),
            index["size"]?.GetValue<long>(), null, cancellationToken);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(indexPath, cancellationToken)) as JsonObject;
        if (root?.Obj("objects") is not JsonObject objects) return;
        foreach (var pair in objects)
        {
            if (pair.Value is not JsonObject asset) continue;
            var hash = asset.Str("hash");
            if (string.IsNullOrWhiteSpace(hash) || hash.Length < 2) continue;
            var relative = Path.Combine("objects", hash[..2], hash);
            var destination = Path.Combine(_dataDirectory, "assets", relative);
            var url = $"https://resources.download.minecraft.net/{hash[..2]}/{hash}";
            AddTask(tasks, url, destination, hash, asset["size"]?.GetValue<long>(),
                DownloadFileKind.Asset, relative);
        }
    }

    private async Task RunDownloadTasksAsync(
        IEnumerable<DownloadTask> source,
        IProgress<DownloadProgress>? progress,
        string stage,
        CancellationToken cancellationToken)
    {
        var tasks = source.ToList();
        var pending = tasks.Where(task => !IsFileComplete(task)).ToList();
        var skipped = tasks.Count - pending.Count;
        var allSizesKnown = pending.All(task => task.Size is > 0);
        var pendingBytes = allSizesKnown ? pending.Sum(task => task.Size!.Value) : 0;
        var meter = new DownloadBatchMeter(tasks.Count, skipped, pendingBytes);
        foreach (var task in pending)
        {
            if (File.Exists(task.Path + ".part"))
                meter.SetResumeOffset(task.Path, new FileInfo(task.Path + ".part").Length);
        }
        var errors = new ConcurrentQueue<Exception>();
        using var gate = new SemaphoreSlim(24);
        var stopwatch = Stopwatch.StartNew();
        long lastReportTimestamp = 0;
        var mirrorFailures = 0;
        var mirrorDisabled = 0;

        var probes = BuildRouteCandidates(stage, pending);
        var routeChoice = pending.Count == 0
            ? new DownloadRouteChoice(DownloadRoute.Official, "已是最新", 0, 0)
            : await _routeService.SelectAsync(_routeModeProvider(), probes, progress, cancellationToken);
        var routeName = routeChoice.DisplayName;

        void Report(string file, bool force = false)
        {
            var now = Stopwatch.GetTimestamp();
            if (!force)
            {
                var previous = Interlocked.Read(ref lastReportTimestamp);
                if (Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromMilliseconds(110)) return;
                if (Interlocked.CompareExchange(ref lastReportTimestamp, now, previous) != previous) return;
            }
            else
            {
                Interlocked.Exchange(ref lastReportTimestamp, now);
            }
            progress?.Report(meter.Snapshot("正在下载 " + stage, file, routeName));
        }

        Report("准备连接", true);
        var workers = pending.Select(async task =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var mirrorUrl = DownloadRouteService.GetMirrorUrl(stage, task.OfficialUrl,
                    task.RelativePath, task.Kind);
                var tryMirror = routeChoice.Route == DownloadRoute.BmclApi &&
                                DownloadRouteService.HasDistinctMirror(task.OfficialUrl, mirrorUrl) &&
                                Volatile.Read(ref mirrorDisabled) == 0;
                if (tryMirror)
                {
                    try
                    {
                        await _http.DownloadFileAsync(mirrorUrl, task.Path, task.Sha1, task.Size,
                            (received, total) =>
                            {
                                meter.Report(task.Path, received);
                                Report(Path.GetFileName(task.Path));
                            }, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception mirrorError)
                    {
                        var failures = Interlocked.Increment(ref mirrorFailures);
                        if (failures >= 5) Volatile.Write(ref mirrorDisabled, 1);
                        if (failures <= 5)
                            LogService.Warn($"镜像下载失败，回退官方线路：{mirrorError.Message}");
                        await _http.DownloadFileAsync(task.OfficialUrl, task.Path, task.Sha1, task.Size,
                            (received, total) =>
                            {
                                meter.Report(task.Path, received);
                                Report(Path.GetFileName(task.Path));
                            }, cancellationToken);
                    }
                }
                else
                {
                    await _http.DownloadFileAsync(task.OfficialUrl, task.Path, task.Sha1, task.Size,
                        (received, total) =>
                        {
                            meter.Report(task.Path, received);
                            Report(Path.GetFileName(task.Path));
                        }, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Enqueue(new IOException($"{Path.GetFileName(task.Path)}：{ex.Message}", ex));
            }
            finally
            {
                gate.Release();
                meter.Complete(task.Path, task.Size ?? 0);
                Report(Path.GetFileName(task.Path), tasks.Count == skipped + pending.Count &&
                                                     pending.Count == 1);
            }
        });

        await Task.WhenAll(workers);
        if (!errors.IsEmpty)
            throw new AggregateException($"有 {errors.Count} 个文件下载失败。", errors);
        stopwatch.Stop();
        var completedProgress = meter.Snapshot($"下载完成 · {stopwatch.Elapsed.TotalSeconds:0.0} 秒",
            stage, routeName);
        progress?.Report(completedProgress with
        {
            Completed = tasks.Count,
            BytesReceived = pendingBytes > 0 ? pendingBytes : completedProgress.BytesReceived,
            TotalBytes = pendingBytes > 0 ? pendingBytes : completedProgress.TotalBytes,
            BytesPerSecond = 0,
            EstimatedRemaining = TimeSpan.Zero
        });
    }

    private static IReadOnlyList<DownloadRouteCandidate> BuildRouteCandidates(
        string versionId,
        IReadOnlyList<DownloadTask> pending)
    {
        var candidates = new List<DownloadRouteCandidate>();
        foreach (var kind in new[] { DownloadFileKind.Library, DownloadFileKind.Asset })
        {
            var task = pending
                .Where(item => item.Kind == kind)
                .OrderByDescending(item => item.Size ?? 0)
                .FirstOrDefault();
            if (task is null) continue;
            var mirror = DownloadRouteService.GetMirrorUrl(versionId, task.OfficialUrl,
                task.RelativePath, task.Kind);
            if (DownloadRouteService.HasDistinctMirror(task.OfficialUrl, mirror))
                candidates.Add(new DownloadRouteCandidate(task.OfficialUrl, mirror, Path.GetFileName(task.Path)));
        }
        return candidates;
    }

    private static bool IsFileComplete(DownloadTask task)
    {
        if (!File.Exists(task.Path)) return false;
        var length = new FileInfo(task.Path).Length;
        if (length == 0) return false;
        if (task.Size is > 0) return length == task.Size.Value;
        return string.IsNullOrWhiteSpace(task.Sha1);
    }

    public static bool RulesAllow(JsonArray? rules)
    {
        if (rules is null || rules.Count == 0) return true;
        var result = false;
        foreach (var node in rules)
        {
            if (node is not JsonObject rule) continue;
            var os = rule.Obj("os");
            if (os is not null)
            {
                var name = os.Str("name");
                if (!string.IsNullOrWhiteSpace(name) &&
                    !name.Equals("windows", StringComparison.OrdinalIgnoreCase)) continue;
                var arch = os.Str("arch");
                if (!string.IsNullOrWhiteSpace(arch))
                {
                    var currentArch = RuntimeInformation.ProcessArchitecture switch
                    {
                        Architecture.X64 => "x64",
                        Architecture.X86 => "x86",
                        Architecture.Arm64 => "arm64",
                        _ => "unknown"
                    };
                    if (!arch.Equals(currentArch, StringComparison.OrdinalIgnoreCase)) continue;
                }
                if (os.Obj("versionRange") is { } range)
                {
                    if (!WindowsVersionAllowed(range)) continue;
                }
            }

            if (rule.Obj("features") is { } features)
            {
                var supported = features.All(pair => pair.Value?.GetValue<bool>() == false);
                if (!supported) continue;
            }
            result = rule.Str("action")?.Equals("allow", StringComparison.OrdinalIgnoreCase) == true;
        }
        return result;
    }

    private static bool WindowsVersionAllowed(JsonObject range)
    {
        var current = Environment.OSVersion.Version;
        if (Version.TryParse(range.Str("min"), out var min) && current < min) return false;
        if (Version.TryParse(range.Str("max"), out var max) && current >= max) return false;
        return true;
    }

    private static bool IsWindowsNative(string classifier) =>
        classifier.Contains("natives-windows", StringComparison.OrdinalIgnoreCase) &&
        IsNativeArtifactCompatible(classifier);

    public static bool IsNativeArtifactCompatible(string artifact)
    {
        var value = artifact.Replace('\\', '/');
        if (value.Contains("natives-windows-arm64", StringComparison.OrdinalIgnoreCase))
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        if (value.Contains("natives-windows-x86", StringComparison.OrdinalIgnoreCase))
            return RuntimeInformation.ProcessArchitecture == Architecture.X86;
        if (value.Contains("natives-windows-64", StringComparison.OrdinalIgnoreCase))
            return RuntimeInformation.ProcessArchitecture == Architecture.X64;
        if (value.Contains("natives-windows", StringComparison.OrdinalIgnoreCase))
            return RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.X86;
        return true;
    }

    private static void AddTask(
        IDictionary<string, DownloadTask> tasks,
        string url,
        string path,
        string? sha1,
        long? size,
        DownloadFileKind kind,
        string relativePath)
    {
        tasks[path] = new DownloadTask(url, path, sha1, size, kind, relativePath);
    }

    private sealed record DownloadTask(
        string OfficialUrl,
        string Path,
        string? Sha1,
        long? Size,
        DownloadFileKind Kind,
        string RelativePath);

    private sealed record ExternalLibraryTask(
        string RelativePath,
        string OfficialUrl,
        string? Sha1,
        long? Size);

    private sealed class FabricLoaderEntry
    {
        public FabricLoader Loader { get; set; } = new();
    }

    private sealed class FabricLoader
    {
        public string Version { get; set; } = "";
        public bool Stable { get; set; }
    }

    private sealed class QuiltLoaderEntry
    {
        public QuiltLoader Loader { get; set; } = new();
    }

    private sealed class QuiltLoader
    {
        public string Version { get; set; } = "";
        public bool Stable { get; set; }
    }
}
