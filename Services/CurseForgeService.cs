using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Kumo.Models;

namespace Kumo.Services;

public sealed class CurseForgeService
{
    private const string WidgetApiRoot = "https://api.cfwidget.com";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan DiskCacheDuration = TimeSpan.FromHours(2);
    private static readonly JsonSerializerOptions MetadataOptions = new() { WriteIndented = true };
    private readonly HttpService _http;
    private readonly McmodService _mcmod;
    private readonly string _dataDirectory;
    private readonly string _projectCacheDirectory;
    private readonly string _descriptionCacheDirectory;
    private readonly ConcurrentDictionary<string, CacheEntry<CurseForgeProject>> _projectCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _descriptionCache = new();

    public CurseForgeService(HttpService http, McmodService mcmod, string dataDirectory)
    {
        _http = http;
        _mcmod = mcmod;
        _dataDirectory = dataDirectory;
        _projectCacheDirectory = Path.Combine(dataDirectory, "cache", "curseforge-v1");
        _descriptionCacheDirectory = Path.Combine(dataDirectory, "cache", "curseforge-details-v1");
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

        query = query.Trim();
        var targetCount = Math.Clamp(offset + limit, 12, 24);
        var candidates = new List<CurseCandidate>();
        if (query.Length == 0)
        {
            candidates.AddRange(GetPopularSeeds(contentKind)
                .Select(seed => new CurseCandidate(seed.Reference, null, seed.Description)));
        }
        else
        {
            if (contentKind == ContentKind.Mod)
            {
                try
                {
                    var chineseMatches = await _mcmod.SearchWithDetailsAsync(
                        query, targetCount, cancellationToken);
                    candidates.AddRange(chineseMatches
                        .Where(item => !string.IsNullOrWhiteSpace(item.CurseForgeSlug))
                        .Select(item => new CurseCandidate(
                            $"{GetContentSection(contentKind)}/{item.CurseForgeSlug}",
                            item,
                            item.Description)));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogService.Warn($"CurseForge 中文索引匹配失败：{ex.Message}");
                }
            }

            candidates.AddRange(GetPopularSeeds(contentKind)
                .Where(seed => MatchesQuery(seed, query))
                .Select(seed => new CurseCandidate(seed.Reference, null, seed.Description)));

            var directSlug = ToProjectSlug(query);
            if (!string.IsNullOrWhiteSpace(directSlug))
                candidates.Add(new CurseCandidate(
                    $"{GetContentSection(contentKind)}/{directSlug}", null, null));
        }

        var uniqueCandidates = candidates
            .GroupBy(item => item.Reference, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(30)
            .ToList();

        using var gate = new SemaphoreSlim(3);
        var matches = new ConcurrentBag<ModSearchHit>();
        await Task.WhenAll(uniqueCandidates.Select(async candidate =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var project = await GetProjectAsync(candidate.Reference, cancellationToken);
                if (!ProjectMatchesContentKind(project, contentKind)) return;
                var file = FindCompatibleFile(project, gameVersion, loader, contentKind);
                if (file is null) return;
                matches.Add(ToSearchHit(
                    project, candidate.Description, candidate.Mcmod?.ProjectUrl, contentKind));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogService.Warn($"CurseForge 项目 {candidate.Reference} 读取失败：{ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        }));

        var ordered = matches
            .GroupBy(hit => hit.ProjectId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(hit => hit.Downloads)
            .ToList();
        var page = ordered.Skip(offset).Take(limit).ToList();
        return new ModSearchResult(offset, limit, ordered.Count, page);
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

        var project = await GetProjectAsync(GetProjectReference(hit), cancellationToken)
                      ?? throw new InvalidOperationException("无法读取 CurseForge 项目信息。");
        var file = FindCompatibleFile(project, baseGameVersion, loader, hit.ContentKind)
                   ?? throw new InvalidOperationException("没有找到与当前游戏版本和加载器兼容的文件。");
        var safeName = Path.GetFileName(file.Name);
        if (string.IsNullOrWhiteSpace(safeName)) safeName = $"curseforge-{file.Id}.jar";

        var instanceDirectory = instanceDirectoryOverride ??
                                Path.Combine(_dataDirectory, "versions", instanceVersionId);
        var modsDirectory = Path.Combine(instanceDirectory, "mods");
        var metadataDirectory = AppPaths.GetInstanceMetadataDirectory(instanceDirectory);
        Directory.CreateDirectory(modsDirectory);
        Directory.CreateDirectory(metadataDirectory);
        var target = Path.Combine(modsDirectory, safeName);
        var downloadUrl = GetDirectDownloadUrl(file.Id, safeName);
        var resumeOffset = File.Exists(target + ".part") ? new FileInfo(target + ".part").Length : 0;
        var transferClock = Stopwatch.StartNew();
        status($"下载 · {project.Title}");
        await _http.DownloadFileAsync(downloadUrl, target, null, file.FileSize,
            (received, total) =>
            {
                var transferred = Math.Max(0, received - resumeOffset);
                var speed = transferred / Math.Max(0.2, transferClock.Elapsed.TotalSeconds);
                var remaining = speed > 0 && total is > 0 && total.Value > received
                    ? TimeSpan.FromSeconds((total.Value - received) / speed)
                    : (TimeSpan?)null;
                progress?.Report(new DownloadProgress(
                    "正在安装 Mod",
                    received >= (total ?? received) ? 1 : 0,
                    1,
                    safeName,
                    received,
                    total,
                    speed,
                    remaining,
                    "CurseForge"));
            },
            cancellationToken);

        var metadataPath = Path.Combine(metadataDirectory, "curseforge-installed.json");
        var installed = await LoadMetadataAsync(metadataPath, cancellationToken);
        installed.RemoveAll(record => record.ProjectId == project.Id.ToString());
        installed.Add(new InstalledModRecord
        {
            ProjectId = project.Id.ToString(),
            VersionId = file.Id.ToString(),
            Name = project.Title,
            File = safeName,
            InstalledAt = DateTimeOffset.UtcNow
        });
        await SaveMetadataAsync(metadataPath, installed, cancellationToken);
        LogService.Info($"已从 CurseForge 安装 {project.Title}，文件：{safeName}");
        return new ModInstallResult(1, 0, [safeName]);
    }

    public async Task<ResolvedModFile> ResolveCompatibleFileAsync(
        ModSearchHit hit,
        string gameVersion,
        ModLoader loader,
        CancellationToken cancellationToken)
    {
        var project = await GetProjectAsync(GetProjectReference(hit), cancellationToken)
                      ?? throw new InvalidOperationException("无法读取 CurseForge 项目信息。");
        var file = FindCompatibleFile(project, gameVersion, loader, hit.ContentKind)
                   ?? throw new InvalidOperationException("没有找到与目标版本和加载器兼容的文件。");
        var safeName = Path.GetFileName(file.Name);
        if (string.IsNullOrWhiteSpace(safeName)) safeName = $"curseforge-{file.Id}.jar";
        return new ResolvedModFile(
            safeName,
            GetDirectDownloadUrl(file.Id, safeName),
            null,
            file.FileSize,
            "CurseForge");
    }

    public async Task<ResolvedModFile> ResolveLatestFileAsync(
        ModSearchHit hit,
        ModLoader preferredLoader,
        CancellationToken cancellationToken)
    {
        var project = await GetProjectAsync(GetProjectReference(hit), cancellationToken)
                      ?? throw new InvalidOperationException("无法读取 CurseForge 项目信息。");
        var preferred = hit.ContentKind == ContentKind.Mod ||
                        (hit.ContentKind == ContentKind.Modpack &&
                         preferredLoader != ModLoader.Vanilla)
            ? project.Files
                .Where(file => GetLoaderTags(preferredLoader).Any(tag =>
                    file.Versions.Contains(tag, StringComparer.OrdinalIgnoreCase)))
                .ToList()
            : [];
        var file = OrderBestFile(preferred, hit.ContentKind).FirstOrDefault()
                   ?? OrderBestFile(project.Files, hit.ContentKind).FirstOrDefault()
                   ?? throw new InvalidOperationException("这个项目没有可下载文件。");
        var safeName = Path.GetFileName(file.Name);
        if (string.IsNullOrWhiteSpace(safeName)) safeName = $"curseforge-{file.Id}.jar";
        return new ResolvedModFile(
            safeName,
            GetDirectDownloadUrl(file.Id, safeName),
            null,
            file.FileSize,
            "CurseForge");
    }

    public async Task<IReadOnlyList<ModFileChoice>> GetFileChoicesAsync(
        ModSearchHit hit,
        string gameVersion,
        ModLoader loader,
        CancellationToken cancellationToken)
    {
        var project = await GetProjectAsync(GetProjectReference(hit), cancellationToken)
                      ?? throw new InvalidOperationException("无法读取 CurseForge 项目信息。");
        var loaderTags = hit.ContentKind == ContentKind.Mod
            ? GetLoaderTags(loader)
            : [];
        return project.Files
            .Where(file =>
                HasContentExtension(file.Name, hit.ContentKind) &&
                file.Versions.Contains(gameVersion, StringComparer.OrdinalIgnoreCase) &&
                (loaderTags.Length == 0 ||
                 loaderTags.Any(tag =>
                     file.Versions.Contains(tag, StringComparer.OrdinalIgnoreCase))))
            .OrderBy(file => file.ReleaseType switch
            {
                "release" => 0,
                "beta" => 1,
                _ => 2
            })
            .ThenByDescending(file => file.UploadedAt)
            .Select(file =>
            {
                var safeName = Path.GetFileName(file.Name);
                if (string.IsNullOrWhiteSpace(safeName))
                    safeName = $"curseforge-{file.Id}.jar";
                return new ModFileChoice(
                    safeName,
                    safeName,
                    GetDirectDownloadUrl(file.Id, safeName),
                    null,
                    file.FileSize > 0 ? file.FileSize : null,
                    file.ReleaseType switch
                    {
                        "release" => "正式版",
                        "beta" => "Beta",
                        _ => "Alpha"
                    },
                    file.UploadedAt,
                    "CurseForge");
            })
            .ToList();
    }

    public async Task<RemoteModFile?> FindProjectFileAsync(
        string slug,
        string gameVersion,
        ModLoader loader,
        CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(slug, cancellationToken);
        var file = FindCompatibleFile(project, gameVersion, loader);
        if (file is null || !Path.GetExtension(file.Name).Equals(".jar", StringComparison.OrdinalIgnoreCase))
            return null;
        return new RemoteModFile(
            Path.GetFileName(file.Name),
            file.FileSize,
            GetDirectDownloadUrl(file.Id, file.Name));
    }

    public async Task<RemoteModFile?> FindFileByIdAsync(
        long projectId,
        long fileId,
        CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(
            projectId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);
        var file = project.Files.FirstOrDefault(item => item.Id == fileId);
        if (file is null) return null;
        return new RemoteModFile(
            Path.GetFileName(file.Name),
            file.FileSize,
            GetDirectDownloadUrl(file.Id, file.Name));
    }

    public async Task<string> GetProjectDescriptionAsync(
        ModSearchHit hit,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"{hit.ProjectId}|{hit.Slug}";
        if (_descriptionCache.TryGetValue(cacheKey, out var cached)) return cached;
        Directory.CreateDirectory(_descriptionCacheDirectory);
        var safeName = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(cacheKey))).ToLowerInvariant();
        var cachePath = Path.Combine(_descriptionCacheDirectory, safeName + ".txt");
        if (File.Exists(cachePath) &&
            DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(cachePath) < TimeSpan.FromDays(7))
        {
            var diskCache = await File.ReadAllTextAsync(cachePath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(diskCache))
            {
                _descriptionCache[cacheKey] = diskCache;
                return diskCache;
            }
        }

        var project = await GetProjectAsync(GetProjectReference(hit), cancellationToken);
        var description = CleanDescription(project.Description, project.Summary);
        _descriptionCache[cacheKey] = description;
        try
        {
            await File.WriteAllTextAsync(cachePath, description, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warn($"保存 CurseForge 项目说明缓存失败：{ex.Message}");
        }
        return description;
    }

    public static string GetDirectDownloadUrl(long fileId, string fileName)
    {
        var first = fileId / 1000;
        var second = fileId % 1000;
        return $"https://edge.forgecdn.net/files/{first}/{second}/" +
               Uri.EscapeDataString(Path.GetFileName(fileName));
    }

    internal static CurseForgeProjectFile? FindCompatibleFile(
        CurseForgeProject project,
        string? gameVersion,
        ModLoader loader,
        ContentKind contentKind = ContentKind.Mod)
    {
        var loaderTags = contentKind == ContentKind.Mod ||
                         (contentKind == ContentKind.Modpack && loader != ModLoader.Vanilla)
            ? GetLoaderTags(loader)
            : [];
        return project.Files
            .Where(file =>
                HasContentExtension(file.Name, contentKind) &&
                (string.IsNullOrWhiteSpace(gameVersion) ||
                 file.Versions.Contains(gameVersion, StringComparer.OrdinalIgnoreCase)) &&
                (loaderTags.Length == 0 ||
                 loaderTags.Any(tag => file.Versions.Contains(tag, StringComparer.OrdinalIgnoreCase))))
            .OrderBy(file => file.ReleaseType switch
            {
                "release" => 0,
                "beta" => 1,
                _ => 2
            })
            .ThenByDescending(file => file.UploadedAt)
            .FirstOrDefault();
    }

    private static IEnumerable<CurseForgeProjectFile> OrderBestFile(
        IEnumerable<CurseForgeProjectFile> files,
        ContentKind contentKind = ContentKind.Mod) =>
        files
            .Where(file => HasContentExtension(file.Name, contentKind))
            .OrderBy(file => file.ReleaseType switch
            {
                "release" => 0,
                "beta" => 1,
                _ => 2
            })
            .ThenByDescending(file => file.UploadedAt);

    internal static string[] GetLoaderTags(ModLoader loader) => loader switch
    {
        ModLoader.Vanilla => [],
        ModLoader.Fabric => ["Fabric"],
        ModLoader.Quilt => ["Quilt", "Fabric"],
        ModLoader.Forge => ["Forge"],
        ModLoader.NeoForge => ["NeoForge"],
        _ => throw new ArgumentOutOfRangeException(nameof(loader))
    };

    private async Task<CurseForgeProject> GetProjectAsync(string slug, CancellationToken cancellationToken)
    {
        if (_projectCache.TryGetValue(slug, out var cached) &&
            DateTimeOffset.UtcNow - cached.CachedAt < CacheDuration)
            return cached.Value;

        var cachePath = Path.Combine(_projectCacheDirectory, SafeCacheName(slug) + ".json");
        if (File.Exists(cachePath) &&
            DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(cachePath) < DiskCacheDuration)
        {
            try
            {
                var cachedProject = JsonSerializer.Deserialize<CurseForgeProject>(
                    await File.ReadAllTextAsync(cachePath, cancellationToken), HttpService.JsonOptions);
                if (cachedProject is { Id: > 0 } && !string.IsNullOrWhiteSpace(cachedProject.Title))
                {
                    _projectCache[slug] = new CacheEntry<CurseForgeProject>(
                        DateTimeOffset.UtcNow, cachedProject);
                    return cachedProject;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                LogService.Warn($"CurseForge 缓存读取失败：{ex.Message}");
            }
        }

        var trimmedReference = slug.Trim('/');
        var requestPath = trimmedReference.Contains('/', StringComparison.Ordinal)
            ? "minecraft/" + trimmedReference
            : long.TryParse(trimmedReference, out _)
                ? trimmedReference
                : "minecraft/mc-mods/" + trimmedReference;
        var project = await _http.GetJsonAsync<CurseForgeProject>(
            $"{WidgetApiRoot}/{requestPath}", cancellationToken);
        if (project.Id <= 0 || string.IsNullOrWhiteSpace(project.Title))
            throw new InvalidDataException("CurseForge 返回了无效的项目数据。");
        _projectCache[slug] = new CacheEntry<CurseForgeProject>(DateTimeOffset.UtcNow, project);
        try
        {
            Directory.CreateDirectory(_projectCacheDirectory);
            var temporaryPath = cachePath + ".tmp";
            await File.WriteAllTextAsync(
                temporaryPath, JsonSerializer.Serialize(project, MetadataOptions), cancellationToken);
            File.Move(temporaryPath, cachePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warn($"CurseForge 缓存保存失败：{ex.Message}");
        }
        return project;
    }

    private static ModSearchHit ToSearchHit(
        CurseForgeProject project,
        string? chineseDescription,
        string? mcmodUrl,
        ContentKind contentKind)
    {
        var fallbackDescription = string.IsNullOrWhiteSpace(project.Summary)
            ? "暂无说明。"
            : project.Summary;
        if (string.IsNullOrWhiteSpace(chineseDescription))
            McmodService.TryGetKnownDescription(project.Title, out chineseDescription);
        if (string.IsNullOrWhiteSpace(chineseDescription))
            chineseDescription = fallbackDescription;

        var projectUrl = project.Urls?.CurseForge;
        if (string.IsNullOrWhiteSpace(projectUrl))
            projectUrl =
                $"https://www.curseforge.com/minecraft/{GetContentSection(contentKind)}/{project.Slug}";
        return new ModSearchHit
        {
            ProjectId = project.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Slug = project.Slug,
            Title = project.Title,
            Description = chineseDescription,
            Author = project.Members.FirstOrDefault()?.Username ?? string.Empty,
            Downloads = project.Downloads?.Total ?? 0,
            IconUrl = project.Thumbnail,
            Categories = project.Categories,
            Source = ModSource.CurseForge,
            ContentKind = contentKind,
            ProjectUrl = projectUrl,
            CurseForgeUrl = projectUrl,
            McmodUrl = mcmodUrl,
            DownloadUrl = projectUrl,
            CoverUrl = project.Thumbnail,
            FallbackDescription = fallbackDescription,
            HasChineseDescription = Regex.IsMatch(chineseDescription, @"[\u3400-\u9fff]"),
            Status = contentKind == ContentKind.Modpack ? "安装" : "下载"
        };
    }

    private static bool MatchesQuery(CurseForgeSeed seed, string query)
    {
        var normalized = McmodService.NormalizeTitle(query);
        return new[] { seed.Reference, seed.Title, seed.Description }
            .Select(McmodService.NormalizeTitle)
            .Any(value => value.Contains(normalized, StringComparison.Ordinal));
    }

    private static string? ToProjectSlug(string query)
    {
        if (query.Any(character => character > 0x7f)) return null;
        var slug = Regex.Replace(query.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return slug.Length >= 3 ? slug : null;
    }

    private static string CleanDescription(string html, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(html) ? fallback : html;
        if (string.IsNullOrWhiteSpace(value)) return "暂无说明。";
        value = Regex.Replace(value, @"<(script|style)\b[\s\S]*?</\1>", string.Empty,
            RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"</p>", "\n\n", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"<[^>]+>", string.Empty);
        value = WebUtility.HtmlDecode(value);
        value = Regex.Replace(value, @"[ \t]+", " ");
        value = Regex.Replace(value, @"\n{3,}", "\n\n");
        return value.Trim();
    }

    private static string SafeCacheName(string slug) =>
        Regex.Replace(slug.ToLowerInvariant(), @"[^a-z0-9-]", "_");

    internal static string GetContentSection(ContentKind contentKind) => contentKind switch
    {
        ContentKind.Modpack => "modpacks",
        ContentKind.DataPack => "data-packs",
        ContentKind.ResourcePack => "texture-packs",
        ContentKind.Shader => "shaders",
        _ => "mc-mods"
    };

    internal static bool ProjectMatchesContentKind(
        CurseForgeProject project,
        ContentKind contentKind)
    {
        var expected = contentKind switch
        {
            ContentKind.Modpack => "Modpacks",
            ContentKind.DataPack => "Data Packs",
            ContentKind.ResourcePack => "Resource Packs",
            ContentKind.Shader => "Shaders",
            _ => "Mods"
        };
        return string.Equals(project.Type, expected, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool HasContentExtension(string fileName, ContentKind contentKind)
    {
        var extension = Path.GetExtension(fileName);
        return contentKind switch
        {
            ContentKind.Mod => extension.Equals(".jar", StringComparison.OrdinalIgnoreCase),
            ContentKind.Modpack =>
                extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".mrpack", StringComparison.OrdinalIgnoreCase),
            ContentKind.DataPack =>
                extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jar", StringComparison.OrdinalIgnoreCase),
            _ => extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
        };
    }

    private static string GetProjectReference(ModSearchHit hit) =>
        $"{GetContentSection(hit.ContentKind)}/{hit.Slug}";

    private static CurseForgeSeed[] GetPopularSeeds(ContentKind contentKind) =>
        contentKind switch
        {
            ContentKind.Modpack => PopularModpacks,
            ContentKind.DataPack => PopularDataPacks,
            ContentKind.ResourcePack => PopularResourcePacks,
            ContentKind.Shader => PopularShaders,
            _ => PopularMods
        };

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

    private sealed record CurseCandidate(string Reference, McmodModInfo? Mcmod, string? Description);
    private sealed record CacheEntry<T>(DateTimeOffset CachedAt, T Value);

    private sealed record InstalledModRecord
    {
        public string ProjectId { get; set; } = "";
        public string VersionId { get; set; } = "";
        public string Name { get; set; } = "";
        public string File { get; set; } = "";
        public DateTimeOffset InstalledAt { get; set; }
    }

    private sealed record CurseForgeSeed(string Reference, string Title, string Description);

    private static readonly CurseForgeSeed[] PopularMods =
    [
        new("jei", "Just Enough Items",
            "JEI 是一个用于查看物品及配方的模组，从零开始构建，注重稳定性与性能。"),
        new("create", "Create",
            "机械动力以旋转动力、机械结构和自动化建造为核心，提供传动、加工与物流设备。"),
        new("sodium", "Sodium",
            "钠是一款渲染优化模组，通过改进 Minecraft 的渲染逻辑来提升帧率并减少卡顿。"),
        new("appleskin", "AppleSkin",
            "苹果皮在 HUD 中显示饱和度、饥饿值恢复和食物效果等隐藏信息。"),
        new("fabric-api", "Fabric API",
            "Fabric API 是 Fabric 模组生态的基础运行库，提供大多数 Fabric 模组共用的接口。"),
        new("cloth-config", "Cloth Config API",
            "Cloth Config API 为模组提供统一的配置界面构建接口。"),
        new("geckolib", "GeckoLib",
            "GeckoLib 是用于实体、方块和物品动画的模组开发库，许多动画模组依赖它。"),
        new("curios", "Curios API",
            "Curios API 提供可扩展的饰品栏系统，允许模组添加戒指、项链和护符等槽位。"),
        new("applied-energistics-2", "Applied Energistics 2",
            "应用能源2围绕数字化存储、自动合成和网络物流构建大型科技体系。"),
        new("mekanism", "Mekanism",
            "通用机械提供矿物处理、能源生产、机器自动化和高阶装备等科技内容。"),
        new("botania", "Botania",
            "植物魔法以自然魔法为主题，通过花朵、魔力和符文构建科技树。"),
        new("journeymap", "JourneyMap",
            "旅行地图会实时记录探索区域，并支持在游戏内查看地图和路径点。"),
        new("patchouli", "Patchouli",
            "帕秋莉手册提供数据驱动的游戏内指南书，许多模组用它展示说明与教程。"),
        new("iron-chests", "Iron Chests",
            "铁箱子扩展了原版储物箱体系，增加不同材质和更大容量的箱子。"),
        new("waystones", "Waystones",
            "传送石碑允许玩家在已激活的石碑之间传送，并可使用卷轴快速返回。"),
        new("carry-on", "Carry On",
            "搬运允许玩家抬起箱子、熔炉和部分生物，并在移动后重新放下。"),
        new("mouse-tweaks", "Mouse Tweaks",
            "鼠标手势增强物品栏操作，支持批量移动、快速整理和拖拽。"),
        new("terralith", "Terralith",
            "Terralith 大幅扩展主世界地形，加入大量新生物群系、洞穴和地貌。"),
        new("cobblemon", "Cobblemon",
            "Cobblemon 将宝可梦收集、对战和养成内容带入 Minecraft。")
    ];

    private static readonly CurseForgeSeed[] PopularModpacks =
    [
        new("modpacks/all-the-mods-10", "All the Mods 10",
            "A large technology and magic kitchen-sink modpack built around modern Minecraft."),
        new("modpacks/rlcraft", "RLCraft",
            "A hardcore survival pack focused on danger, exploration and progression."),
        new("modpacks/better-mc-forge-bmc1", "Better MC [FORGE] BMC1",
            "A broad adventure and quality-of-life modpack with extra content and performance tuning."),
        new("modpacks/better-mc-forge-bmc4", "Better MC [FORGE] BMC4",
            "A modern Better MC adventure pack with quests, structures and quality-of-life mods."),
        new("modpacks/dawn-craft", "DawnCraft",
            "An RPG-style adventure pack with custom combat, quests and world progression."),
        new("modpacks/cottage-witch", "Cottage Witch",
            "A cozy magic and building modpack centered on farming, decoration and exploration.")
    ];

    private static readonly CurseForgeSeed[] PopularDataPacks =
    [
        new("data-packs/spawn-animations", "Spawn Animations",
            "Adds atmospheric animations when hostile mobs appear in the world."),
        new("data-packs/trek", "Trek",
            "A world-generation data pack that expands exploration with new terrain and structures."),
        new("data-packs/geophilic", "Geophilic",
            "Improves vanilla world generation with richer biomes, terrain and natural details.")
    ];

    private static readonly CurseForgeSeed[] PopularResourcePacks =
    [
        new("texture-packs/faithful-64x", "Faithful 64x",
            "A higher-resolution interpretation of the vanilla Minecraft art style."),
        new("texture-packs/roundista", "Roundista",
            "A clean rounded resource pack with support for PBR materials."),
        new("texture-packs/motschens-better-leaves", "Motschen's Better Leaves",
            "Adds fuller connected leaves while keeping the original Minecraft look."),
        new("texture-packs/fresh-moves", "Fresh Moves",
            "Adds detailed animated player and mob movement while preserving the vanilla style."),
        new("texture-packs/fast-better-grass", "Fast Better Grass",
            "Creates fuller grass without the performance cost of complex models."),
        new("texture-packs/jicklus", "JICKLUS",
            "A hand-drawn medieval fantasy resource pack for Minecraft."),
        new("texture-packs/pixel-perfection", "Pixel Perfection",
            "A colorful and detailed pixel-art resource pack based on the vanilla style."),
        new("texture-packs/vanilla-pbr", "Vanilla PBR",
            "Adds physically based materials to the vanilla textures for shader users."),
        new("texture-packs/dandelion", "Dandelion",
            "A bright 16x resource pack with a soft, storybook-inspired palette.")
    ];

    private static readonly CurseForgeSeed[] PopularShaders =
    [
        new("shaders/complementary-reimagined", "Complementary Shaders - Reimagined",
            "A polished shader pack balancing vivid lighting, performance and compatibility."),
        new("shaders/complementary-unbound", "Complementary Shaders - Unbound",
            "A flexible shader pack with extensive visual options for different play styles."),
        new("shaders/bsl-shaders", "BSL Shaders",
            "A popular shader pack with colorful lighting, shadows and atmospheric effects."),
        new("shaders/makeup-ultra-fast-shader", "MakeUp - Ultra Fast",
            "A performance-focused shader pack with a stylized, colorful appearance."),
        new("shaders/sildurs-vibrant-shaders", "Sildur's Vibrant Shaders",
            "A long-running shader pack known for vibrant colors and scalable quality settings."),
        new("shaders/photon-shader", "Photon Shader",
            "A modern shader pack focused on detailed lighting, atmosphere and visual polish.")
    ];
}

internal sealed class CurseForgeProject
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("urls")]
    public CurseForgeUrls? Urls { get; set; }

    [JsonPropertyName("thumbnail")]
    public string? Thumbnail { get; set; }

    [JsonPropertyName("downloads")]
    public CurseForgeDownloads? Downloads { get; set; }

    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = [];

    [JsonPropertyName("members")]
    public List<CurseForgeMember> Members { get; set; } = [];

    [JsonPropertyName("files")]
    public List<CurseForgeProjectFile> Files { get; set; } = [];

    [JsonIgnore]
    public string Slug => Urls?.CurseForge?.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ??
                          Title.ToLowerInvariant().Replace(' ', '-');
}

internal sealed class CurseForgeUrls
{
    [JsonPropertyName("curseforge")]
    public string? CurseForge { get; set; }
}

internal sealed class CurseForgeDownloads
{
    [JsonPropertyName("total")]
    public long Total { get; set; }
}

internal sealed class CurseForgeMember
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;
}

internal sealed class CurseForgeProjectFile
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string ReleaseType { get; set; } = string.Empty;

    [JsonPropertyName("filesize")]
    public long FileSize { get; set; }

    [JsonPropertyName("versions")]
    public List<string> Versions { get; set; } = [];

    [JsonPropertyName("uploaded_at")]
    public DateTimeOffset UploadedAt { get; set; }
}
