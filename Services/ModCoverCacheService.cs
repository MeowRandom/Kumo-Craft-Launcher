using System.IO;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace Kumo.Services;

public sealed class ModCoverCacheService
{
    private static readonly SKColor ModCardBackground = new(0x0D, 0x14, 0x1A);
    private static readonly SKColor ModCardLightBackground = new(0xFB, 0xFD, 0xFC);
    private readonly HttpService _http;
    private readonly string _coverDirectory;

    public ModCoverCacheService(HttpService http, string dataDirectory)
    {
        _http = http;
        _coverDirectory = Path.Combine(dataDirectory, "cache", "covers-v3");
    }

    public Task<string?> GetCoverPathAsync(string? coverUrl, CancellationToken cancellationToken) =>
        GetThemeCoverPathAsync(coverUrl, false, cancellationToken);

    public async Task<string?> GetThemeCoverPathAsync(
        string? coverUrl,
        bool useLightBackground,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(coverUrl)) return null;
        if (coverUrl.StartsWith("//", StringComparison.Ordinal)) coverUrl = "https:" + coverUrl;
        if (!Uri.TryCreate(coverUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps) return null;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(coverUrl))).ToLowerInvariant();
        var extension = Path.GetExtension(uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 8) extension = ".img";
        Directory.CreateDirectory(_coverDirectory);
        var path = Path.Combine(_coverDirectory, hash + extension);
        var displayPath = GetDisplayPath(path, useLightBackground);
        if (GetCachedThemeDisplayPath(path, useLightBackground) is { } cachedDisplayPath)
            return cachedDisplayPath;

        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                await _http.DownloadFileAsync(coverUrl, path, null, null, null, cancellationToken);
            if (!File.Exists(path) || new FileInfo(path).Length == 0) return null;
            if (GetCachedThemeDisplayPath(path, useLightBackground) is { } convertedDisplayPath)
                return convertedDisplayPath;
            var converted = await Task.Run(
                () => ConvertToDisplayPngForTheme(path, displayPath, useLightBackground),
                cancellationToken);
            return converted ? displayPath : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogService.Warn($"封面加载失败 {uri.Host}：{ex.Message}");
            return null;
        }
    }

    internal static string? GetCachedDisplayPath(string sourcePath)
        => GetCachedThemeDisplayPath(sourcePath, false);

    private static string? GetCachedThemeDisplayPath(string sourcePath, bool useLightBackground)
    {
        var displayPath = GetDisplayPath(sourcePath, useLightBackground);
        return File.Exists(displayPath) && new FileInfo(displayPath).Length > 0
            ? displayPath
            : null;
    }

    private static string GetDisplayPath(string sourcePath, bool useLightBackground) =>
        sourcePath + (useLightBackground ? ".display-light.png" : ".display.png");

    internal static bool ConvertToDisplayPng(string sourcePath, string destinationPath) =>
        ConvertToDisplayPngForTheme(sourcePath, destinationPath, false);

    internal static bool ConvertToDisplayPngForTheme(
        string sourcePath,
        string destinationPath,
        bool useLightBackground)
    {
        try
        {
            using var sourceBitmap = SKBitmap.Decode(sourcePath);
            if (sourceBitmap is null) return false;
            using var bitmap = new SKBitmap(new SKImageInfo(
                sourceBitmap.Width, sourceBitmap.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(useLightBackground ? ModCardLightBackground : ModCardBackground);
                canvas.DrawBitmap(sourceBitmap, 0, 0);
                canvas.Flush();
            }
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            if (encoded is null) return false;
            var temporaryPath = destinationPath + ".tmp";
            using (var output = File.Create(temporaryPath))
                encoded.SaveTo(output);
            File.Move(temporaryPath, destinationPath, true);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Warn($"封面格式转换失败：{ex.Message}");
            return false;
        }
    }
}
