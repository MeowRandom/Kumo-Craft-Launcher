using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Kumo.Models;
using SkiaSharp;

namespace Kumo.Services;

public sealed class PlayerAvatarService
{
    private const int AvatarSize = 64;
    private const string VersionManifestUrl =
        "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private readonly HttpService _http;
    private readonly string _avatarDirectory;
    private readonly string _dataDirectory;
    private readonly string _defaultSkinDirectory;
    private readonly SemaphoreSlim _defaultSkinGate = new(1, 1);

    public PlayerAvatarService(HttpService http, string dataDirectory)
    {
        _http = http;
        _dataDirectory = dataDirectory;
        _avatarDirectory = Path.Combine(dataDirectory, "cache", "avatars-v1");
        _defaultSkinDirectory = Path.Combine(dataDirectory, "cache", "default-skins-v1");
        Directory.CreateDirectory(_avatarDirectory);
        Directory.CreateDirectory(_defaultSkinDirectory);
    }

    public async Task<string?> GetAvatarAsync(AccountInfo account, CancellationToken cancellationToken)
    {
        if (account.Kind == AccountKind.Microsoft && NormalizeUuid(account.MinecraftUuid) is { Length: 32 } uuid)
        {
            if (string.IsNullOrWhiteSpace(account.SkinUrl))
                account.SkinUrl = await FetchSkinUrlAsync(uuid, cancellationToken);

            if (account.SkinUrl is { } skinUrl && NormalizeTextureUrl(skinUrl) is { } secureSkinUrl)
            {
                account.SkinUrl = secureSkinUrl;
                try
                {
                    return await CreateSkinAvatarAsync(secureSkinUrl, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogService.Warn($"无法生成玩家皮肤头像，将使用官方默认头像：{ex.Message}");
                }
            }
        }

        var defaultSkin = await EnsureDefaultSkinAsync(account.OfflineSkinModel, cancellationToken);
        return defaultSkin is null ? null : CreateAvatarFromSkin(defaultSkin, account.OfflineSkinModel);
    }

    private async Task<string?> FetchSkinUrlAsync(string uuid, CancellationToken cancellationToken)
    {
        try
        {
            var profile = await _http.GetJsonAsync<SessionProfile>(
                $"https://sessionserver.mojang.com/session/minecraft/profile/{uuid}",
                cancellationToken);
            var encoded = profile.Properties
                .FirstOrDefault(property => property.Name == "textures")?.Value;
            if (string.IsNullOrWhiteSpace(encoded)) return null;

            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var textures = JsonNode.Parse(decoded)?["textures"] as JsonObject;
            return NormalizeTextureUrl(textures?["SKIN"]?["url"]?.GetValue<string>());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Warn($"无法读取 Minecraft 皮肤信息：{ex.Message}");
            return null;
        }
    }

    private async Task<string> CreateSkinAvatarAsync(string skinUrl, CancellationToken cancellationToken)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(skinUrl))).ToLowerInvariant();
        var skinPath = Path.Combine(_avatarDirectory, key + ".skin.png");
        var avatarPath = Path.Combine(_avatarDirectory, key + ".avatar.png");
        if (File.Exists(avatarPath) && new FileInfo(avatarPath).Length > 0) return avatarPath;

        if (!File.Exists(skinPath) || new FileInfo(skinPath).Length == 0)
            await _http.DownloadFileAsync(skinUrl, skinPath, null, null, null, cancellationToken);

        await Task.Run(() => CropHead(skinPath, avatarPath), cancellationToken);
        if (!File.Exists(avatarPath))
            throw new InvalidDataException("Minecraft 皮肤格式无效。");
        return avatarPath;
    }

    private static void CropHead(string skinPath, string destinationPath)
    {
        using var skin = SKBitmap.Decode(skinPath);
        if (skin is null || skin.Width < 64 || skin.Height < 32)
            throw new InvalidDataException("Minecraft 皮肤尺寸不正确。");

        using var head = new SKBitmap(new SKImageInfo(
            AvatarSize, AvatarSize, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(head))
        {
            canvas.Clear(SKColors.Transparent);
            using var skinImage = SKImage.FromBitmap(skin);
            using var paint = new SKPaint
            {
                IsAntialias = false
            };
            var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
            canvas.DrawImage(skinImage, new SKRect(8, 8, 16, 16),
                new SKRect(0, 0, AvatarSize, AvatarSize), sampling, paint);
            canvas.DrawImage(skinImage, new SKRect(40, 8, 48, 16),
                new SKRect(0, 0, AvatarSize, AvatarSize), sampling, paint);
            canvas.Flush();
        }

        SavePng(head, destinationPath);
    }

    private string CreateAvatarFromSkin(string skinPath, OfflineSkinModel skinModel)
    {
        var avatarPath = Path.Combine(_avatarDirectory, $"official-{skinModel.ToString().ToLowerInvariant()}.png");
        if (!File.Exists(avatarPath) || new FileInfo(avatarPath).Length == 0)
            CropHead(skinPath, avatarPath);
        return avatarPath;
    }

    private async Task<string?> EnsureDefaultSkinAsync(
        OfflineSkinModel skinModel,
        CancellationToken cancellationToken)
    {
        await _defaultSkinGate.WaitAsync(cancellationToken);
        try
        {
            var cached = GetDefaultSkinPath(skinModel);
            if (File.Exists(cached) && new FileInfo(cached).Length > 0) return cached;

            if (TryExtractDefaultSkinFromInstalledClients(skinModel, cached)) return cached;
            await DownloadOfficialClientSkinsAsync(cancellationToken);
            return File.Exists(cached) ? cached : null;
        }
        finally
        {
            _defaultSkinGate.Release();
        }
    }

    private bool TryExtractDefaultSkinFromInstalledClients(OfflineSkinModel skinModel, string destination)
    {
        var versionDirectory = Path.Combine(_dataDirectory, "versions");
        if (!Directory.Exists(versionDirectory)) return false;

        foreach (var jarPath in Directory.EnumerateFiles(versionDirectory, "*.jar", SearchOption.AllDirectories)
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                using var archive = ZipFile.OpenRead(jarPath);
                if (TryExtractEntry(archive, skinModel, destination)) return true;
            }
            catch (InvalidDataException)
            {
                // Ignore partial or non-ZIP files in the versions directory.
            }
            catch (IOException)
            {
                // A running game may temporarily lock its client JAR.
            }
        }
        return false;
    }

    private async Task DownloadOfficialClientSkinsAsync(CancellationToken cancellationToken)
    {
        var manifestJson = await _http.GetStringAsync(VersionManifestUrl, cancellationToken);
        var manifest = JsonNode.Parse(manifestJson) as JsonObject
                       ?? throw new InvalidDataException("Mojang 版本清单格式无效。");
        var releaseId = manifest["latest"]?["release"]?.GetValue<string>()
                        ?? throw new InvalidDataException("Mojang 版本清单缺少最新正式版。");
        var versionUrl = manifest["versions"]?.AsArray()
            .OfType<JsonObject>()
            .FirstOrDefault(version => version["id"]?.GetValue<string>() == releaseId)?["url"]
            ?.GetValue<string>()
            ?? throw new InvalidDataException("找不到最新正式版客户端信息。");

        var versionJson = JsonNode.Parse(await _http.GetStringAsync(versionUrl, cancellationToken)) as JsonObject
                          ?? throw new InvalidDataException("Mojang 客户端信息格式无效。");
        var client = versionJson["downloads"]?["client"] as JsonObject
                     ?? throw new InvalidDataException("Mojang 客户端信息缺少下载地址。");
        var url = client["url"]?.GetValue<string>()
                  ?? throw new InvalidDataException("Mojang 客户端下载地址为空。");
        var sha1 = client["sha1"]?.GetValue<string>();
        var size = client["size"]?.GetValue<long>();

        var jarPath = Path.Combine(_defaultSkinDirectory, "latest-client.jar");
        TryDelete(jarPath);
        try
        {
            await _http.DownloadFileAsync(url, jarPath, sha1, size, null, cancellationToken);
            using var archive = ZipFile.OpenRead(jarPath);
            foreach (var model in Enum.GetValues<OfflineSkinModel>())
            {
                var destination = GetDefaultSkinPath(model);
                if (!File.Exists(destination))
                    TryExtractEntry(archive, model, destination);
            }
        }
        finally
        {
            TryDelete(jarPath);
        }
    }

    private string GetDefaultSkinPath(OfflineSkinModel skinModel) =>
        Path.Combine(_defaultSkinDirectory, $"{skinModel.ToString().ToLowerInvariant()}.skin.png");

    private static bool TryExtractEntry(
        ZipArchive archive,
        OfflineSkinModel skinModel,
        string destination)
    {
        var entries = skinModel == OfflineSkinModel.Alex
            ? new[]
            {
                "assets/minecraft/textures/entity/player/slim/alex.png",
                "assets/minecraft/textures/entity/alex.png"
            }
            : new[]
            {
                "assets/minecraft/textures/entity/player/wide/steve.png",
                "assets/minecraft/textures/entity/steve.png"
            };
        var entry = entries.Select(archive.GetEntry).FirstOrDefault(value => value is not null);
        if (entry is null) return false;

        var temporaryPath = destination + ".tmp";
        entry.ExtractToFile(temporaryPath, true);
        File.Move(temporaryPath, destination, true);
        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Temporary client archives may remain locked by antivirus software.
        }
    }

    private static void SavePng(SKBitmap bitmap, string destinationPath)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var temporaryPath = destinationPath + ".tmp";
        using (var stream = File.Create(temporaryPath))
            encoded.SaveTo(stream);
        File.Move(temporaryPath, destinationPath, true);
    }

    internal static string? NormalizeTextureUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.Host.Equals("textures.minecraft.net", StringComparison.OrdinalIgnoreCase) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;
        return new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri.AbsoluteUri;
    }

    private static string? NormalizeUuid(string value) =>
        Guid.TryParse(value, out var uuid)
            ? uuid.ToString("N")
            : value.Length == 32 && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : null;

    private sealed class SessionProfile
    {
        public List<SessionProperty> Properties { get; set; } = [];
    }

    private sealed class SessionProperty
    {
        public string Name { get; set; } = "";
        public string Value { get; set; } = "";
    }
}
