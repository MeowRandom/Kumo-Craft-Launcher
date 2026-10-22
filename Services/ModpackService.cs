using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Kumo.Models;

namespace Kumo.Services;

public sealed record ModpackFilePlan(
    string RelativePath,
    string? Url,
    string? Sha1,
    long? Size,
    long? CurseProjectId = null,
    long? CurseFileId = null);

public sealed record ModpackInstallPlan(
    string Name,
    string Version,
    string MinecraftVersion,
    ModLoader Loader,
    string? LoaderVersion,
    string Source,
    string ArchivePath,
    IReadOnlyList<string> OverrideRoots,
    IReadOnlyList<ModpackFilePlan> Files);

public sealed class ModpackService
{
    private readonly HttpService _http;
    private readonly CurseForgeService _curseForge;
    private readonly string _dataDirectory;
    private readonly string _modpackCacheDirectory;

    public ModpackService(
        HttpService http,
        CurseForgeService curseForge,
        string dataDirectory)
    {
        _http = http;
        _curseForge = curseForge;
        _dataDirectory = dataDirectory;
        _modpackCacheDirectory = Path.Combine(dataDirectory, "cache", "modpacks-v1");
    }

    public Task<ModpackInstallPlan> InspectAsync(string archivePath, CancellationToken cancellationToken) =>
        Task.Run(() => Inspect(archivePath), cancellationToken);

    public async Task InstallContentsAsync(
        ModpackInstallPlan plan,
        string instanceVersionId,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken,
        string? instanceDirectoryOverride = null)
    {
        var instanceDirectory = instanceDirectoryOverride ??
                                Path.Combine(_dataDirectory, "versions", instanceVersionId);
        Directory.CreateDirectory(instanceDirectory);
        for (var index = 0; index < plan.Files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = plan.Files[index];
            var relative = NormalizeRelativePath(file.RelativePath);
            var target = GetSafeChildPath(instanceDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            var url = file.Url;
            var size = file.Size;
            if (string.IsNullOrWhiteSpace(url) && file.CurseProjectId is { } projectId &&
                file.CurseFileId is { } fileId)
            {
                var remote = await _curseForge.FindFileByIdAsync(projectId, fileId, cancellationToken)
                             ?? throw new InvalidDataException(
                                 $"无法解析 CurseForge 文件 {projectId}/{fileId}。");
                url = remote.Url;
                size = remote.Size > 0 ? remote.Size : null;
            }
            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidDataException($"整合包文件缺少下载地址：{relative}");

            var completed = index;
            progress?.Report(new DownloadProgress(
                "正在安装整合包文件", completed, plan.Files.Count,
                Path.GetFileName(relative), 0, size, 0, null, plan.Source));
            try
            {
                await _http.DownloadFileAsync(
                    url,
                    target,
                    file.Sha1,
                    size,
                    (received, total) => progress?.Report(new DownloadProgress(
                        "正在安装整合包文件", completed, plan.Files.Count,
                        Path.GetFileName(relative), received, total, 0, null, plan.Source)),
                    cancellationToken,
                    verifyExistingHash: !string.IsNullOrWhiteSpace(file.Sha1));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new IOException($"下载整合包文件失败：{relative}", ex);
            }
        }

        progress?.Report(new DownloadProgress(
            "正在写入整合包配置", plan.Files.Count, plan.Files.Count,
            "overrides", 0, null, 0, null, plan.Source));
        await Task.Run(() => ExtractOverrides(plan.ArchivePath, plan.OverrideRoots, instanceDirectory),
            cancellationToken);
    }

    public static string CreateInstanceId(ModpackInstallPlan plan)
    {
        var slug = new string(plan.Name
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray())
            .Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-");
        if (slug.Length == 0) slug = "modpack";
        if (slug.Length > 48) slug = slug[..48].Trim('-');
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(
                    $"{plan.Name}|{plan.Version}|{plan.MinecraftVersion}|{plan.Loader}|{plan.LoaderVersion}")))
            [..8].ToLowerInvariant();
        return $"{slug}-{plan.MinecraftVersion}-{plan.Loader.ToString().ToLowerInvariant()}-{hash}";
    }

    private ModpackInstallPlan Inspect(string archivePath)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("找不到整合包文件。", archivePath);
        if (!Path.GetExtension(archivePath).Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetExtension(archivePath).Equals(".mrpack", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("仅支持 .mrpack 或 CurseForge 整合包 .zip。");

        using var archive = ZipFile.OpenRead(archivePath);
        var modrinthEntry = archive.GetEntry("modrinth.index.json");
        if (modrinthEntry is not null)
            return InspectModrinth(archivePath, archive, modrinthEntry);
        var manifestEntry = archive.GetEntry("manifest.json");
        if (manifestEntry is not null)
            return InspectCurseForge(archivePath, archive, manifestEntry);
        var nestedModpack = archive.Entries.FirstOrDefault(entry =>
            entry.Name.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase));
        if (nestedModpack is not null)
        {
            var nestedPath = MaterializeNestedModpack(archivePath, nestedModpack);
            using var nestedArchive = ZipFile.OpenRead(nestedPath);
            var nestedIndex = nestedArchive.GetEntry("modrinth.index.json")
                              ?? throw new InvalidDataException(
                                  "PCL2 安装包中的 mrpack 缺少 modrinth.index.json。");
            return InspectModrinth(nestedPath, nestedArchive, nestedIndex);
        }
        throw new InvalidDataException("压缩包中缺少 modrinth.index.json 或 CurseForge manifest.json。");
    }

    private string MaterializeNestedModpack(
        string outerArchivePath,
        ZipArchiveEntry nestedEntry)
    {
        var info = new FileInfo(outerArchivePath);
        var keySource = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{nestedEntry.FullName}";
        var key = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(keySource))).ToLowerInvariant();
        var directory = Path.Combine(_modpackCacheDirectory, "nested");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, key + ".mrpack");
        if (!File.Exists(destination) || new FileInfo(destination).Length != nestedEntry.Length)
            nestedEntry.ExtractToFile(destination, true);
        return destination;
    }

    private static ModpackInstallPlan InspectModrinth(
        string archivePath,
        ZipArchive archive,
        ZipArchiveEntry indexEntry)
    {
        var root = ReadJson(indexEntry, "Modrinth 整合包索引");
        var dependencies = root.Obj("dependencies") ?? new JsonObject();
        var minecraftVersion = dependencies.Str("minecraft")
                               ?? throw new InvalidDataException("整合包没有声明 Minecraft 版本。");
        var (loader, loaderVersion) = DetectLoader(dependencies);
        var files = new List<ModpackFilePlan>();
        if (root.Arr("files") is { } fileArray)
        {
            foreach (var node in fileArray.OfType<JsonObject>())
            {
                if (string.Equals(node.Obj("env")?.Str("client"), "unsupported",
                        StringComparison.OrdinalIgnoreCase)) continue;
                var path = node.Str("path");
                var downloads = node.Arr("downloads")?.OfType<JsonValue>()
                    .Select(value => value.GetValue<string>())
                    .FirstOrDefault(url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(downloads)) continue;
                files.Add(new ModpackFilePlan(
                    NormalizeRelativePath(path),
                    downloads,
                    node.Obj("hashes")?.Str("sha1"),
                    node["fileSize"]?.GetValue<long>()));
            }
        }
        var overrides = new[] { "overrides", "client-overrides" }
            .Where(prefix => archive.Entries.Any(entry =>
                entry.FullName.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return new ModpackInstallPlan(
            root.Str("name") ?? Path.GetFileNameWithoutExtension(archivePath),
            root.Str("versionId") ?? "1.0",
            minecraftVersion,
            loader,
            loaderVersion,
            "Modrinth",
            archivePath,
            overrides,
            files);
    }

    private static ModpackInstallPlan InspectCurseForge(
        string archivePath,
        ZipArchive archive,
        ZipArchiveEntry manifestEntry)
    {
        var root = ReadJson(manifestEntry, "CurseForge 整合包清单");
        var minecraft = root.Obj("minecraft")
                        ?? throw new InvalidDataException("CurseForge 整合包缺少 minecraft 配置。");
        var minecraftVersion = minecraft.Str("version")
                               ?? throw new InvalidDataException("整合包没有声明 Minecraft 版本。");
        var loaderId = minecraft.Arr("modLoaders")?.OfType<JsonObject>()
                           .OrderByDescending(item => item.Bool("primary"))
                           .Select(item => item.Str("id"))
                           .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                       ?? "";
        var (loader, loaderVersion) = ParseCurseLoader(loaderId);
        var files = new List<ModpackFilePlan>();
        if (root.Arr("files") is { } fileArray)
        {
            foreach (var node in fileArray.OfType<JsonObject>())
            {
                if (!node.Bool("required", true)) continue;
                var projectId = node["projectID"]?.GetValue<long>();
                var fileId = node["fileID"]?.GetValue<long>();
                if (projectId is null || fileId is null) continue;
                files.Add(new ModpackFilePlan(
                    "mods/pending-" + fileId + ".jar",
                    null,
                    null,
                    null,
                    projectId,
                    fileId));
            }
        }
        var overrides = root.Str("overrides") ?? "overrides";
        var roots = archive.Entries.Any(entry =>
            entry.FullName.StartsWith(overrides + "/", StringComparison.OrdinalIgnoreCase))
            ? new[] { overrides }
            : [];
        return new ModpackInstallPlan(
            root.Str("name") ?? Path.GetFileNameWithoutExtension(archivePath),
            root.Str("version") ?? "1.0",
            minecraftVersion,
            loader,
            loaderVersion,
            "CurseForge",
            archivePath,
            roots,
            files);
    }

    private static (ModLoader Loader, string? Version) DetectLoader(JsonObject dependencies)
    {
        if (dependencies.Str("fabric-loader") is { } fabric) return (ModLoader.Fabric, fabric);
        if (dependencies.Str("quilt-loader") is { } quilt) return (ModLoader.Quilt, quilt);
        if (dependencies.Str("neoforge") is { } neoForge) return (ModLoader.NeoForge, neoForge);
        if (dependencies.Str("forge") is { } forge) return (ModLoader.Forge, forge);
        return (ModLoader.Vanilla, null);
    }

    private static (ModLoader Loader, string? Version) ParseCurseLoader(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return (ModLoader.Vanilla, null);
        if (id.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase))
            return (ModLoader.NeoForge, id["neoforge-".Length..]);
        if (id.StartsWith("forge-", StringComparison.OrdinalIgnoreCase))
            return (ModLoader.Forge, id["forge-".Length..]);
        if (id.StartsWith("fabric-", StringComparison.OrdinalIgnoreCase))
            return (ModLoader.Fabric, id["fabric-".Length..]);
        if (id.StartsWith("quilt-", StringComparison.OrdinalIgnoreCase))
            return (ModLoader.Quilt, id["quilt-".Length..]);
        return (ModLoader.Vanilla, null);
    }

    private static void ExtractOverrides(
        string archivePath,
        IReadOnlyList<string> roots,
        string instanceDirectory)
    {
        if (roots.Count == 0) return;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var root in roots)
        {
            var prefix = root.TrimEnd('/') + "/";
            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var relative = entry.FullName[prefix.Length..].Replace('\\', '/');
                if (string.IsNullOrWhiteSpace(relative) || relative.EndsWith('/')) continue;
                var target = GetSafeChildPath(instanceDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, true);
            }
        }
    }

    private static JsonObject ReadJson(ZipArchiveEntry entry, string description)
    {
        using var stream = entry.Open();
        return JsonNode.Parse(stream) as JsonObject
               ?? throw new InvalidDataException(description + "格式无效。");
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static string GetSafeChildPath(string root, string relative)
    {
        var normalizedRelative = NormalizeRelativePath(relative);
        var target = Path.GetFullPath(Path.Combine(root, normalizedRelative));
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("整合包包含不安全的文件路径。");
        return target;
    }
}
