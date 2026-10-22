using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kumo.Models;

namespace Kumo.Services;

public sealed record ResolvedModFile(
    string FileName,
    string Url,
    string? Sha1,
    long? Size,
    string SourceLabel);

public sealed class ModrinthService
{
    private const string ApiRoot = "https://api.modrinth.com/v2";
    private readonly HttpService _http;
    private readonly string _dataDirectory;
    private readonly string _descriptionCacheDirectory;
    private readonly ConcurrentDictionary<string, string> _descriptionCache = new();
    private static readonly JsonSerializerOptions MetadataOptions = new() { WriteIndented = true };

    public ModrinthService(HttpService http, string dataDirectory)
    {
        _http = http;
        _dataDirectory = dataDirectory;
        _descriptionCacheDirectory = Path.Combine(dataDirectory, "cache", "modrinth-details-v1");
    }

    public async Task<ModSearchResult> SearchAsync(
        string query,
        string? gameVersion,
        ModLoader loader,
        int offset,
        int limit = 20,
        CancellationToken cancellationToken = default,
        ContentKind contentKind = ContentKind.Mod)
    {
        if (contentKind == ContentKind.Mod && loader == ModLoader.Vanilla)
            throw new InvalidOperationException("请先选择一个带 Mod 加载器的游戏版本。");
        var facets = new List<object>
        {
            new[] { "project_type:" + ContentKindFacet(contentKind) }
        };
        if (contentKind == ContentKind.Mod ||
            (contentKind == ContentKind.Modpack && loader != ModLoader.Vanilla))
            facets.Add(GetLoaderFacets(loader));
        if (!string.IsNullOrWhiteSpace(gameVersion))
            facets.Add(new[] { "versions:" + gameVersion });
        var index = string.IsNullOrWhiteSpace(query) ? "downloads" : "relevance";
        var url =
            $"{ApiRoot}/search?query={Uri.EscapeDataString(query.Trim())}&limit={limit}&offset={offset}&index={index}&facets={Uri.EscapeDataString(JsonSerializer.Serialize(facets))}";
        var response = await _http.GetJsonAsync<SearchResponse>(url, cancellationToken);
        foreach (var hit in response.Hits)
        {
            hit.Source = ModSource.Modrinth;
            hit.ContentKind = contentKind;
            hit.ProjectUrl = $"https://modrinth.com/{ContentKindFacet(contentKind)}/{hit.Slug}";
            hit.ModrinthUrl = hit.ProjectUrl;
            hit.DownloadUrl = hit.ProjectUrl;
            hit.CoverUrl = hit.IconUrl ?? hit.Gallery.FirstOrDefault();
            hit.Status = hit.PrimaryActionLabel;
        }
        return new ModSearchResult(response.Offset, response.Limit, response.TotalHits, response.Hits);
    }

    public async Task<string> GetProjectDescriptionAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        if (_descriptionCache.TryGetValue(projectId, out var cached)) return cached;
        Directory.CreateDirectory(_descriptionCacheDirectory);
        var safeName = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(projectId))).ToLowerInvariant();
        var cachePath = Path.Combine(_descriptionCacheDirectory, safeName + ".txt");
        if (File.Exists(cachePath) &&
            DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(cachePath) < TimeSpan.FromDays(7))
        {
            var diskCache = await File.ReadAllTextAsync(cachePath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(diskCache))
            {
                _descriptionCache[projectId] = diskCache;
                return diskCache;
            }
        }

        var project = await _http.GetJsonAsync<ProjectDetailsResponse>(
            $"{ApiRoot}/project/{Uri.EscapeDataString(projectId)}", cancellationToken);
        var description = CleanDescription(project.Body, project.Description);
        _descriptionCache[projectId] = description;
        try
        {
            await File.WriteAllTextAsync(cachePath, description, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warn($"保存 Modrinth 项目说明缓存失败：{ex.Message}");
        }
        return description;
    }

    private static string CleanDescription(string body, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(body) ? fallback : body;
        if (string.IsNullOrWhiteSpace(value)) return "暂无说明。";
        value = Regex.Replace(value, @"<(script|style)\b[\s\S]*?</\1>", string.Empty,
            RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"</p>", "\n\n", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"<[^>]+>", string.Empty);
        value = WebUtility.HtmlDecode(value);
        value = Regex.Replace(value, @"!\[[^\]]*\]\([^)]+\)", string.Empty);
        value = Regex.Replace(value, @"\[([^\]]+)\]\([^)]+\)", "$1");
        value = Regex.Replace(value, @"(?m)^\s{0,3}#{1,6}\s*", string.Empty);
        value = Regex.Replace(value, @"[*_`~]{1,3}", string.Empty);
        value = Regex.Replace(value, @"[ \t]+", " ");
        value = Regex.Replace(value, @"\n{3,}", "\n\n");
        return value.Trim();
    }

    public async Task<ModVersion?> FindCompatibleVersionAsync(
        string projectId,
        string gameVersion,
        ModLoader loader,
        CancellationToken cancellationToken = default,
        ContentKind contentKind = ContentKind.Mod)
    {
        var queryParts = new List<string>
        {
            "game_versions=" + Uri.EscapeDataString(
                JsonSerializer.Serialize(new[] { gameVersion }))
        };
        if (contentKind == ContentKind.Mod ||
            (contentKind == ContentKind.Modpack && loader != ModLoader.Vanilla))
        {
            var loaders = GetLoaderNames(loader);
            queryParts.Insert(0,
                "loaders=" + Uri.EscapeDataString(
                    JsonSerializer.Serialize(loaders)));
        }
        var query = "?" + string.Join("&", queryParts);
        var versions = await _http.GetJsonAsync<List<ModVersion>>(
            $"{ApiRoot}/project/{Uri.EscapeDataString(projectId)}/version{query}", cancellationToken);
        return PickBestVersion(versions);
    }

    public async Task<ResolvedModFile> ResolveCompatibleFileAsync(
        ModSearchHit hit,
        string gameVersion,
        ModLoader loader,
        CancellationToken cancellationToken)
    {
        var version = await FindCompatibleVersionAsync(
            hit.ProjectId, gameVersion, loader, cancellationToken, hit.ContentKind)
            ?? throw new InvalidOperationException("没有找到与目标游戏版本兼容的文件。");
        var file = version.Files.FirstOrDefault(item => item.Primary)
                   ?? version.Files.FirstOrDefault()
                   ?? throw new InvalidOperationException("Modrinth 没有返回可下载文件。");
        return new ResolvedModFile(
            Path.GetFileName(file.Filename),
            file.Url,
            file.Hashes.Sha1,
            file.Size,
            "Modrinth");
    }

    public async Task<ResolvedModFile> ResolveLatestFileAsync(
        ModSearchHit hit,
        ModLoader preferredLoader,
        CancellationToken cancellationToken)
    {
        var versions = await _http.GetJsonAsync<List<ModVersion>>(
            $"{ApiRoot}/project/{Uri.EscapeDataString(hit.ProjectId)}/version",
            cancellationToken);
        var preferredNames = GetLoaderNames(preferredLoader);
        var preferred = hit.ContentKind == ContentKind.Mod ||
                        (hit.ContentKind == ContentKind.Modpack &&
                         preferredLoader != ModLoader.Vanilla)
            ? versions
                .Where(version => version.Loaders.Any(loader =>
                    preferredNames.Contains(loader, StringComparer.OrdinalIgnoreCase)))
                .ToList()
            : [];
        var version = PickBestVersion(preferred) ?? PickBestVersion(versions)
                      ?? throw new InvalidOperationException("这个项目没有可下载文件。");
        var file = version.Files.FirstOrDefault(item => item.Primary)
                   ?? version.Files.FirstOrDefault()
                   ?? throw new InvalidOperationException("这个项目没有可下载文件。");
        return new ResolvedModFile(
            Path.GetFileName(file.Filename),
            file.Url,
            file.Hashes.Sha1,
            file.Size,
            "Modrinth");
    }

    public async Task<IReadOnlyList<ModFileChoice>> GetFileChoicesAsync(
        ModSearchHit hit,
        string gameVersion,
        ModLoader loader,
        CancellationToken cancellationToken)
    {
        var versions = await _http.GetJsonAsync<List<ModVersion>>(
            $"{ApiRoot}/project/{Uri.EscapeDataString(hit.ProjectId)}/version",
            cancellationToken);
        var loaderNames = hit.ContentKind == ContentKind.Mod
            ? GetLoaderNames(loader)
            : [];
        return versions
            .Where(version =>
                version.GameVersions.Contains(gameVersion, StringComparer.OrdinalIgnoreCase) &&
                (loaderNames.Length == 0 ||
                 version.Loaders.Any(item =>
                     loaderNames.Contains(item, StringComparer.OrdinalIgnoreCase))))
            .OrderBy(version => version.VersionType switch
            {
                "release" => 0,
                "beta" => 1,
                _ => 2
            })
            .ThenByDescending(version => version.DatePublished)
            .Select(version =>
            {
                var file = version.Files.FirstOrDefault(item => item.Primary)
                           ?? version.Files.FirstOrDefault();
                if (file is null) return null;
                return new ModFileChoice(
                    version.VersionNumber,
                    Path.GetFileName(file.Filename),
                    file.Url,
                    file.Hashes.Sha1,
                    file.Size,
                    version.VersionType switch
                    {
                        "release" => "正式版",
                        "beta" => "Beta",
                        _ => "Alpha"
                    },
                    version.DatePublished,
                    "Modrinth");
            })
            .Where(choice => choice is not null)
            .Cast<ModFileChoice>()
            .ToList();
    }

    public async Task<ModInstallResult> InstallAsync(
        ModSearchHit hit,
        string instanceVersionId,
        string baseGameVersion,
        ModLoader loader,
        Action<string> status,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken,
        string? instanceDirectoryOverride = null)
    {
        if (loader == ModLoader.Vanilla)
            throw new InvalidOperationException("原版无法加载 Mod，请先安装 Mod 加载器版本。");

        var instanceDirectory = instanceDirectoryOverride ??
                                Path.Combine(_dataDirectory, "versions", instanceVersionId);
        var modsDirectory = Path.Combine(instanceDirectory, "mods");
        var metadataDirectory = AppPaths.GetInstanceMetadataDirectory(instanceDirectory);
        Directory.CreateDirectory(modsDirectory);
        Directory.CreateDirectory(metadataDirectory);
        var metadataPath = Path.Combine(metadataDirectory, "modrinth-installed.json");
        var installed = await LoadMetadataAsync(metadataPath, cancellationToken);
        var files = new List<string>();
        var dependencyCount = 0;

        async Task InstallVersionAsync(ModVersion version, bool dependency)
        {
            var file = version.Files.FirstOrDefault(x => x.Primary) ?? version.Files.FirstOrDefault();
            if (file is null || string.IsNullOrWhiteSpace(file.Url) ||
                string.IsNullOrWhiteSpace(file.Filename)) return;
            var safeName = Path.GetFileName(file.Filename);
            if (string.IsNullOrWhiteSpace(safeName)) safeName = version.Id + ".jar";
            var target = Path.Combine(modsDirectory, safeName);
            var resumeOffset = File.Exists(target + ".part") ? new FileInfo(target + ".part").Length : 0;
            var transferClock = Stopwatch.StartNew();
            status(dependency ? $"安装前置 · {version.Name}" : $"下载 · {version.Name}");
            await _http.DownloadFileAsync(file.Url, target, file.Hashes.Sha1, file.Size,
                (received, total) =>
                {
                    var transferred = Math.Max(0, received - resumeOffset);
                    var speed = transferred / Math.Max(0.2, transferClock.Elapsed.TotalSeconds);
                    var remaining = speed > 0 && total is > 0 && total.Value > received
                        ? TimeSpan.FromSeconds((total.Value - received) / speed)
                        : (TimeSpan?)null;
                    progress?.Report(new DownloadProgress(
                        dependency ? "正在安装前置 Mod" : "正在安装 Mod",
                        received >= (total ?? received) ? 1 : 0,
                        1,
                        safeName,
                        received,
                        total,
                        speed,
                        remaining,
                        "Modrinth"));
                },
                cancellationToken, verifyExistingHash: true);
            files.Add(safeName);
            if (dependency) dependencyCount++;

            installed.RemoveAll(x => x.ProjectId == version.ProjectId);
            installed.Add(new InstalledModRecord
            {
                ProjectId = version.ProjectId,
                VersionId = version.Id,
                Name = version.Name,
                File = safeName,
                InstalledAt = DateTimeOffset.UtcNow
            });
            await SaveMetadataAsync(metadataPath, installed, cancellationToken);
        }

        var rootVersion = await FindCompatibleVersionAsync(hit.ProjectId, baseGameVersion, loader, cancellationToken)
                          ?? throw new InvalidOperationException("没有找到与当前游戏版本和加载器兼容的文件。");
        status($"安装 · {hit.Title}");
        await InstallVersionAsync(rootVersion, false);

        var visitedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { hit.ProjectId };
        var queue = new Queue<ModVersion>();
        queue.Enqueue(rootVersion);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var required = current.Dependencies.Where(x => x.DependencyType == "required").ToList();
            foreach (var dependency in required)
            {
                if (!string.IsNullOrWhiteSpace(dependency.ProjectId) &&
                    visitedProjects.Contains(dependency.ProjectId)) continue;
                ModVersion? version = null;
                if (!string.IsNullOrWhiteSpace(dependency.VersionId))
                {
                    try
                    {
                        version = await _http.GetJsonAsync<ModVersion>(
                            $"{ApiRoot}/version/{Uri.EscapeDataString(dependency.VersionId)}", cancellationToken);
                    }
                    catch (HttpRequestException ex)
                    {
                        LogService.Warn($"指定前置 {dependency.VersionId} 获取失败：{ex.Message}");
                    }
                }
                if (version is null && !string.IsNullOrWhiteSpace(dependency.ProjectId))
                {
                    version = await FindCompatibleVersionAsync(dependency.ProjectId, baseGameVersion, loader,
                        cancellationToken);
                }
                if (version is null) continue;
                if (!string.IsNullOrWhiteSpace(version.ProjectId) &&
                    !visitedProjects.Add(version.ProjectId))
                    continue;
                await InstallVersionAsync(version, true);
                queue.Enqueue(version);
            }
        }

        LogService.Info($"已安装 Mod {hit.Title}，文件：{string.Join("、", files)}");
        return new ModInstallResult(files.Count - dependencyCount, dependencyCount, files);
    }

    public async Task<string> GetModsDirectoryAsync(string versionId)
    {
        await Task.CompletedTask;
        return Path.Combine(_dataDirectory, "versions", versionId, "mods");
    }

    public static string[] GetLoaderFacets(ModLoader loader) => loader switch
    {
        ModLoader.Vanilla => [],
        ModLoader.Fabric => ["categories:fabric", "categories:quilt"],
        ModLoader.Forge => ["categories:forge"],
        ModLoader.NeoForge => ["categories:neoforge"],
        ModLoader.Quilt => ["categories:quilt", "categories:fabric"],
        _ => throw new ArgumentOutOfRangeException(nameof(loader))
    };

    public static string ContentKindFacet(ContentKind kind) => kind switch
    {
        ContentKind.Modpack => "modpack",
        ContentKind.DataPack => "datapack",
        ContentKind.ResourcePack => "resourcepack",
        ContentKind.Shader => "shader",
        _ => "mod"
    };

    public static string[] GetLoaderNames(ModLoader loader) => loader switch
    {
        ModLoader.Vanilla => [],
        ModLoader.Fabric => ["fabric", "quilt"],
        ModLoader.Forge => ["forge"],
        ModLoader.NeoForge => ["neoforge"],
        ModLoader.Quilt => ["quilt", "fabric"],
        _ => throw new ArgumentOutOfRangeException(nameof(loader))
    };

    private static ModVersion? PickBestVersion(IEnumerable<ModVersion> versions)
    {
        return versions
            .OrderBy(x => x.VersionType switch { "release" => 0, "beta" => 1, _ => 2 })
            .ThenByDescending(x => x.DatePublished)
            .FirstOrDefault();
    }

    private static async Task<List<InstalledModRecord>> LoadMetadataAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<InstalledModRecord>>(
                       await File.ReadAllTextAsync(path, cancellationToken), MetadataOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static async Task SaveMetadataAsync(
        string path,
        List<InstalledModRecord> records,
        CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(records, MetadataOptions), cancellationToken);
    }

    private sealed class SearchResponse
    {
        [JsonPropertyName("offset")]
        public int Offset { get; set; }

        [JsonPropertyName("limit")]
        public int Limit { get; set; }

        [JsonPropertyName("total_hits")]
        public int TotalHits { get; set; }

        [JsonPropertyName("hits")]
        public List<ModSearchHit> Hits { get; set; } = [];
    }

    private sealed class ProjectDetailsResponse
    {
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("body")]
        public string Body { get; set; } = string.Empty;
    }

    private sealed class InstalledModRecord
    {
        public string ProjectId { get; set; } = "";
        public string VersionId { get; set; } = "";
        public string Name { get; set; } = "";
        public string File { get; set; } = "";
        public DateTimeOffset InstalledAt { get; set; }
    }
}
