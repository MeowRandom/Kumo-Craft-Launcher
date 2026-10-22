using System.IO;
using System.Text.Json;
using Kumo.Models;

namespace Kumo.Services;

public sealed class MinecraftService
{
    private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private readonly HttpService _http;
    private readonly string _dataDirectory;
    private VersionManifest? _cachedManifest;
    private DateTimeOffset _manifestFetchedAt;

    public MinecraftService(HttpService http, string dataDirectory)
    {
        _http = http;
        _dataDirectory = dataDirectory;
    }

    public async Task<VersionManifest> GetManifestAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!force && _cachedManifest is not null &&
            DateTimeOffset.UtcNow - _manifestFetchedAt < TimeSpan.FromMinutes(15))
            return _cachedManifest;

        var cachePath = Path.Combine(_dataDirectory, "meta", "version_manifest_v2.json");
        try
        {
            var json = await _http.GetStringAsync(ManifestUrl, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            await File.WriteAllTextAsync(cachePath, json, cancellationToken);
            _cachedManifest = ParseManifest(json);
            _manifestFetchedAt = DateTimeOffset.UtcNow;
            return _cachedManifest;
        }
        catch (Exception ex) when (File.Exists(cachePath))
        {
            LogService.Warn($"版本清单联网失败，使用本地缓存：{ex.Message}");
            var json = await File.ReadAllTextAsync(cachePath, cancellationToken);
            _cachedManifest = ParseManifest(json);
            _manifestFetchedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
            return _cachedManifest;
        }
    }

    public string GetVersionJsonPath(string id) =>
        Path.Combine(_dataDirectory, "versions", id, id + ".json");

    public string GetVersionJarPath(string id) =>
        Path.Combine(_dataDirectory, "versions", id, id + ".jar");

    public bool IsVersionInstalled(string id)
    {
        var json = GetVersionJsonPath(id);
        if (!File.Exists(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(json));
            var root = doc.RootElement;
            var inherits = root.TryGetProperty("inheritsFrom", out var parent) ? parent.GetString() : null;
            if (!string.IsNullOrWhiteSpace(inherits))
                return IsVersionInstalled(inherits);
            return File.Exists(GetVersionJarPath(id));
        }
        catch
        {
            return false;
        }
    }

    private static VersionManifest ParseManifest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var latest = root.GetProperty("latest");
        var versions = new List<ManifestVersion>();
        foreach (var item in root.GetProperty("versions").EnumerateArray())
        {
            versions.Add(new ManifestVersion(
                item.GetProperty("id").GetString() ?? string.Empty,
                item.GetProperty("type").GetString() ?? "unknown",
                item.GetProperty("url").GetString() ?? string.Empty,
                DateTimeOffset.Parse(item.GetProperty("releaseTime").GetString() ?? DateTimeOffset.MinValue.ToString("O")),
                item.TryGetProperty("sha1", out var sha) ? sha.GetString() : null));
        }

        return new VersionManifest(
            new LatestVersions(latest.GetProperty("release").GetString() ?? "",
                latest.GetProperty("snapshot").GetString() ?? ""),
            versions,
            json);
    }
}
