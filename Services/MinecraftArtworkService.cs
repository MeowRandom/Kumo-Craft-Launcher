using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kumo.Services;

public sealed class MinecraftArtworkService
{
    private const string CatalogUrl = "https://launchercontent.mojang.com/v2/javaPatchNotes.json";
    private const string WikiApiUrl = "https://minecraft.wiki/api.php";
    private static readonly Uri ContentRoot = new("https://launchercontent.mojang.com/");
    private static readonly TimeSpan CatalogLifetime = TimeSpan.FromHours(12);
    private readonly HttpService _http;
    private readonly string _cacheDirectory;
    private readonly string _catalogPath;
    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _wideArtworkCache =
        new(StringComparer.OrdinalIgnoreCase);

    public MinecraftArtworkService(HttpService http, string dataDirectory)
    {
        _http = http;
        _cacheDirectory = Path.Combine(dataDirectory, "cache", "version-artwork");
        _catalogPath = Path.Combine(_cacheDirectory, "patch-notes.json");
    }

    public async Task<string?> GetBackgroundPathAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion)) return null;
        var catalog = await LoadCatalogAsync(cancellationToken);
        var entry = SelectEntry(catalog, minecraftVersion);
        var image = entry is null
            ? null
            : await FindWideArtworkAsync(minecraftVersion, entry, cancellationToken);
        image ??= await FindWideArtworkByVersionAsync(minecraftVersion, cancellationToken);
        image ??= entry?.Obj("image")?.Str("url");
        if (string.IsNullOrWhiteSpace(image)) return null;

        var imageUri = Uri.TryCreate(image, UriKind.Absolute, out var absolute)
            ? absolute
            : new Uri(ContentRoot, image);
        Directory.CreateDirectory(_cacheDirectory);
        var safeVersion = new string(minecraftVersion
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .Take(48)
            .ToArray());
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(imageUri.AbsoluteUri)))[..10]
            .ToLowerInvariant();
        var extension = Path.GetExtension(imageUri.LocalPath);
        if (extension is not (".jpg" or ".jpeg" or ".png" or ".webp"))
            extension = ".jpg";
        var destination = Path.Combine(
            _cacheDirectory, $"{safeVersion}-{hash}{extension}");
        if (!File.Exists(destination) || new FileInfo(destination).Length == 0)
        {
            await _http.DownloadFileAsync(
                imageUri.AbsoluteUri,
                destination,
                null,
                null,
                null,
                cancellationToken);
        }
        return File.Exists(destination) && new FileInfo(destination).Length > 0
            ? destination
            : null;
    }

    internal static string? SelectImageUrl(JsonObject? catalog, string minecraftVersion)
        => SelectEntry(catalog, minecraftVersion)?.Obj("image")?.Str("url");

    private static JsonObject? SelectEntry(JsonObject? catalog, string minecraftVersion)
    {
        foreach (var entry in catalog?.Arr("entries")?.OfType<JsonObject>() ?? [])
        {
            if (!string.Equals(
                    entry.Str("version"),
                    minecraftVersion,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            return entry;
        }
        return null;
    }

    private async Task<string?> FindWideArtworkAsync(
        string minecraftVersion,
        JsonObject entry,
        CancellationToken cancellationToken)
    {
        if (_wideArtworkCache.TryGetValue(minecraftVersion, out var cached))
            return cached.Length == 0 ? null : cached;

        try
        {
            var imageTitle = entry.Obj("image")?.Str("title");
            var updateName = await FindUpdateNameAsync(minecraftVersion, cancellationToken);
            var queries = new[]
                {
                    updateName,
                    CleanImageTitle(imageTitle)
                }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var query in queries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var image = await SearchWikiArtworkAsync(query, cancellationToken);
                if (string.IsNullOrWhiteSpace(image)) continue;
                _wideArtworkCache[minecraftVersion] = image;
                return image;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Warn($"读取 Minecraft Wiki 版本宣传图失败：{ex.Message}");
        }

        _wideArtworkCache[minecraftVersion] = string.Empty;
        return null;
    }

    private async Task<string?> FindWideArtworkByVersionAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        if (_wideArtworkCache.TryGetValue(minecraftVersion, out var cached))
            return cached.Length == 0 ? null : cached;

        try
        {
            var updateName = await FindUpdateNameAsync(minecraftVersion, cancellationToken);
            var image = string.IsNullOrWhiteSpace(updateName)
                ? null
                : await SearchWikiArtworkAsync(updateName, cancellationToken);
            image ??= await FindVersionPageImageAsync(minecraftVersion, cancellationToken);
            _wideArtworkCache[minecraftVersion] = image ?? string.Empty;
            return image;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Warn($"读取 Minecraft {minecraftVersion} 更新宣传图失败：{ex.Message}");
            _wideArtworkCache[minecraftVersion] = string.Empty;
            return null;
        }
    }

    private async Task<string?> FindUpdateNameAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var updateName = await ReadPageUpdateNameAsync(
            $"Java Edition {minecraftVersion}", cancellationToken);
        if (!string.IsNullOrWhiteSpace(updateName)) return updateName;

        var family = Regex.Match(minecraftVersion, @"^(\d+\.\d+)\.\d+$");
        return family.Success
            ? await ReadPageUpdateNameAsync(
                $"Java Edition {family.Groups[1].Value}", cancellationToken)
            : null;
    }

    private async Task<string?> ReadPageUpdateNameAsync(
        string pageTitle,
        CancellationToken cancellationToken)
    {
        var page = Uri.EscapeDataString(pageTitle);
        var url = $"{WikiApiUrl}?action=parse&page={page}&prop=wikitext&format=json";
        var root = JsonNode.Parse(await _http.GetStringAsync(url, cancellationToken)) as JsonObject;
        var text = root?.Obj("parse")?.Obj("wikitext")?.Str("*");
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Regex.Match(
            text,
            @"\|\s*name\s*=\s*\*?\s*\[\[([^\]|#]+)",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private async Task<string?> FindVersionPageImageAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var page = Uri.EscapeDataString($"Java Edition {minecraftVersion}");
        var url = $"{WikiApiUrl}?action=parse&page={page}&prop=wikitext&format=json";
        var root = JsonNode.Parse(await _http.GetStringAsync(url, cancellationToken)) as JsonObject;
        var text = root?.Obj("parse")?.Obj("wikitext")?.Str("*");
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = Regex.Match(
            text,
            @"\|\s*image\s*=\s*([^\r\n]+)",
            RegexOptions.IgnoreCase).Groups[1].Value.Trim();
        var fileName = Regex.Match(
            value,
            @"(?:File:)?([^|\]\[<>\r\n]+\.(?:png|jpg|jpeg))",
            RegexOptions.IgnoreCase);
        if (!fileName.Success) return null;

        var title = Uri.EscapeDataString("File:" + fileName.Groups[1].Value.Trim());
        var infoUrl =
            $"{WikiApiUrl}?action=query&titles={title}" +
            "&prop=imageinfo&iiprop=url%7Csize&format=json";
        var infoRoot = JsonNode.Parse(
            await _http.GetStringAsync(infoUrl, cancellationToken)) as JsonObject;
        foreach (var pageNode in infoRoot?.Obj("query")?.Obj("pages")?
                     .OfType<KeyValuePair<string, JsonNode?>>()
                     .Select(pair => pair.Value as JsonObject)
                     .Where(pageNode => pageNode is not null) ?? [])
        {
            var info = pageNode!.Arr("imageinfo")?.FirstOrDefault() as JsonObject;
            var imageUrl = info?.Str("url");
            if (!string.IsNullOrWhiteSpace(imageUrl)) return imageUrl;
        }
        return null;
    }

    private async Task<string?> SearchWikiArtworkAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var searchUrl =
            $"{WikiApiUrl}?action=query&list=search&srsearch={Uri.EscapeDataString(query)}" +
            "&srnamespace=6&srlimit=30&format=json";
        var searchRoot = JsonNode.Parse(
            await _http.GetStringAsync(searchUrl, cancellationToken)) as JsonObject;
        var titles = searchRoot?.Obj("query")?.Arr("search")?
            .OfType<JsonObject>()
            .Select(item => item.Str("title"))
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(title => title!)
            .Where(title =>
            {
                var extension = Path.GetExtension(title);
                return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                       extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                       extension.Equals(".png", StringComparison.OrdinalIgnoreCase);
            })
            .Take(20)
            .ToList();
        if (titles is not { Count: > 0 }) return null;

        var titleList = Uri.EscapeDataString(string.Join('|', titles));
        var infoUrl =
            $"{WikiApiUrl}?action=query&titles={titleList}" +
            "&prop=imageinfo&iiprop=url%7Csize&format=json";
        var infoRoot = JsonNode.Parse(
            await _http.GetStringAsync(infoUrl, cancellationToken)) as JsonObject;
        var candidates = new List<(string Url, double Score)>();
        var queryWords = Regex.Matches(query, @"[\p{L}\p{N}]+")
            .Select(match => match.Value.ToLowerInvariant())
            .Where(word => word.Length >= 3)
            .Distinct()
            .ToList();
        foreach (var page in infoRoot?.Obj("query")?.Obj("pages")?.OfType<KeyValuePair<string, JsonNode?>>()
                     .Select(pair => pair.Value as JsonObject)
                     .Where(page => page is not null) ?? [])
        {
            var info = page!.Arr("imageinfo")?.FirstOrDefault() as JsonObject;
            if (info is null) continue;
            var width = info["width"]?.GetValue<int>() ?? 0;
            var height = info["height"]?.GetValue<int>() ?? 0;
            var imageUrl = info.Str("url");
            if (width < 800 || height <= 0 || string.IsNullOrWhiteSpace(imageUrl)) continue;
            var ratio = width / (double)height;
            if (ratio is < 1.3 or > 2.4) continue;

            var title = page.Str("title")?.ToLowerInvariant() ?? string.Empty;
            var matchedWords = queryWords.Count(word =>
                title.Contains(word, StringComparison.Ordinal));
            var requiredMatches = Math.Min(2, queryWords.Count);
            if (requiredMatches > 0 && matchedWords < requiredMatches) continue;

            var score = 100d - Math.Abs(ratio - 16d / 9d) * 40d +
                        Math.Min(60, width / 60d) + matchedWords * 70d;
            if (title.Contains("key art", StringComparison.Ordinal)) score += 180;
            else if (title.Contains("banner", StringComparison.Ordinal)) score += 160;
            else if (title.Contains("artwork", StringComparison.Ordinal)) score += 100;
            else if (title.Contains("art", StringComparison.Ordinal)) score += 70;
            if (title.Contains("concept", StringComparison.Ordinal)) score -= 60;
            if (title.Contains("logo", StringComparison.Ordinal)) score -= 250;
            if (title.Contains("storyboard", StringComparison.Ordinal) ||
                title.Contains("thumbnail", StringComparison.Ordinal) ||
                title.Contains("soundtrack", StringComparison.Ordinal) ||
                title.Contains("panorama", StringComparison.Ordinal) ||
                title.Contains("divider", StringComparison.Ordinal))
                score -= 150;
            candidates.Add((imageUrl, score));
        }
        return candidates.OrderByDescending(candidate => candidate.Score)
            .Select(candidate => candidate.Url)
            .FirstOrDefault();
    }

    private static string? CleanImageTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var cleaned = Path.GetFileNameWithoutExtension(title);
        cleaned = Regex.Replace(cleaned, @"\b\d{2,4}x\d{2,4}\b", " ", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private async Task<JsonObject?> LoadCatalogAsync(CancellationToken cancellationToken)
    {
        await _catalogGate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_catalogPath) &&
                DateTime.UtcNow - File.GetLastWriteTimeUtc(_catalogPath) < CatalogLifetime)
                return await ReadCatalogAsync(cancellationToken);

            Directory.CreateDirectory(_cacheDirectory);
            try
            {
                var json = await _http.GetStringAsync(CatalogUrl, cancellationToken);
                var catalog = JsonNode.Parse(json) as JsonObject
                              ?? throw new InvalidDataException("Mojang 宣传图清单格式无效。");
                var temporary = _catalogPath + ".tmp";
                await File.WriteAllTextAsync(temporary, json, cancellationToken);
                File.Move(temporary, _catalogPath, true);
                return catalog;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogService.Warn($"读取 Mojang 版本宣传图失败：{ex.Message}");
                return await ReadCatalogAsync(cancellationToken);
            }
        }
        finally
        {
            _catalogGate.Release();
        }
    }

    private async Task<JsonObject?> ReadCatalogAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_catalogPath)) return null;
        try
        {
            return JsonNode.Parse(
                await File.ReadAllTextAsync(_catalogPath, cancellationToken)) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       System.Text.Json.JsonException)
        {
            LogService.Warn($"读取本地版本宣传图清单失败：{ex.Message}");
            return null;
        }
    }
}
