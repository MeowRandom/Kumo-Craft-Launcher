using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kumo.Models;

namespace Kumo.Services;

public sealed record DeviceCodeSession(
    string UserCode,
    string VerificationUri,
    string DeviceCode,
    DateTimeOffset ExpiresAt,
    int IntervalSeconds);

public sealed class MicrosoftAuthService
{
    private const string LegacyClientId = "00000000402b5328";
    private const string LiveScope = "service::user.auth.xboxlive.com::MBI_SSL";
    private const string XboxUserAgent = "MinecraftLauncher/2.2.10675";
    private readonly HttpService _http;

    public MicrosoftAuthService(HttpService? http = null)
    {
        _http = http ?? new HttpService(XboxUserAgent);
    }

    public async Task<DeviceCodeSession> StartDeviceCodeAsync(CancellationToken cancellationToken = default)
    {
        var response = await _http.PostFormRawAsync(
            "https://login.live.com/oauth20_connect.srf",
            new Dictionary<string, string>
            {
                ["client_id"] = LegacyClientId,
                ["scope"] = LiveScope,
                ["response_type"] = "device_code"
            }, AuthHeaders(), cancellationToken);
        var json = ParseObject(response, "无法创建 Microsoft 设备登录请求");
        var expires = json["expires_in"]?.GetValue<int>() ?? 900;
        return new DeviceCodeSession(
            json.Str("user_code") ?? throw new InvalidDataException("Microsoft 未返回设备码。"),
            json.Str("verification_uri") ?? "https://www.microsoft.com/link",
            json.Str("device_code") ?? throw new InvalidDataException("Microsoft 未返回设备令牌。"),
            DateTimeOffset.UtcNow.AddSeconds(expires),
            json["interval"]?.GetValue<int>() ?? 5);
    }

    public async Task<AccountInfo> CompleteDeviceCodeAsync(
        DeviceCodeSession session,
        Action<string>? status,
        CancellationToken cancellationToken)
    {
        var interval = Math.Max(3, session.IntervalSeconds);
        while (DateTimeOffset.UtcNow < session.ExpiresAt)
        {
            status?.Invoke("等待你在浏览器中完成登录…");
            await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);
            var response = await _http.PostFormRawAsync(
                $"https://login.live.com/oauth20_token.srf?client_id={LegacyClientId}",
                new Dictionary<string, string>
                {
                    ["client_id"] = LegacyClientId,
                    ["device_code"] = session.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                }, AuthHeaders(), cancellationToken);
            var json = JsonNode.Parse(response.Body) as JsonObject ?? new JsonObject();
            if (response.StatusCode is >= 200 and < 300 &&
                json.Str("access_token") is { } accessToken)
            {
                status?.Invoke("Microsoft 登录成功，正在连接 Minecraft…");
                return await ExchangeForMinecraftAsync(
                    accessToken, json.Str("refresh_token") ?? "", status, cancellationToken);
            }

            var error = json.Str("error") ?? "";
            if (error == "authorization_pending") continue;
            if (error == "slow_down")
            {
                interval += 5;
                continue;
            }
            if (error == "expired_token")
                throw new MicrosoftAuthException("设备码已过期，请重新开始正版登录。");
            if (error == "authorization_declined")
                throw new MicrosoftAuthException("你取消了 Microsoft 登录。");
            throw new MicrosoftAuthException(json.Str("error_description") ??
                                             "Microsoft 登录失败，请稍后重试。");
        }
        throw new MicrosoftAuthException("设备码已过期，请重新开始正版登录。");
    }

    public async Task<AccountInfo> RefreshAsync(AccountInfo account, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(account.RefreshTokenProtected))
            throw new MicrosoftAuthException("此账户没有可用的刷新令牌，请重新登录。");
        var refreshToken = DpapiProtector.Unprotect(account.RefreshTokenProtected);
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new MicrosoftAuthException("账户令牌无法解密，请重新登录。");

        var response = await _http.PostFormRawAsync(
            $"https://login.live.com/oauth20_token.srf?client_id={LegacyClientId}",
            new Dictionary<string, string>
            {
                ["client_id"] = LegacyClientId,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token",
                ["scope"] = LiveScope
            }, AuthHeaders(), cancellationToken);
        if (response.StatusCode is < 200 or >= 300)
            throw new MicrosoftAuthException("Microsoft 登录已过期，请重新登录。");
        var json = ParseObject(response, "刷新 Microsoft 登录失败");
        var result = await ExchangeForMinecraftAsync(
            json.Str("access_token") ?? throw new MicrosoftAuthException("刷新令牌无效。"),
            json.Str("refresh_token") ?? refreshToken,
            null,
            cancellationToken);
        return result;
    }

    private async Task<AccountInfo> ExchangeForMinecraftAsync(
        string microsoftAccessToken,
        string refreshToken,
        Action<string>? status,
        CancellationToken cancellationToken)
    {
        status?.Invoke("正在验证 Xbox Live 账户…");
        var headers = new Dictionary<string, string>
        {
            ["x-xbl-contract-version"] = "1",
            ["Accept"] = "application/json"
        };
        RawHttpResponse userResponse;
        var ticket = microsoftAccessToken.Trim();
        var prefixedTicket = ticket.StartsWith("d=", StringComparison.OrdinalIgnoreCase)
            ? ticket
            : "d=" + ticket;
        userResponse = await RequestXboxUserAsync(prefixedTicket, headers, cancellationToken);
        if (userResponse.StatusCode == 401 && prefixedTicket != ticket)
        {
            LogService.Warn("Xbox Live 拒绝 d= 票证，正在尝试原始令牌格式。");
            userResponse = await RequestXboxUserAsync(ticket, headers, cancellationToken);
        }
        var userJson = ParseObject(userResponse, "Xbox Live 身份验证失败");
        var xboxToken = userJson.Str("Token")
                        ?? throw new MicrosoftAuthException("Xbox Live 未返回用户令牌。");
        var firstXui = userJson.Obj("DisplayClaims")?.Arr("xui")?.FirstOrDefault() as JsonObject;
        var userHash = firstXui?.Str("uhs");
        if (string.IsNullOrWhiteSpace(userHash))
            throw new MicrosoftAuthException("Xbox Live 未返回账户标识。");

        status?.Invoke("正在获取 Xbox 安全令牌…");
        var xstsResponse = await _http.PostJsonRawAsync(
            "https://xsts.auth.xboxlive.com/xsts/authorize",
            new
            {
                Properties = new
                {
                    SandboxId = "RETAIL",
                    UserTokens = new[] { xboxToken }
                },
                RelyingParty = "rp://api.minecraftservices.com/",
                TokenType = "JWT"
            }, headers, cancellationToken);
        var xstsJson = ParseObjectAllowXstsError(xstsResponse);
        var xstsToken = xstsJson.Str("Token")
                        ?? throw new MicrosoftAuthException("Xbox 安全令牌获取失败。");
        var xstsHash = xstsJson.Obj("DisplayClaims")?.Arr("xui")?.FirstOrDefault() as JsonObject;
        var uhs = xstsHash?.Str("uhs") ?? userHash;
        var xuid = xstsHash?.Str("xid") ?? firstXui?.Str("xid");

        status?.Invoke("正在登录 Minecraft Services…");
        var minecraftResponse = await _http.PostJsonRawAsync(
            "https://api.minecraftservices.com/authentication/login_with_xbox",
            new { identityToken = $"XBL3.0 x={uhs};{xstsToken}" },
            null, cancellationToken);
        var minecraftJson = ParseObject(minecraftResponse, "Minecraft 登录失败");
        var minecraftToken = minecraftJson.Str("access_token")
                             ?? throw new MicrosoftAuthException("Minecraft 服务未返回访问令牌。");
        var expiresIn = minecraftJson["expires_in"]?.GetValue<int>() ?? 86_400;

        status?.Invoke("正在读取 Minecraft 档案…");
        MinecraftProfile? profile;
        try
        {
            profile = await _http.GetJsonWithHeadersAsync<MinecraftProfile>(
                "https://api.minecraftservices.com/minecraft/profile",
                new Dictionary<string, string> { ["Authorization"] = "Bearer " + minecraftToken },
                cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("404"))
        {
            throw new MicrosoftAuthException("此 Microsoft 账户没有可用的 Minecraft: Java Edition 档案。");
        }
        if (profile is null || string.IsNullOrWhiteSpace(profile.Name) || string.IsNullOrWhiteSpace(profile.Id))
            throw new MicrosoftAuthException("此账户没有可用的 Minecraft: Java Edition 档案。");
        var skinUrl = profile.Skins
            .FirstOrDefault(skin => skin.State.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase))?.Url
            ?? profile.Skins.FirstOrDefault()?.Url;

        return new AccountInfo
        {
            Id = profile.Id,
            Name = profile.Name,
            MinecraftUuid = profile.Id,
            Kind = AccountKind.Microsoft,
            Xuid = xuid,
            SkinUrl = skinUrl,
            AccessToken = minecraftToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 30),
            RefreshTokenProtected = DpapiProtector.Protect(refreshToken)
        };
    }

    private Task<RawHttpResponse> RequestXboxUserAsync(
        string ticket,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        return _http.PostJsonRawAsync(
            "https://user.auth.xboxlive.com/user/authenticate",
            new
            {
                Properties = new
                {
                    AuthMethod = "RPS",
                    SiteName = "user.auth.xboxlive.com",
                    RpsTicket = ticket
                },
                RelyingParty = "http://auth.xboxlive.com",
                TokenType = "JWT"
            }, headers, cancellationToken);
    }

    private static Dictionary<string, string> AuthHeaders() => new()
    {
        ["Accept"] = "application/json"
    };

    private static JsonObject ParseObject(RawHttpResponse response, string action)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            string? description = null;
            try
            {
                var errorJson = JsonNode.Parse(response.Body) as JsonObject;
                description = errorJson?.Str("error_description") ?? errorJson?.Str("Message");
                var xerr = errorJson?["XErr"]?.ToString();
                if (!string.IsNullOrWhiteSpace(xerr))
                    description = $"{action}（XErr {xerr}）";
            }
            catch { /* The status code is enough for the user-facing error. */ }
            LogService.Warn($"{action} 返回 HTTP {response.StatusCode}：{Truncate(response.Body, 500)}");
            throw new MicrosoftAuthException(description ?? $"{action}（HTTP {response.StatusCode}）。");
        }
        return JsonNode.Parse(response.Body) as JsonObject
               ?? throw new MicrosoftAuthException(action + "：服务返回内容无效。");
    }

    private static string Truncate(string value, int length) =>
        string.IsNullOrEmpty(value) ? "(空响应)" : value[..Math.Min(value.Length, length)];

    private static JsonObject ParseObjectAllowXstsError(RawHttpResponse response)
    {
        if (response.StatusCode is >= 200 and < 300)
            return JsonNode.Parse(response.Body) as JsonObject ??
                   throw new MicrosoftAuthException("Xbox 安全令牌返回内容无效。");
        var json = JsonNode.Parse(response.Body) as JsonObject ?? new JsonObject();
        var xerr = json["XErr"]?.ToString() ?? "";
        var message = xerr switch
        {
            "2148916233" => "此 Microsoft 账户尚未创建 Xbox 档案，请先在 xbox.com 完成设置。",
            "2148916235" => "此 Microsoft 账户所在地区不支持 Xbox Live。",
            "2148916238" => "儿童账户需先由家庭管理员批准 Xbox Live 访问。",
            _ => "Xbox 安全令牌验证失败，请确认账户可访问 Minecraft。"
        };
        throw new MicrosoftAuthException(message);
    }

    private sealed class MinecraftProfile
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<MinecraftSkin> Skins { get; set; } = [];
    }

    private sealed class MinecraftSkin
    {
        public string State { get; set; } = "";
        public string Url { get; set; } = "";
    }
}

public sealed class MicrosoftAuthException(string message) : Exception(message);
