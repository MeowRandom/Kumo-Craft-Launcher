using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Kumo.Models;

namespace Kumo.Services;

public sealed class VersionProfileService
{
    private static readonly HashSet<string> ReservedWindowsNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

    private readonly string _dataDirectory;
    private readonly MinecraftService _minecraft;

    public VersionProfileService(string dataDirectory, MinecraftService minecraft)
    {
        _dataDirectory = dataDirectory;
        _minecraft = minecraft;
    }

    public IReadOnlyList<InstalledVersionInfo> GetInstalledVersions()
    {
        var root = Path.Combine(_dataDirectory, "versions");
        if (!Directory.Exists(root)) return [];

        var parsed = new List<InstalledVersionEntry>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var id = Path.GetFileName(directory);
            var path = Path.Combine(directory, id + ".json");
            if (!File.Exists(path)) continue;
            try
            {
                var json = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                if (json is null) continue;
                var baseVersion = VersionMetadata.GetString(json, VersionMetadata.BaseVersion)
                                  ?? json.Str("inheritsFrom")
                                  ?? InferBaseVersion(id, json)
                                  ?? id;
                var (loader, detectedLoaderVersion) = DetectLoader(id, json);
                var loaderVersion = VersionMetadata.GetString(json, VersionMetadata.LoaderVersion)
                                    ?? detectedLoaderVersion
                                    ?? InferLoaderVersion(id, baseVersion, loader, json);
                parsed.Add(new InstalledVersionEntry(id, baseVersion, loader,
                    loaderVersion,
                    VersionMetadata.GetObject(json, VersionMetadata.Modpack)?.Str("name"),
                    VersionMetadata.GetString(json, VersionMetadata.DisplayName),
                    path));
            }
            catch (Exception ex)
            {
                LogService.Warn($"无法读取版本 {id}：{ex.Message}");
            }
        }

        var modpackDependencies = parsed
            .Where(entry => !string.IsNullOrWhiteSpace(entry.ModpackName))
            .SelectMany(entry => new[]
            {
                DependencyKey(entry.BaseVersion, entry.Loader),
                DependencyKey(entry.BaseVersion, ModLoader.Vanilla)
            })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = parsed
            .Where(entry => !string.IsNullOrWhiteSpace(entry.ModpackName) ||
                            !modpackDependencies.Contains(DependencyKey(entry.BaseVersion, entry.Loader)))
            .Select(entry => new InstalledVersionInfo
            {
                Id = entry.Id,
                BaseVersion = entry.BaseVersion,
                Loader = entry.Loader,
                LoaderVersion = entry.LoaderVersion,
                ModpackName = entry.ModpackName,
                DisplayName = entry.DisplayName,
                VanillaIconPath = entry.Loader is ModLoader.Vanilla or ModLoader.OptiFine
                    ? EnsureVanillaIcon()
                    : null,
                Ready = _minecraft.IsVersionInstalled(entry.Id),
                LastWriteTime = File.GetLastWriteTimeUtc(entry.Path)
            })
            .ToList();

        return result
            .OrderByDescending(v => v.Loader != ModLoader.Vanilla)
            .ThenByDescending(v => v.LastWriteTime)
            .ToList();
    }

    public bool HasInheritedDependents(string versionId)
    {
        var root = Path.Combine(_dataDirectory, "versions");
        if (!Directory.Exists(root)) return false;

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (string.Equals(Path.GetFileName(directory), versionId,
                    StringComparison.OrdinalIgnoreCase))
                continue;

            var id = Path.GetFileName(directory);
            var path = Path.Combine(directory, id + ".json");
            if (!File.Exists(path)) continue;
            try
            {
                var json = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                if (string.Equals(json.Str("inheritsFrom"), versionId,
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                           System.Text.Json.JsonException)
            {
                LogService.Warn($"检查版本依赖 {id} 失败：{ex.Message}");
            }
        }

        return false;
    }

    public IReadOnlySet<string> GetInstanceIds()
    {
        var root = Path.Combine(_dataDirectory, "versions");
        if (!Directory.Exists(root)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public bool InstanceIdExists(string instanceId) =>
        Directory.Exists(Path.Combine(_dataDirectory, "versions", instanceId));

    public static bool TryNormalizeInstanceId(
        string? value,
        out string instanceId,
        out string error)
    {
        instanceId = value?.Trim() ?? string.Empty;
        error = string.Empty;
        if (instanceId.Length == 0) return true;
        if (instanceId.Length > 64)
        {
            error = "版本名称不能超过 64 个字符。";
            return false;
        }
        if (instanceId is "." or "..")
        {
            error = "这个名称不能用于版本目录。";
            return false;
        }
        if (instanceId.EndsWith('.') || instanceId.EndsWith(' '))
        {
            error = "版本名称不能以句点或空格结尾。";
            return false;
        }
        if (instanceId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            instanceId.Any(char.IsControl))
        {
            error = "版本名称不能包含 \\ / : * ? \" < > | 等字符。";
            return false;
        }
        var deviceName = instanceId.Split('.')[0];
        if (ReservedWindowsNames.Contains(deviceName))
        {
            error = "这个名称是 Windows 保留名称，请换一个版本名称。";
            return false;
        }
        return true;
    }

    public async Task<InstalledVersionInfo> MaterializeInstanceAsync(
        InstalledVersionInfo source,
        string instanceId,
        bool copySource,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeInstanceId(instanceId, out var normalized, out var error) ||
            normalized.Length == 0)
            throw new InvalidOperationException(error);
        if (string.Equals(source.Id, normalized, StringComparison.OrdinalIgnoreCase))
        {
            await UpdateInstanceIdentityAsync(
                source.Id, source.Id, source.BaseVersion, normalized, cancellationToken);
            return CopyWithIdentity(source, normalized);
        }

        var versionsRoot = Path.Combine(_dataDirectory, "versions");
        var sourceDirectory = Path.Combine(versionsRoot, source.Id);
        var destinationDirectory = Path.Combine(versionsRoot, normalized);
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"找不到版本 {source.Id} 的安装目录。");
        if (Directory.Exists(destinationDirectory))
            throw new InvalidOperationException($"版本名称“{normalized}”已存在，请换一个名称。");

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (copySource)
                CopyDirectory(sourceDirectory, destinationDirectory, cancellationToken);
            else
                Directory.Move(sourceDirectory, destinationDirectory);

            RenameIdentityFiles(
                destinationDirectory, source.Id, normalized,
                source.BaseVersion, cancellationToken);
            TransferNatives(source.Id, normalized, copySource, cancellationToken);
        }, cancellationToken);

        return CopyWithIdentity(source, normalized);
    }

    private async Task UpdateInstanceIdentityAsync(
        string directoryId,
        string sourceId,
        string baseVersion,
        string displayName,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(_dataDirectory, "versions", directoryId);
        await Task.Run(
            () => RenameIdentityFiles(
                directory, sourceId, directoryId, baseVersion, cancellationToken),
            cancellationToken);
    }

    private static void RenameIdentityFiles(
        string directory,
        string sourceId,
        string destinationId,
        string baseVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceJson = Path.Combine(directory, sourceId + ".json");
        var destinationJson = Path.Combine(directory, destinationId + ".json");
        if (!File.Exists(sourceJson) && !File.Exists(destinationJson))
            throw new InvalidDataException("新版本缺少 JSON 配置，无法创建独立实例。");
        if (!string.Equals(sourceJson, destinationJson, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(sourceJson))
            File.Move(sourceJson, destinationJson, true);

        var sourceJar = Path.Combine(directory, sourceId + ".jar");
        var destinationJar = Path.Combine(directory, destinationId + ".jar");
        if (!string.Equals(sourceJar, destinationJar, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(sourceJar))
            File.Move(sourceJar, destinationJar, true);

        var sourceMappings = Path.Combine(directory, sourceId + "-mappings.txt");
        var destinationMappings = Path.Combine(directory, destinationId + "-mappings.txt");
        if (!string.Equals(sourceMappings, destinationMappings, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(sourceMappings))
            File.Move(sourceMappings, destinationMappings, true);

        var profile = JsonNode.Parse(File.ReadAllText(destinationJson)) as JsonObject
                      ?? throw new InvalidDataException("新版本的 JSON 配置无效。");
        profile["id"] = destinationId;
        VersionMetadata.Set(profile, VersionMetadata.BaseVersion, baseVersion);
        VersionMetadata.Set(profile, VersionMetadata.DisplayName, destinationId);
        VersionMetadata.Set(profile, VersionMetadata.Explicit, true);
        VersionMetadata.Remove(profile, VersionMetadata.Dependency);
        File.WriteAllText(destinationJson, profile.ToJsonString());
    }

    private void TransferNatives(
        string sourceId,
        string destinationId,
        bool copySource,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(_dataDirectory, "natives");
        var source = Path.Combine(root, sourceId);
        if (!Directory.Exists(source)) return;
        var destination = Path.Combine(root, destinationId);
        if (copySource)
            CopyDirectory(source, destination, cancellationToken);
        else if (!Directory.Exists(destination))
            Directory.Move(source, destination);
    }

    private static void CopyDirectory(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(
                     source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(
                Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(
                     source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private static InstalledVersionInfo CopyWithIdentity(
        InstalledVersionInfo source,
        string instanceId) =>
        new()
        {
            Id = instanceId,
            BaseVersion = source.BaseVersion,
            Loader = source.Loader,
            LoaderVersion = source.LoaderVersion,
            Ready = true,
            LastWriteTime = DateTimeOffset.UtcNow,
            ModpackName = source.ModpackName,
            DisplayName = instanceId
        };

    private static string DependencyKey(string baseVersion, ModLoader loader) =>
        $"{baseVersion}|{loader}";

    private string? InferBaseVersion(string id, JsonObject profile)
    {
        foreach (var prefix in new[] { "fabric-loader-", "quilt-loader-" })
        {
            if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var value = id[prefix.Length..];
            var separator = value.LastIndexOf('-');
            return separator > 0 ? value[(separator + 1)..] : null;
        }

        var forgeMarker = id.IndexOf("-forge-", StringComparison.OrdinalIgnoreCase);
        if (forgeMarker > 0) return id[..forgeMarker];
        var optiFineMarker = id.IndexOf("-OptiFine_", StringComparison.OrdinalIgnoreCase);
        if (optiFineMarker > 0) return id[..optiFineMarker];

        var libraryBaseVersion = InferBaseVersionFromLibraries(profile);
        if (!string.IsNullOrWhiteSpace(libraryBaseVersion))
            return libraryBaseVersion;

        var assetId = profile.Obj("assetIndex")?.Str("id");
        var clientSha1 = profile.Obj("downloads")?.Obj("client")?.Str("sha1");
        var cacheRoot = Path.Combine(_dataDirectory, "cache", "version-dependencies");
        if (!Directory.Exists(cacheRoot)) return null;
        foreach (var directory in Directory.EnumerateDirectories(cacheRoot))
        {
            var cachedId = Path.GetFileName(directory);
            var cachedPath = Path.Combine(directory, cachedId + ".json");
            if (!File.Exists(cachedPath)) continue;
            var cached = JsonNode.Parse(File.ReadAllText(cachedPath)) as JsonObject;
            if (cached is null) continue;
            var cachedAssetId = cached.Obj("assetIndex")?.Str("id");
            var cachedClientSha1 = cached.Obj("downloads")?.Obj("client")?.Str("sha1");
            if (string.Equals(assetId, cachedAssetId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(clientSha1, cachedClientSha1, StringComparison.OrdinalIgnoreCase))
                return cachedId;
        }
        return null;
    }

    private static string? InferBaseVersionFromLibraries(JsonObject profile)
    {
        foreach (var library in profile.Arr("libraries")?.OfType<JsonObject>() ?? [])
        {
            var coordinate = library.Str("name");
            if (string.IsNullOrWhiteSpace(coordinate)) continue;
            var parts = coordinate.Split(':');
            if (parts.Length < 3) continue;

            if (parts[0].Equals("net.minecraftforge", StringComparison.OrdinalIgnoreCase) &&
                parts[1] is "forge" or "fmlloader" or "fmlearlydisplay")
            {
                var match = Regex.Match(
                    parts[2],
                    @"^(?<minecraft>\d+\.\d+(?:\.\d+)?)-",
                    RegexOptions.CultureInvariant);
                if (match.Success) return match.Groups["minecraft"].Value;
            }

            if (!parts[0].Equals("net.neoforged", StringComparison.OrdinalIgnoreCase) ||
                !parts[1].Equals("neoforge", StringComparison.OrdinalIgnoreCase))
                continue;
            var neoforge = parts[2].Split('.');
            if (neoforge.Length < 2 ||
                !int.TryParse(neoforge[0], out var major) ||
                !int.TryParse(neoforge[1], out var minor))
                continue;
            if (major >= 21)
                return minor == 0 ? $"1.{major}" : $"1.{major}.{minor}";
            if (major == 20)
                return $"1.20.{minor}";
        }
        return null;
    }

    private sealed record InstalledVersionEntry(
        string Id,
        string BaseVersion,
        ModLoader Loader,
        string? LoaderVersion,
        string? ModpackName,
        string? DisplayName,
        string Path);

    internal static string? InferLoaderVersion(
        string id,
        string baseVersion,
        ModLoader loader,
        JsonObject profile)
    {
        var libraries = profile.Arr("libraries");
        string? LibraryVersion(params string[] prefixes)
        {
            if (libraries is null) return null;
            foreach (var library in libraries.OfType<JsonObject>())
            {
                var name = library.Str("name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                foreach (var prefix in prefixes)
                {
                    if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var remainder = name[prefix.Length..];
                    var separator = remainder.IndexOf(':');
                    return separator > 0 ? remainder[..separator] : remainder;
                }
            }
            return null;
        }

        switch (loader)
        {
            case ModLoader.Fabric:
                return LibraryVersion("net.fabricmc:fabric-loader:");
            case ModLoader.Quilt:
                return LibraryVersion("org.quiltmc:quilt-loader:");
            case ModLoader.Forge:
            {
                var coordinateVersion = LibraryVersion(
                    "net.minecraftforge:forge:",
                    "net.minecraftforge:fmlloader:");
                if (!string.IsNullOrWhiteSpace(coordinateVersion))
                {
                    var prefix = baseVersion + "-";
                    return coordinateVersion.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        ? coordinateVersion[prefix.Length..]
                        : coordinateVersion;
                }
                var marker = id.IndexOf("-forge-", StringComparison.OrdinalIgnoreCase);
                if (marker > 0) return id[(marker + "-forge-".Length)..];
                if (id.StartsWith("forge-", StringComparison.OrdinalIgnoreCase))
                {
                    var combined = id["forge-".Length..];
                    var basePrefix = baseVersion + "-";
                    return combined.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase)
                        ? combined[basePrefix.Length..]
                        : combined;
                }
                return null;
            }
            case ModLoader.NeoForge:
                if (id.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase))
                    return id["neoforge-".Length..];
                return LibraryVersion("net.neoforged:neoforge:");
            case ModLoader.OptiFine:
            {
                var marker = id.IndexOf("-OptiFine_", StringComparison.OrdinalIgnoreCase);
                return marker >= 0 ? id[(marker + "-OptiFine_".Length)..] : null;
            }
            default:
                return null;
        }
    }

    private string? EnsureVanillaIcon()
    {
        var cacheDirectory = Path.Combine(_dataDirectory, "cache", "vanilla-icons-v4");
        var destination = Path.Combine(cacheDirectory, "grass-block-light.png");
        if (File.Exists(destination) && new FileInfo(destination).Length > 0) return destination;

        Directory.CreateDirectory(cacheDirectory);
        foreach (var source in GetOfficialMinecraftIconCandidates())
        {
            if (!File.Exists(source) || new FileInfo(source).Length == 0) continue;
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(source, temporary, true);
                File.Move(temporary, destination, true);
                return destination;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogService.Warn($"无法读取 Minecraft 应用图标：{ex.Message}");
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch
                {
                    // The icon is optional and must not prevent version discovery.
                }
            }
        }
        return null;
    }

    private static IEnumerable<string> GetOfficialMinecraftIconCandidates()
    {
        var overridePath = Environment.GetEnvironmentVariable("KUMO_MINECRAFT_ICON");
        if (!string.IsNullOrWhiteSpace(overridePath)) yield return overridePath;

        var systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (string.IsNullOrWhiteSpace(systemDrive)) yield break;

        var contentDirectory = Path.Combine(systemDrive, "XboxGames", "Minecraft for Windows", "Content");
        var assetsDirectory = Path.Combine(contentDirectory, "data", "gui", "dist", "hbui", "assets");
        if (!Directory.Exists(assetsDirectory)) yield break;

        var lightVersionIcon = Path.Combine(
            assetsDirectory,
            "grass_block-fbea7d7f754c51b4ea00.png");
        if (File.Exists(lightVersionIcon)) yield return lightVersionIcon;

        foreach (var path in Directory.EnumerateFiles(assetsDirectory, "grass_block-*.png")
                     .OrderByDescending(path => new FileInfo(path).Length))
            yield return path;
    }

    public async Task<JsonObject> LoadLocalAsync(string id, CancellationToken cancellationToken = default)
    {
        var path = _minecraft.GetVersionJsonPath(id);
        if (!File.Exists(path))
            throw new FileNotFoundException($"本地版本 {id} 尚未安装。", path);
        var text = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonNode.Parse(text) as JsonObject
               ?? throw new InvalidDataException($"版本 {id} 的 JSON 格式无效。");
    }

    public async Task<JsonObject> ResolveAsync(string id, CancellationToken cancellationToken = default)
    {
        return await ResolveRecursiveAsync(id, new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            cancellationToken);
    }

    public void SaveLocal(string id, string json)
    {
        var path = _minecraft.GetVersionJsonPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    public (ModLoader Loader, string? LoaderVersion) DetectLoader(string id, JsonObject? profile = null)
    {
        if (id.Contains("OptiFine", StringComparison.OrdinalIgnoreCase))
            return (ModLoader.OptiFine, null);

        if (id.Contains("neoforge", StringComparison.OrdinalIgnoreCase))
            return (ModLoader.NeoForge, null);

        if (id.Contains("forge", StringComparison.OrdinalIgnoreCase))
            return (ModLoader.Forge, null);

        if (id.StartsWith("fabric-loader-", StringComparison.OrdinalIgnoreCase))
        {
            var parts = id.Split('-');
            return (ModLoader.Fabric, parts.Length > 2 ? parts[2] : null);
        }

        if (id.StartsWith("quilt-loader-", StringComparison.OrdinalIgnoreCase))
        {
            var parts = id.Split('-');
            return (ModLoader.Quilt, parts.Length > 2 ? parts[2] : null);
        }

        var main = profile.Str("mainClass") ?? string.Empty;
        var profileText = profile?.ToJsonString() ?? string.Empty;
        if (profileText.Contains("optifine", StringComparison.OrdinalIgnoreCase))
            return (ModLoader.OptiFine, null);
        if (main.Contains("neoforge", StringComparison.OrdinalIgnoreCase) ||
            profileText.Contains("net.neoforged", StringComparison.OrdinalIgnoreCase))
            return (ModLoader.NeoForge, null);
        if (main.Contains("forge", StringComparison.OrdinalIgnoreCase) ||
            profileText.Contains("net.minecraftforge", StringComparison.OrdinalIgnoreCase))
            return (ModLoader.Forge, null);
        if (main.Contains("fabric", StringComparison.OrdinalIgnoreCase))
            return (ModLoader.Fabric, null);
        if (main.Contains("quilt", StringComparison.OrdinalIgnoreCase))
            return (ModLoader.Quilt, null);
        return (ModLoader.Vanilla, null);
    }

    public async Task<(ModLoader Loader, string BaseVersion)> InspectAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var profile = await LoadLocalAsync(id, cancellationToken);
        var (loader, _) = DetectLoader(id, profile);
        return (loader, profile.Str("inheritsFrom") ?? id);
    }

    private async Task<JsonObject> ResolveRecursiveAsync(
        string id,
        HashSet<string> visited,
        CancellationToken cancellationToken)
    {
        if (!visited.Add(id))
            throw new InvalidDataException($"版本继承链存在循环：{id}");

        var child = await LoadLocalAsync(id, cancellationToken);
        var parentId = child.Str("inheritsFrom");
        if (string.IsNullOrWhiteSpace(parentId))
        {
            child["id"] = id;
            return child;
        }

        var parent = await ResolveRecursiveAsync(parentId, visited, cancellationToken);
        return Merge(parent, child, id);
    }

    private static JsonObject Merge(JsonObject parent, JsonObject child, string id)
    {
        var merged = (JsonObject)parent.DeepClone();
        merged["id"] = id;

        foreach (var pair in child)
        {
            if (pair.Key is "id" or "inheritsFrom") continue;
            if (pair.Key == "libraries" && pair.Value is JsonArray childLibraries &&
                merged["libraries"] is JsonArray parentLibraries)
            {
                var combined = (JsonArray)parentLibraries.DeepClone();
                foreach (var library in childLibraries)
                    combined.Add(library?.DeepClone());
                merged["libraries"] = combined;
                continue;
            }

            if (pair.Key == "arguments" && pair.Value is JsonObject childArgs &&
                merged["arguments"] is JsonObject parentArgs)
            {
                var combined = (JsonObject)parentArgs.DeepClone();
                foreach (var key in new[] { "game", "jvm" })
                {
                    if (childArgs[key] is not JsonArray childList) continue;
                    var list = combined[key] as JsonArray ?? new JsonArray();
                    foreach (var item in childList) list.Add(item?.DeepClone());
                    combined[key] = list;
                }
                merged["arguments"] = combined;
                continue;
            }

            merged[pair.Key] = pair.Value?.DeepClone();
        }

        return merged;
    }
}
