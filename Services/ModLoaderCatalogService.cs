using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using Kumo.Models;

namespace Kumo.Services;

public sealed class ModLoaderCatalogService
{
    private const string MirrorRoot = DownloadRouteService.MirrorRoot;
    private readonly HttpService _http;
    private readonly ConcurrentDictionary<string, (DateTimeOffset CachedAt, IReadOnlyList<LoaderVersionOption> Items)> _cache = new();

    public ModLoaderCatalogService(HttpService http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<LoaderVersionOption>> GetVersionsAsync(
        ModLoader loader,
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        if (loader == ModLoader.Vanilla)
            throw new InvalidOperationException("原版不是 Mod 加载器。");

        var cacheKey = $"{loader}|{minecraftVersion}";
        if (_cache.TryGetValue(cacheKey, out var cached) &&
            DateTimeOffset.UtcNow - cached.CachedAt < TimeSpan.FromMinutes(10))
            return cached.Items;

        IReadOnlyList<LoaderVersionOption> items;
        try
        {
            items = loader switch
            {
                ModLoader.Fabric => await GetFabricAsync(minecraftVersion, cancellationToken),
                ModLoader.Quilt => await GetQuiltAsync(minecraftVersion, cancellationToken),
                ModLoader.Forge => await GetForgeAsync(minecraftVersion, cancellationToken),
                ModLoader.NeoForge => await GetNeoForgeAsync(minecraftVersion, cancellationToken),
                ModLoader.OptiFine => await GetOptiFineAsync(minecraftVersion, cancellationToken),
                _ => []
            };
        }
        catch (HttpRequestException ex) when (IsNoLoaderVersionsStatus(ex.StatusCode))
        {
            items = [];
        }
        _cache[cacheKey] = (DateTimeOffset.UtcNow, items);
        return items;
    }

    internal static bool IsNoLoaderVersionsStatus(HttpStatusCode? statusCode) =>
        statusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound;

    private async Task<IReadOnlyList<LoaderVersionOption>> GetFabricAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var entries = await _http.GetJsonAsync<List<FabricEntry>>(
            $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(minecraftVersion)}",
            cancellationToken);
        return entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Loader.Version))
            .OrderByDescending(entry => entry.Loader.Stable)
            .ThenByDescending(entry => entry.Loader.Version, VersionTextComparer.Instance)
            .Select(entry => new LoaderVersionOption(
                ModLoader.Fabric,
                entry.Loader.Version,
                entry.Loader.Version))
            .ToList();
    }

    private async Task<IReadOnlyList<LoaderVersionOption>> GetQuiltAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var entries = await _http.GetJsonAsync<List<QuiltEntry>>(
            $"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(minecraftVersion)}",
            cancellationToken);
        return entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Loader.Version))
            .OrderByDescending(entry => entry.Loader.Stable)
            .ThenByDescending(entry => entry.Loader.Version, VersionTextComparer.Instance)
            .Select(entry => new LoaderVersionOption(
                ModLoader.Quilt,
                entry.Loader.Version,
                entry.Loader.Version))
            .ToList();
    }

    private async Task<IReadOnlyList<LoaderVersionOption>> GetForgeAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var entries = await _http.GetJsonAsync<List<ForgeEntry>>(
            $"{MirrorRoot}/forge/minecraft/{Uri.EscapeDataString(minecraftVersion)}",
            cancellationToken);
        return entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Version))
            .OrderByDescending(entry => entry.Build)
            .Select(entry =>
            {
                var relative =
                    $"net/minecraftforge/forge/{minecraftVersion}-{entry.Version}/forge-{minecraftVersion}-{entry.Version}-installer.jar";
                var sha1 = entry.Files?
                    .FirstOrDefault(file => file.Category.Equals("installer", StringComparison.OrdinalIgnoreCase))
                    ?.Hash;
                return new LoaderVersionOption(
                    ModLoader.Forge,
                    entry.Version,
                    entry.Version,
                    $"https://maven.minecraftforge.net/{relative}",
                    $"{MirrorRoot}/maven/{relative}",
                    sha1);
            })
            .ToList();
    }

    private async Task<IReadOnlyList<LoaderVersionOption>> GetNeoForgeAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var entries = await _http.GetJsonAsync<List<NeoForgeEntry>>(
            $"{MirrorRoot}/neoforge/list/{Uri.EscapeDataString(minecraftVersion)}",
            cancellationToken);
        return entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Version))
            .OrderByDescending(entry => entry.Version, VersionTextComparer.Instance)
            .Select(entry =>
            {
                var installerPath = string.IsNullOrWhiteSpace(entry.InstallerPath)
                    ? $"/maven/net/neoforged/neoforge/{entry.Version}/neoforge-{entry.Version}-installer.jar"
                    : entry.InstallerPath;
                var officialPath = installerPath.StartsWith("/maven/", StringComparison.OrdinalIgnoreCase)
                    ? installerPath["/maven".Length..]
                    : installerPath;
                return new LoaderVersionOption(
                    ModLoader.NeoForge,
                    entry.Version,
                    entry.Version,
                    "https://maven.neoforged.net/releases" + officialPath,
                    MirrorRoot + installerPath);
            })
            .ToList();
    }

    private async Task<IReadOnlyList<LoaderVersionOption>> GetOptiFineAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var entries = await _http.GetJsonAsync<List<OptiFineEntry>>(
            $"{MirrorRoot}/optifine/{Uri.EscapeDataString(minecraftVersion)}",
            cancellationToken);
        return entries
            .Where(entry =>
                !string.IsNullOrWhiteSpace(entry.Type) &&
                !string.IsNullOrWhiteSpace(entry.Patch))
            .OrderBy(entry => entry.Filename.StartsWith(
                "preview_", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenByDescending(entry => $"{entry.Type}_{entry.Patch}", VersionTextComparer.Instance)
            .Select(entry =>
            {
                var isPreview = entry.Filename.StartsWith(
                    "preview_", StringComparison.OrdinalIgnoreCase);
                var displayName = $"{entry.Type.Replace('_', ' ')} {entry.Patch}";
                return new LoaderVersionOption(
                    ModLoader.OptiFine,
                    $"{entry.Type}_{entry.Patch}",
                    isPreview ? displayName + " · 预览" : displayName,
                    $"{MirrorRoot}/optifine/{Uri.EscapeDataString(minecraftVersion)}/" +
                    $"{Uri.EscapeDataString(entry.Type)}/{Uri.EscapeDataString(entry.Patch)}",
                    IsPreview: isPreview);
            })
            .ToList();
    }

    private sealed class FabricEntry
    {
        public FabricLoaderInfo Loader { get; set; } = new();
    }

    private sealed class FabricLoaderInfo
    {
        public string Version { get; set; } = "";
        public bool Stable { get; set; }
    }

    private sealed class QuiltEntry
    {
        public QuiltLoaderInfo Loader { get; set; } = new();
    }

    private sealed class QuiltLoaderInfo
    {
        public string Version { get; set; } = "";
        public bool Stable { get; set; }
    }

    private sealed class ForgeEntry
    {
        public string Version { get; set; } = "";
        public long Build { get; set; }
        public List<ForgeFile>? Files { get; set; }
    }

    private sealed class ForgeFile
    {
        public string Category { get; set; } = "";
        public string Hash { get; set; } = "";
    }

    private sealed class NeoForgeEntry
    {
        public string Version { get; set; } = "";
        public string? InstallerPath { get; set; }
    }

    private sealed class OptiFineEntry
    {
        public string Type { get; set; } = string.Empty;
        public string Patch { get; set; } = string.Empty;
        public string Filename { get; set; } = string.Empty;
    }

    private sealed class VersionTextComparer : IComparer<string>
    {
        public static VersionTextComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            var left = Parse(x);
            var right = Parse(y);
            for (var index = 0; index < Math.Max(left.Count, right.Count); index++)
            {
                var leftPart = index < left.Count ? left[index] : 0;
                var rightPart = index < right.Count ? right[index] : 0;
                var comparison = leftPart.CompareTo(rightPart);
                if (comparison != 0) return comparison;
            }
            return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        }

        private static List<long> Parse(string? value) =>
            (value ?? "").Split(['.', '-', '+'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => long.TryParse(part, out var number) ? number : 0)
            .ToList();
    }
}
