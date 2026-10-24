using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kumo.Models;

namespace Kumo.Services;

public static class InstalledModService
{
    public static IReadOnlyList<InstalledModEntry> Scan(
        string modsDirectory,
        string iconCacheDirectory)
    {
        if (!Directory.Exists(modsDirectory)) return [];

        return Directory.EnumerateFiles(modsDirectory)
            .Select(path => new
            {
                Path = path,
                FileName = Path.GetFileName(path)
            })
            .Where(file =>
                file.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                file.FileName.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
            .Select(file =>
            {
                var enabled = file.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
                var metadata = ReadMetadata(file.Path);
                return new InstalledModEntry(
                    file.FileName,
                    new FileInfo(file.Path).Length,
                    enabled,
                    metadata.ModId,
                    metadata.Name,
                    ExtractIcon(file.Path, metadata.IconEntry, iconCacheDirectory));
            })
            .OrderByDescending(entry => entry.IsEnabled)
            .ThenBy(entry => entry.PrimaryName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string SetEnabled(string modPath, bool enabled)
    {
        if (!File.Exists(modPath))
            throw new FileNotFoundException("找不到要修改的 Mod 文件。", modPath);

        var isEnabled = modPath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
        var isDisabled = modPath.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);
        if (!isEnabled && !isDisabled)
            throw new InvalidDataException("只能修改 JAR 或已关闭的 JAR Mod。");
        if (isEnabled == enabled)
            return modPath;

        string destination;
        if (enabled)
        {
            destination = modPath[..^".disabled".Length];
        }
        else
        {
            destination = modPath + ".disabled";
        }

        if (File.Exists(destination))
            throw new IOException($"文件“{Path.GetFileName(destination)}”已经存在。");

        File.Move(modPath, destination);
        return destination;
    }

    private static JarModMetadata ReadMetadata(string jarPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(jarPath);
            var fabric = FindEntry(archive, "fabric.mod.json");
            if (fabric is not null)
                return ReadFabricMetadata(fabric);

            var quilt = FindEntry(archive, "quilt.mod.json");
            if (quilt is not null)
                return ReadQuiltMetadata(quilt);

            var forge = FindEntry(archive, "META-INF/mods.toml") ??
                        FindEntry(archive, "META-INF/neoforge.mods.toml");
            if (forge is not null)
                return ReadTomlMetadata(forge);

            var legacy = FindEntry(archive, "mcmod.info");
            if (legacy is not null)
                return ReadLegacyMetadata(legacy);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            LogService.Warn($"读取 Mod 元数据失败 {Path.GetFileName(jarPath)}：{ex.Message}");
        }
        return new JarModMetadata(null, null, null);
    }

    private static JarModMetadata ReadFabricMetadata(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var id = ReadString(root, "id");
        var name = ReadString(root, "name");
        string? icon = null;
        if (root.TryGetProperty("icon", out var iconElement))
        {
            icon = iconElement.ValueKind == JsonValueKind.String
                ? iconElement.GetString()
                : iconElement.ValueKind == JsonValueKind.Object
                    ? iconElement.EnumerateObject()
                        .OrderByDescending(property => property.Name)
                        .Select(property => property.Value.GetString())
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                    : null;
        }
        return new JarModMetadata(id, name, NormalizeEntryPath(icon));
    }

    private static JarModMetadata ReadQuiltMetadata(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var loader = root.TryGetProperty("quilt_loader", out var loaderElement)
            ? loaderElement
            : default;
        var metadata = root.TryGetProperty("metadata", out var metadataElement)
            ? metadataElement
            : default;
        return new JarModMetadata(
            loader.ValueKind == JsonValueKind.Object ? ReadString(loader, "id") : null,
            metadata.ValueKind == JsonValueKind.Object ? ReadString(metadata, "name") : null,
            NormalizeEntryPath(
                metadata.ValueKind == JsonValueKind.Object
                    ? ReadString(metadata, "icon")
                    : null));
    }

    private static JarModMetadata ReadTomlMetadata(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, true);
        var text = reader.ReadToEnd();
        return new JarModMetadata(
            ReadTomlString(text, "modId"),
            ReadTomlString(text, "displayName"),
            NormalizeEntryPath(ReadTomlString(text, "logoFile")));
    }

    private static JarModMetadata ReadLegacyMetadata(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind != JsonValueKind.Array ||
            document.RootElement.GetArrayLength() == 0)
            return new JarModMetadata(null, null, null);
        var root = document.RootElement[0];
        return new JarModMetadata(
            ReadString(root, "modid"),
            ReadString(root, "name"),
            NormalizeEntryPath(ReadString(root, "logoFile")));
    }

    private static string? ExtractIcon(
        string jarPath,
        string? iconEntry,
        string iconCacheDirectory)
    {
        if (string.IsNullOrWhiteSpace(iconEntry)) return null;
        try
        {
            using var archive = ZipFile.OpenRead(jarPath);
            var entry = FindEntry(archive, iconEntry);
            if (entry is null || entry.Length == 0) return null;

            Directory.CreateDirectory(iconCacheDirectory);
            var sourceInfo = new FileInfo(jarPath);
            var key =
                $"{sourceInfo.FullName}|{sourceInfo.Length}|{sourceInfo.LastWriteTimeUtc.Ticks}|{iconEntry}";
            var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
            var extension = Path.GetExtension(entry.FullName);
            if (string.IsNullOrWhiteSpace(extension) || extension.Length > 8) extension = ".png";
            var destination = Path.Combine(
                iconCacheDirectory, hash + extension.ToLowerInvariant());
            if (File.Exists(destination) && new FileInfo(destination).Length > 0)
                return destination;

            var temporary = destination + ".tmp";
            using (var input = entry.Open())
            using (var output = File.Create(temporary))
                input.CopyTo(output);
            File.Move(temporary, destination, true);
            return destination;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            LogService.Warn($"提取 Mod 图标失败 {Path.GetFileName(jarPath)}：{ex.Message}");
            return null;
        }
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string path)
    {
        var normalized = NormalizeEntryPath(path);
        return archive.Entries.FirstOrDefault(entry =>
            string.Equals(
                entry.FullName.Replace('\\', '/'),
                normalized,
                StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadTomlString(string text, string key)
    {
        var match = Regex.Match(
            text,
            $@"^\s*{Regex.Escape(key)}\s*=\s*[""'](?<value>[^""']+)[""']",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private static string NormalizeEntryPath(string? path) =>
        (path ?? "").Replace('\\', '/').TrimStart('/');

    private sealed record JarModMetadata(string? ModId, string? Name, string? IconEntry);
}
