using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Kumo.Models;

namespace Kumo.Services;

public sealed class McmodService
{
    private const string SiteRoot = "https://www.mcmod.cn";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);
    private static readonly Regex ClassLinkRegex =
        new(@"/class/(?<id>\d+)\.html", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex MarkupTokenRegex = new(@"\[[^\]]{1,80}\]", RegexOptions.Compiled);
    private static readonly Regex RepeatedWhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex AnchorRegex =
        new(@"<a\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HrefRegex =
        new(@"href\s*=\s*[""'](?<href>[^""']+)[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CurseForgePathRegex =
        new(@"curseforge\.com/(?:minecraft/)?mc-mods/(?<slug>[a-z0-9][a-z0-9-]*)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ModrinthPathRegex =
        new(@"modrinth\.com/(?:mod|project)/(?<slug>[a-z0-9][a-z0-9-]*)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CoverRegex =
        new(@"class-cover-image[\s\S]{0,600}?<img\b[^>]*src=[""'](?<src>[^""']+)[""']",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ChineseTextRegex =
        new(@"[\u3400-\u9fff]", RegexOptions.Compiled);

    private readonly HttpService _http;
    private readonly ConcurrentDictionary<string, CacheEntry<List<McmodModInfo>>> _searchCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CacheEntry<McmodModInfo>> _detailCache = new();
    private readonly ConcurrentDictionary<string, CachedDescription> _descriptionCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _searchGate = new(1, 1);
    private readonly SemaphoreSlim _descriptionCacheWriteGate = new(1, 1);
    private readonly string _descriptionCachePath;
    private DateTimeOffset _lastSearchAt = DateTimeOffset.MinValue;

    public McmodService(HttpService http, string dataDirectory)
    {
        _http = http;
        _descriptionCachePath = Path.Combine(
            dataDirectory, "cache", "mcmod-descriptions-v1.json");
        LoadDescriptionCache();
    }

    public async Task<IReadOnlyList<McmodModInfo>> SearchAsync(
        string query,
        int limit = 20,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        query = query.Trim();
        if (query.Length == 0) return [];
        var cacheKey = $"{query.ToLowerInvariant()}|{Math.Clamp(limit, 1, 30)}";
        if (!forceRefresh &&
            _searchCache.TryGetValue(cacheKey, out var cached) &&
            DateTimeOffset.UtcNow - cached.CachedAt < CacheDuration)
            return cached.Value;

        await _searchGate.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh &&
                _searchCache.TryGetValue(cacheKey, out cached) &&
                DateTimeOffset.UtcNow - cached.CachedAt < CacheDuration)
                return cached.Value;

            var wait = TimeSpan.FromMilliseconds(1200) - (DateTimeOffset.UtcNow - _lastSearchAt);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, cancellationToken);

            List<McmodModInfo> results;
            try
            {
                results = await SearchSiteAsync(query, limit, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogService.Warn($"MC百科搜索不可用，改用站点索引：{ex.Message}");
                results = [];
            }

            if (results.Count == 0)
                results = await SearchIndexAsync(query, limit, cancellationToken);

            _lastSearchAt = DateTimeOffset.UtcNow;
            if (results.Count > 0)
                _searchCache[cacheKey] =
                    new CacheEntry<List<McmodModInfo>>(DateTimeOffset.UtcNow, results);
            return results;
        }
        finally
        {
            _searchGate.Release();
        }
    }

    public async Task<IReadOnlyList<McmodModInfo>> SearchWithDetailsAsync(
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var results = (await SearchAsync(query, limit, cancellationToken)).Take(limit).ToList();
        using var gate = new SemaphoreSlim(4);
        var detailed = new McmodModInfo[results.Count];
        await Task.WhenAll(results.Select(async (item, index) =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                detailed[index] = await GetDetailsAsync(item, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogService.Warn($"读取 MC百科条目 {item.ProjectUrl} 失败：{ex.Message}");
                detailed[index] = item;
            }
            finally
            {
                gate.Release();
            }
        }));
        return detailed;
    }

    public async Task<McmodModInfo> GetDetailsAsync(
        McmodModInfo item,
        CancellationToken cancellationToken = default)
    {
        if (_detailCache.TryGetValue(item.ClassId, out var cached) &&
            DateTimeOffset.UtcNow - cached.CachedAt < CacheDuration)
            return cached.Value;

        var html = await _http.GetStringAsync(item.ProjectUrl, cancellationToken);
        var result = new McmodModInfo
        {
            ClassId = item.ClassId,
            ChineseTitle = item.ChineseTitle,
            EnglishTitle = item.EnglishTitle,
            DisplayTitle = item.DisplayTitle,
            Description = item.Description,
            ProjectUrl = item.ProjectUrl,
            CoverUrl = item.CoverUrl,
            CurseForgeSlug = ParseCurseForgeSlug(html),
            ModrinthSlug = ParseModrinthSlug(html)
        };

        var chineseTitle = Regex.Match(html,
            @"<div\s+class=[""']class-title[""'][\s\S]*?<h3>(?<value>[\s\S]*?)</h3>",
            RegexOptions.IgnoreCase);
        var englishTitle = Regex.Match(html,
            @"<div\s+class=[""']class-title[""'][\s\S]*?<h4>(?<value>[\s\S]*?)</h4>",
            RegexOptions.IgnoreCase);
        if (chineseTitle.Success)
            result.ChineseTitle = CleanText(chineseTitle.Groups["value"].Value);
        if (englishTitle.Success)
            result.EnglishTitle = CleanText(englishTitle.Groups["value"].Value);
        result.DisplayTitle = BuildDisplayTitle(result.ChineseTitle, result.EnglishTitle, result.DisplayTitle);

        var cover = CoverRegex.Match(html);
        if (cover.Success)
            result.CoverUrl = MakeHttps(WebUtility.HtmlDecode(cover.Groups["src"].Value));

        _detailCache[item.ClassId] = new CacheEntry<McmodModInfo>(DateTimeOffset.UtcNow, result);
        return result;
    }

    public async Task<McmodModInfo?> FindBestMatchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var candidates = await SearchAsync(query, 8, cancellationToken);
        var match = PickBestMatch(query, candidates);
        return match is null ? null : await GetDetailsAsync(match, cancellationToken);
    }

    public async Task EnrichModrinthHitsAsync(
        IReadOnlyList<ModSearchHit> hits,
        string query,
        CancellationToken cancellationToken = default)
    {
        var candidates = new List<McmodModInfo>();
        if (!string.IsNullOrWhiteSpace(query))
            candidates.AddRange(await SearchAsync(query, Math.Max(20, hits.Count), cancellationToken));

        foreach (var hit in hits)
        {
            if (string.IsNullOrWhiteSpace(hit.FallbackDescription))
                hit.FallbackDescription = hit.Description;
            var cacheKey = NormalizeTitle(hit.Title);
            if (_descriptionCache.TryGetValue(cacheKey, out var cached) &&
                DateTimeOffset.UtcNow - cached.CachedAt < TimeSpan.FromDays(30) &&
                !string.IsNullOrWhiteSpace(cached.Description))
            {
                if (!string.IsNullOrWhiteSpace(cached.McmodUrl))
                    hit.McmodUrl = cached.McmodUrl;
                ApplyChineseDescription(hit, cached.Description);
                continue;
            }
            var match = PickBestMatch(hit.Title, candidates);
            var description = match?.Description;
            if (string.IsNullOrWhiteSpace(description))
                TryGetKnownDescription(hit.Title, out description);

            if (!string.IsNullOrWhiteSpace(description))
            {
                if (!string.IsNullOrWhiteSpace(match?.ProjectUrl))
                    hit.McmodUrl = match.ProjectUrl;
                hit.Description = Shorten(description, 220);
                hit.HasChineseDescription = ChineseTextRegex.IsMatch(hit.Description);
                hit.NeedsChineseDescription = false;
            }
            else
            {
                hit.Description = "正在匹配 MC百科中文说明…";
                hit.HasChineseDescription = true;
                hit.NeedsChineseDescription = true;
            }
        }
    }

    public async Task EnrichMissingDescriptionsAsync(
        IReadOnlyList<ModSearchHit> hits,
        CancellationToken cancellationToken = default)
    {
        foreach (var hit in hits.Where(item => item.NeedsChineseDescription))
        {
            if (cancellationToken.IsCancellationRequested) return;
            try
            {
                var metadata = await FindDescriptionAsync(hit.Title, cancellationToken);
                if (!string.IsNullOrWhiteSpace(metadata.McmodUrl))
                    hit.McmodUrl = metadata.McmodUrl;
                ApplyChineseDescription(hit, metadata.Description);
                hit.NeedsChineseDescription = false;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                LogService.Warn($"匹配 {hit.Title} 的中文说明失败：{ex.Message}");
                ApplyChineseDescription(hit, null);
                hit.NeedsChineseDescription = false;
            }
        }
    }

    public async Task ResolveHitLinksAsync(
        ModSearchHit hit,
        CancellationToken cancellationToken = default)
    {
        McmodModInfo? match = null;
        if (!string.IsNullOrWhiteSpace(hit.McmodUrl))
        {
            var classMatch = ClassLinkRegex.Match(hit.McmodUrl);
            if (classMatch.Success)
            {
                match = new McmodModInfo
                {
                    ClassId = classMatch.Groups["id"].Value,
                    DisplayTitle = hit.Title,
                    EnglishTitle = hit.Title,
                    ChineseTitle = hit.Title,
                    Description = hit.Description,
                    ProjectUrl = hit.McmodUrl
                };
            }
        }

        if (match is null)
        {
            var candidates = await SearchAsync(
                hit.Title, 8, cancellationToken, forceRefresh: true);
            match = PickBestMatch(hit.Title, candidates);
        }
        if (match is null) return;

        hit.McmodUrl = match.ProjectUrl;
        if (!string.IsNullOrWhiteSpace(match.Description))
        {
            ApplyChineseDescription(hit, match.Description);
            _descriptionCache[NormalizeTitle(hit.Title)] = new CachedDescription
            {
                Description = match.Description,
                McmodUrl = match.ProjectUrl,
                CachedAt = DateTimeOffset.UtcNow
            };
            await SaveDescriptionCacheAsync(cancellationToken);
        }
        var details = await GetDetailsAsync(match, cancellationToken);
        if (!string.IsNullOrWhiteSpace(details.CurseForgeSlug))
            hit.CurseForgeUrl = $"https://www.curseforge.com/minecraft/mc-mods/{details.CurseForgeSlug}";
        if (!string.IsNullOrWhiteSpace(details.ModrinthSlug))
            hit.ModrinthUrl = $"https://modrinth.com/mod/{details.ModrinthSlug}";
    }

    public static bool TryGetKnownDescription(string title, out string description)
    {
        var normalized = NormalizeTitle(title);
        foreach (var entry in KnownDescriptions)
        {
            if (entry.Aliases.Any(alias =>
                {
                    var candidate = NormalizeTitle(alias);
                    return normalized.Contains(candidate, StringComparison.Ordinal) ||
                           candidate.Contains(normalized, StringComparison.Ordinal);
                }))
            {
                description = entry.Description;
                return true;
            }
        }

        description = string.Empty;
        return false;
    }

    public static string ApplyChineseDescription(ModSearchHit hit, string? description)
    {
        if (string.IsNullOrWhiteSpace(description) &&
            !TryGetKnownDescription(hit.Title, out description))
        {
            hit.Description = string.IsNullOrWhiteSpace(hit.FallbackDescription)
                ? "暂无说明。"
                : hit.FallbackDescription;
            hit.HasChineseDescription = ChineseTextRegex.IsMatch(hit.Description);
            hit.NeedsChineseDescription = false;
            return hit.Description;
        }

        hit.Description = Shorten(description, 220);
        hit.HasChineseDescription = ChineseTextRegex.IsMatch(hit.Description);
        hit.NeedsChineseDescription = false;
        return hit.Description;
    }

    internal static IReadOnlyList<McmodModInfo> ParseSearchHtml(string html, int limit)
    {
        var results = new List<McmodModInfo>();
        var parts = html.Split("result-item", StringSplitOptions.None);
        for (var index = 1; index < parts.Length && results.Count < limit; index++)
        {
            var block = parts[index];
            var link = Regex.Match(block,
                @"<a\b[^>]*href=[""'](?<url>https?://www\.mcmod\.cn/class/(?<id>\d+)\.html)[""'][^>]*>(?<title>[\s\S]*?)</a>",
                RegexOptions.IgnoreCase);
            if (!link.Success) continue;

            var body = Regex.Match(block,
                @"<div\s+class=[""']body[""'][^>]*>(?<body>[\s\S]*?)</div>\s*<div\s+class=[""']foot[""']",
                RegexOptions.IgnoreCase);
            var title = CleanText(link.Groups["title"].Value);
            ParseTitle(title, out var chineseTitle, out var englishTitle);
            var description = body.Success ? CleanSearchBody(body.Groups["body"].Value) : string.Empty;
            results.Add(new McmodModInfo
            {
                ClassId = link.Groups["id"].Value,
                ChineseTitle = chineseTitle,
                EnglishTitle = englishTitle,
                DisplayTitle = title,
                Description = description,
                ProjectUrl = WebUtility.HtmlDecode(link.Groups["url"].Value)
            });
        }

        return results;
    }

    internal static string? ParseCurseForgeSlug(string html)
    {
        foreach (Match anchor in AnchorRegex.Matches(html))
        {
            var tag = anchor.Value;
            if (!tag.Contains("CurseForge", StringComparison.OrdinalIgnoreCase)) continue;
            var hrefMatch = HrefRegex.Match(tag);
            if (!hrefMatch.Success) continue;
            var href = WebUtility.HtmlDecode(hrefMatch.Groups["href"].Value);
            var decoded = DecodeMcmodLink(href);
            var match = CurseForgePathRegex.Match(decoded);
            if (match.Success) return match.Groups["slug"].Value.ToLowerInvariant();
        }

        var fallback = CurseForgePathRegex.Match(html);
        return fallback.Success ? fallback.Groups["slug"].Value.ToLowerInvariant() : null;
    }

    internal static string? ParseModrinthSlug(string html)
    {
        foreach (Match anchor in AnchorRegex.Matches(html))
        {
            var tag = anchor.Value;
            if (!tag.Contains("Modrinth", StringComparison.OrdinalIgnoreCase)) continue;
            var hrefMatch = HrefRegex.Match(tag);
            if (!hrefMatch.Success) continue;
            var decoded = DecodeMcmodLink(WebUtility.HtmlDecode(hrefMatch.Groups["href"].Value));
            var match = ModrinthPathRegex.Match(decoded);
            if (match.Success) return match.Groups["slug"].Value.ToLowerInvariant();
        }

        var fallback = ModrinthPathRegex.Match(html);
        return fallback.Success ? fallback.Groups["slug"].Value.ToLowerInvariant() : null;
    }

    internal static string NormalizeTitle(string value) =>
        new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private async Task<List<McmodModInfo>> SearchSiteAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        var url = $"https://search.mcmod.cn/s?key={Uri.EscapeDataString(query)}&filter=1";
        var html = await _http.GetStringAsync(url, cancellationToken);
        if (html.Contains("搜索太频繁", StringComparison.Ordinal) ||
            html.Contains("检索速度过快", StringComparison.Ordinal))
            return [];
        return ParseSearchHtml(html, limit).ToList();
    }

    private async Task<List<McmodModInfo>> SearchIndexAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        var searchUrl =
            $"https://www.bing.com/search?format=rss&count={Math.Clamp(limit, 1, 20)}&q=" +
            Uri.EscapeDataString($"site:mcmod.cn/class {query} Minecraft");
        var xml = await _http.GetStringAsync(searchUrl, cancellationToken);
        var document = XDocument.Parse(xml);
        var results = new List<McmodModInfo>();
        foreach (var item in document.Descendants("item"))
        {
            var link = item.Element("link")?.Value.Trim();
            var match = link is null ? Match.Empty : ClassLinkRegex.Match(link);
            if (!match.Success) continue;
            var title = CleanText(item.Element("title")?.Value ?? $"MC百科 Mod {match.Groups["id"].Value}");
            var marker = title.IndexOf(" - MC百科", StringComparison.OrdinalIgnoreCase);
            if (marker > 0) title = title[..marker].Trim();
            ParseTitle(title, out var chineseTitle, out var englishTitle);
            results.Add(new McmodModInfo
            {
                ClassId = match.Groups["id"].Value,
                ChineseTitle = chineseTitle,
                EnglishTitle = englishTitle,
                DisplayTitle = title,
                Description = Shorten(CleanSearchBody(item.Element("description")?.Value ?? string.Empty), 220),
                ProjectUrl = $"{SiteRoot}/class/{match.Groups["id"].Value}.html"
            });
            if (results.Count >= limit) break;
        }

        return results;
    }

    private async Task<CachedDescription> FindDescriptionAsync(
        string title,
        CancellationToken cancellationToken)
    {
        var cacheKey = NormalizeTitle(title);
        if (_descriptionCache.TryGetValue(cacheKey, out var cached) &&
            DateTimeOffset.UtcNow - cached.CachedAt < TimeSpan.FromDays(30) &&
            !string.IsNullOrWhiteSpace(cached.Description))
            return cached;

        CachedDescription result;
        if (TryGetKnownDescription(title, out var knownDescription))
        {
            result = new CachedDescription
            {
                Description = knownDescription,
                CachedAt = DateTimeOffset.UtcNow
            };
        }
        else
        {
            var candidates = await SearchAsync(title, 8, cancellationToken);
            var match = PickBestMatch(title, candidates);
            result = new CachedDescription
            {
                Description = match?.Description ?? string.Empty,
                McmodUrl = match?.ProjectUrl,
                CachedAt = DateTimeOffset.UtcNow
            };
        }

        if (!string.IsNullOrWhiteSpace(result.Description))
        {
            _descriptionCache[cacheKey] = result;
            await SaveDescriptionCacheAsync(cancellationToken);
        }
        return result;
    }

    private void LoadDescriptionCache()
    {
        if (!File.Exists(_descriptionCachePath)) return;
        try
        {
            var entries = JsonSerializer.Deserialize<Dictionary<string, CachedDescription>>(
                File.ReadAllText(_descriptionCachePath), HttpService.JsonOptions);
            if (entries is null) return;
            foreach (var pair in entries)
            {
                if (!string.IsNullOrWhiteSpace(pair.Value.Description))
                    _descriptionCache[pair.Key] = pair.Value;
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"读取 Mod 中文说明缓存失败：{ex.Message}");
        }
    }

    private async Task SaveDescriptionCacheAsync(CancellationToken cancellationToken)
    {
        await _descriptionCacheWriteGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_descriptionCachePath)!);
            var temporaryPath = _descriptionCachePath + ".tmp";
            var snapshot = _descriptionCache.ToDictionary(
                pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = false }),
                cancellationToken);
            File.Move(temporaryPath, _descriptionCachePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warn($"保存 Mod 中文说明缓存失败：{ex.Message}");
        }
        finally
        {
            _descriptionCacheWriteGate.Release();
        }
    }

    private static void ParseTitle(string title, out string chineseTitle, out string englishTitle)
    {
        title = Regex.Replace(title, @"^\[[^\]]+\]\s*", string.Empty).Trim();
        var match = Regex.Match(title, @"^(?<cn>.+?)\s*\((?<en>[^()]+)\)\s*$");
        if (match.Success)
        {
            chineseTitle = match.Groups["cn"].Value.Trim();
            englishTitle = match.Groups["en"].Value.Trim();
        }
        else
        {
            chineseTitle = title;
            englishTitle = title;
        }
    }

    private static string BuildDisplayTitle(string chineseTitle, string englishTitle, string fallback)
    {
        if (string.IsNullOrWhiteSpace(chineseTitle)) return fallback;
        if (string.IsNullOrWhiteSpace(englishTitle) ||
            string.Equals(chineseTitle, englishTitle, StringComparison.OrdinalIgnoreCase))
            return chineseTitle;
        return $"{chineseTitle} ({englishTitle})";
    }

    private static string CleanSearchBody(string value)
    {
        var text = CleanText(value);
        text = MarkupTokenRegex.Replace(text, " ");
        text = RepeatedWhitespaceRegex.Replace(text, " ").Trim();
        if (text.StartsWith("概述", StringComparison.Ordinal))
            text = text[2..].TrimStart(' ', '：', ':');
        return Shorten(text, 260);
    }

    private static string CleanText(string value)
    {
        var text = TagRegex.Replace(value, " ");
        text = WebUtility.HtmlDecode(text);
        text = text.Replace('\u00a0', ' ');
        return RepeatedWhitespaceRegex.Replace(text, " ").Trim();
    }

    private static string DecodeMcmodLink(string href)
    {
        if (href.StartsWith("//", StringComparison.Ordinal)) href = "https:" + href;
        var match = Regex.Match(href, @"/target/(?<data>[A-Za-z0-9+/_=-]+)", RegexOptions.IgnoreCase);
        if (!match.Success) return href;
        try
        {
            var encoded = match.Groups["data"].Value.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            return href;
        }
    }

    private static string MakeHttps(string value)
    {
        if (value.StartsWith("//", StringComparison.Ordinal)) return "https:" + value;
        if (value.StartsWith("/", StringComparison.Ordinal)) return SiteRoot + value;
        return value;
    }

    private static int TitleMatchScore(string title, McmodModInfo item)
    {
        var normalized = NormalizeTitle(title);
        foreach (var candidate in new[] { item.EnglishTitle, item.ChineseTitle, item.DisplayTitle })
        {
            var other = NormalizeTitle(candidate);
            if (other.Length == 0) continue;
            if (normalized == other) return 100;
            if (normalized.Contains(other, StringComparison.Ordinal) ||
                other.Contains(normalized, StringComparison.Ordinal)) return 60;
        }
        return 0;
    }

    private static McmodModInfo? PickBestMatch(
        string title,
        IReadOnlyList<McmodModInfo> candidates)
    {
        var best = candidates
            .OrderByDescending(item => TitleMatchScore(title, item))
            .FirstOrDefault(item => TitleMatchScore(title, item) > 0);
        return best ?? (candidates.Count == 1 ? candidates[0] : null);
    }

    private static string Shorten(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum].TrimEnd() + "…";

    private sealed record CacheEntry<T>(DateTimeOffset CachedAt, T Value);

    private sealed class CachedDescription
    {
        public string Description { get; set; } = string.Empty;
        public string? McmodUrl { get; set; }
        public DateTimeOffset CachedAt { get; set; }
    }

    private static readonly (string[] Aliases, string Description)[] KnownDescriptions =
    [
        (["Just Enough Items", "JEI物品管理器"],
            "JEI 是一个用于查看物品及配方的模组，从零开始构建，注重稳定性与性能。"),
        (["Create", "机械动力"],
            "机械动力以旋转动力、机械结构和自动化建造为核心，提供传动、加工与物流设备。"),
        (["Sodium", "钠"],
            "钠是一款渲染优化模组，通过改进 Minecraft 的渲染逻辑来提升帧率并减少卡顿。"),
        (["Iris Shaders", "Iris光影"],
            "Iris 为 Minecraft 提供高性能光影加载支持，并兼容大量 OptiFine 光影包。"),
        (["Lithium", "锂"],
            "锂从多个游戏系统优化逻辑性能，在不改变原版行为的前提下减少卡顿。"),
        (["Indium", "铟"],
            "铟为使用 Fabric Rendering API 的模组提供与钠的渲染兼容支持。"),
        (["Mod Menu", "模组菜单"],
            "模组菜单为 Fabric 游戏提供 Mod 列表、配置入口和依赖信息。"),
        (["Yet Another Config Lib", "YACL"],
            "YACL 提供配置界面和配置保存接口，是许多 Fabric 模组共用的配置库。"),
        (["Architectury API"],
            "Architectury API 为跨 Fabric、Forge 和 NeoForge 开发的模组提供统一接口。"),
        (["Roughly Enough Items", "REI"],
            "REI 是物品与配方查看模组，支持搜索、配方浏览和多种模组扩展。"),
        (["EMI"],
            "EMI 是物品与配方查看模组，提供快速搜索、合成树和配方管理功能。"),
        (["AppleSkin", "苹果皮"],
            "苹果皮在 HUD 中显示饱和度、饥饿值恢复和食物效果等隐藏信息。"),
        (["Fabric API"],
            "Fabric API 是 Fabric 模组生态的基础运行库，提供大多数 Fabric 模组共用的接口。"),
        (["Cloth Config API", "Cloth Config"],
            "Cloth Config API 为模组提供统一的配置界面构建接口。"),
        (["GeckoLib"],
            "GeckoLib 是用于实体、方块和物品动画的模组开发库，许多动画模组依赖它。"),
        (["Curios API", "Curios"],
            "Curios API 提供可扩展的饰品栏系统，允许模组添加戒指、项链和护符等槽位。"),
        (["Applied Energistics 2", "应用能源2"],
            "应用能源2围绕数字化存储、自动合成和网络物流构建大型科技体系。"),
        (["Mekanism", "通用机械"],
            "通用机械提供矿物处理、能源生产、机器自动化和高阶装备等科技内容。"),
        (["Botania", "植物魔法"],
            "植物魔法以自然魔法为主题，通过花朵、魔力和符文构建科技树。"),
        (["JourneyMap", "旅行地图"],
            "旅行地图会实时记录探索区域，并支持在游戏内查看地图和路径点。"),
        (["Patchouli", "帕秋莉手册"],
            "帕秋莉手册提供数据驱动的游戏内指南书，许多模组用它展示说明与教程。"),
        (["Iron Chests", "铁箱子"],
            "铁箱子扩展了原版储物箱体系，增加不同材质和更大容量的箱子。"),
        (["Waystones", "传送石碑"],
            "传送石碑允许玩家在已激活的石碑之间传送，并可使用卷轴快速返回。"),
        (["Carry On", "搬运"],
            "搬运允许玩家抬起箱子、熔炉和部分生物，并在移动后重新放下。"),
        (["Mouse Tweaks", "鼠标手势"],
            "鼠标手势增强物品栏操作，支持批量移动、快速整理和拖拽。"),
        (["Terralith"],
            "Terralith 大幅扩展主世界地形，加入大量新生物群系、洞穴和地貌。"),
        (["Cobblemon", "宝可梦"],
            "Cobblemon 将宝可梦收集、对战和养成内容带入 Minecraft。")
    ];
}
