using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kumo.Models;
using SkiaSharp;

namespace Kumo.Services;

public sealed record MinecraftCapeInfo(
    string Id,
    string Alias,
    string TextureUrl,
    bool IsActive);

public sealed record MinecraftProfileSnapshot(
    string Name,
    string? SkinUrl,
    string SkinVariant,
    IReadOnlyList<MinecraftCapeInfo> Capes);

public sealed record MinecraftCapeChoice(
    string? Id,
    string Name,
    string? TexturePath = null);

public sealed record MinecraftProfileCacheEntry(
    MinecraftProfileSnapshot Profile,
    DateTimeOffset CachedAt);

public sealed class MinecraftProfileService
{
    private const string ProfileUrl = "https://api.minecraftservices.com/minecraft/profile";
    private const string SkinsUrl = "https://api.minecraftservices.com/minecraft/profile/skins";
    private const string ActiveCapeUrl = "https://api.minecraftservices.com/minecraft/profile/capes/active";
    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly HttpService _http;
    private readonly string? _cacheDirectory;

    public MinecraftProfileService(HttpService http, string? dataDirectory = null)
    {
        _http = http;
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            _cacheDirectory = Path.Combine(dataDirectory, "cache", "minecraft-profiles");
            Directory.CreateDirectory(_cacheDirectory);
        }
    }

    public async Task<MinecraftProfileSnapshot> GetProfileAsync(
        AccountInfo account,
        CancellationToken cancellationToken)
    {
        var response = await SendAuthorizedAsync(HttpMethod.Get, ProfileUrl, account, null, cancellationToken);
        EnsureSuccess(response, "读取 Minecraft 档案");
        var json = ParseObject(response.Body, "Minecraft 档案");

        var skins = json.Arr("skins")?.OfType<JsonObject>().ToList() ?? [];
        var skin = skins.FirstOrDefault(item =>
                       string.Equals(item.Str("state"), "ACTIVE", StringComparison.OrdinalIgnoreCase))
                   ?? skins.FirstOrDefault();
        var capes = json.Arr("capes")?
            .OfType<JsonObject>()
            .Select(item => new MinecraftCapeInfo(
                item.Str("id") ?? "",
                item.Str("alias") ?? "Minecraft 披风",
                PlayerAvatarService.NormalizeTextureUrl(item.Str("url")) ?? "",
                string.Equals(item.Str("state"), "ACTIVE", StringComparison.OrdinalIgnoreCase)))
            .Where(item => item.Id.Length > 0)
            .ToList() ?? [];

        var profile = new MinecraftProfileSnapshot(
            json.Str("name") ?? account.Name,
            PlayerAvatarService.NormalizeTextureUrl(skin?.Str("url")),
            skin?.Str("variant") ?? "CLASSIC",
            capes);
        SaveCachedProfile(account, profile);
        return profile;
    }

    public MinecraftProfileCacheEntry? GetCachedProfile(AccountInfo account)
    {
        if (_cacheDirectory is null || string.IsNullOrWhiteSpace(account.Id)) return null;
        var path = GetCachePath(account);
        if (!File.Exists(path)) return null;
        try
        {
            var document = JsonSerializer.Deserialize<ProfileCacheDocument>(
                File.ReadAllText(path), CacheJsonOptions);
            if (document is null || document.Version != 1 ||
                string.IsNullOrWhiteSpace(document.Name))
                return null;
            var profile = new MinecraftProfileSnapshot(
                document.Name,
                document.SkinUrl,
                string.IsNullOrWhiteSpace(document.SkinVariant)
                    ? "CLASSIC"
                    : document.SkinVariant,
                document.Capes ?? []);
            return new MinecraftProfileCacheEntry(profile, document.CachedAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogService.Warn($"读取 Minecraft 外观缓存失败：{ex.Message}");
            return null;
        }
    }

    public void SaveCachedProfile(AccountInfo account, MinecraftProfileSnapshot profile)
    {
        if (_cacheDirectory is null || string.IsNullOrWhiteSpace(account.Id)) return;
        try
        {
            var document = new ProfileCacheDocument
            {
                Version = 1,
                CachedAt = DateTimeOffset.UtcNow,
                Name = profile.Name,
                SkinUrl = profile.SkinUrl,
                SkinVariant = profile.SkinVariant,
                Capes = profile.Capes.ToList()
            };
            var path = GetCachePath(account);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary,
                JsonSerializer.Serialize(document, CacheJsonOptions));
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warn($"写入 Minecraft 外观缓存失败：{ex.Message}");
        }
    }

    public async Task UploadSkinAsync(
        AccountInfo account,
        string skinPath,
        bool slim,
        CancellationToken cancellationToken)
    {
        ValidateSkinFile(skinPath);
        await using var file = File.OpenRead(skinPath);
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(slim ? "slim" : "classic", Encoding.UTF8), "variant");
        var fileContent = new StreamContent(file);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(fileContent, "file", Path.GetFileName(skinPath));

        var response = await SendAuthorizedAsync(HttpMethod.Post, SkinsUrl, account, content, cancellationToken);
        EnsureSuccess(response, "上传 Minecraft 皮肤");
    }

    public async Task SetCapeAsync(
        AccountInfo account,
        string capeId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(capeId))
            throw new InvalidOperationException("披风标识无效。");
        var body = JsonSerializer.Serialize(new { capeId });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await SendAuthorizedAsync(HttpMethod.Put, ActiveCapeUrl, account, content, cancellationToken);
        EnsureSuccess(response, "更换 Minecraft 披风");
    }

    public async Task ClearCapeAsync(AccountInfo account, CancellationToken cancellationToken)
    {
        var response = await SendAuthorizedAsync(HttpMethod.Delete, ActiveCapeUrl, account, null, cancellationToken);
        EnsureSuccess(response, "移除 Minecraft 披风");
    }

    public static void ValidateSkinFile(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("找不到所选皮肤文件。", path);
        if (file.Length == 0 || file.Length > 1024 * 1024)
            throw new InvalidDataException("皮肤文件应为不超过 1 MB 的 PNG。");

        using var stream = file.OpenRead();
        using var codec = SKCodec.Create(stream);
        if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Png)
            throw new InvalidDataException("请选择 PNG 格式的 Minecraft 皮肤。");
        if (codec.Info.Width != 64 || codec.Info.Height is not (32 or 64))
            throw new InvalidDataException("皮肤尺寸应为 64×64 或 64×32 像素。");
    }

    private Task<RawHttpResponse> SendAuthorizedAsync(
        HttpMethod method,
        string url,
        AccountInfo account,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        if (account.Kind != AccountKind.Microsoft || string.IsNullOrWhiteSpace(account.AccessToken))
            throw new MinecraftProfileException("请先使用 Microsoft 正版账户登录。");
        return _http.SendRawAsync(method, url, content,
            new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + account.AccessToken,
                ["Accept"] = "application/json"
            },
            cancellationToken);
    }

    private static JsonObject ParseObject(string body, string source)
    {
        try
        {
            return JsonNode.Parse(body) as JsonObject
                   ?? throw new MinecraftProfileException($"{source}返回内容无效。");
        }
        catch (JsonException)
        {
            throw new MinecraftProfileException($"{source}返回内容无效。");
        }
    }

    private static void EnsureSuccess(RawHttpResponse response, string action)
    {
        if (response.StatusCode is >= 200 and < 300) return;
        string? message = null;
        try
        {
            var json = JsonNode.Parse(response.Body) as JsonObject;
            message = json.Str("errorMessage") ?? json.Str("Message") ?? json.Str("error") ?? json.Str("details");
        }
        catch (JsonException)
        {
            // The HTTP status remains useful when Mojang returns a non-JSON error.
        }

        LogService.Warn($"{action}返回 HTTP {response.StatusCode}");
        if (response.StatusCode == 401)
            throw new MinecraftProfileException("Minecraft 登录已过期，请重新登录正版账户。");
        throw new MinecraftProfileException(message ?? $"{action}失败（HTTP {response.StatusCode}）。");
    }

    private string GetCachePath(AccountInfo account)
    {
        var key = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(account.Id))).ToLowerInvariant();
        return Path.Combine(_cacheDirectory!, $"profile-{key}.json");
    }

    private sealed class ProfileCacheDocument
    {
        public int Version { get; set; }
        public DateTimeOffset CachedAt { get; set; }
        public string Name { get; set; } = "";
        public string? SkinUrl { get; set; }
        public string SkinVariant { get; set; } = "CLASSIC";
        public List<MinecraftCapeInfo>? Capes { get; set; } = [];
    }
}

public sealed class MinecraftProfileException(string message) : Exception(message);
