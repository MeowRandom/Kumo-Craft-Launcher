using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace Kumo.Services;

public sealed record AppearancePreviewSet(
    string? FrontPath,
    string? ElytraPath,
    string? CapeFrontPath = null,
    string? CapeBackPath = null);

public sealed record MinecraftAppearanceAssets(
    string? SkinPath,
    IReadOnlyDictionary<string, string> CapeTextures,
    IReadOnlyDictionary<string, AppearancePreviewSet> Previews);

public sealed class MinecraftAppearancePreviewService
{
    private const int PreviewWidth = 128;
    private const int PreviewHeight = 184;
    private static readonly SKSamplingOptions NearestSampling =
        new(SKFilterMode.Nearest, SKMipmapMode.None);
    private readonly HttpService _http;
    private readonly string _cacheDirectory;

    public MinecraftAppearancePreviewService(HttpService http, string dataDirectory)
    {
        _http = http;
        _cacheDirectory = Path.Combine(dataDirectory, "cache", "appearance-previews-v1");
        Directory.CreateDirectory(_cacheDirectory);
    }

    public async Task<MinecraftAppearanceAssets> CreateAsync(
        MinecraftProfileSnapshot profile,
        CancellationToken cancellationToken)
    {
        var skinPath = await EnsureTextureAsync(profile.SkinUrl, "skin", cancellationToken);
        var previews = new Dictionary<string, AppearancePreviewSet>(StringComparer.OrdinalIgnoreCase);
        var capeTextures = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        previews[""] = await RenderPreviewAsync(
            skinPath, null, "", profile.SkinVariant, cancellationToken);

        using var gate = new SemaphoreSlim(4);
        var entries = await Task.WhenAll(profile.Capes.Select(async cape =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var capePath = await EnsureTextureAsync(cape.TextureUrl, "cape", cancellationToken);
                if (capePath is not null) capeTextures[cape.Id] = capePath;
                var previews = await RenderPreviewAsync(
                    skinPath, capePath, cape.Id, profile.SkinVariant, cancellationToken);
                return (cape.Id, Previews: previews);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogService.Warn($"无法生成披风 {cape.Alias} 的外观预览：{ex.Message}");
                return (cape.Id, Previews: new AppearancePreviewSet(null, null));
            }
            finally
            {
                gate.Release();
            }
        }));

        foreach (var entry in entries) previews[entry.Id] = entry.Previews;
        return new MinecraftAppearanceAssets(skinPath, capeTextures, previews);
    }

    public Task<AppearancePreviewSet> CreateLocalPreviewAsync(
        string skinPath,
        string? capePath,
        string cacheKey,
        string skinVariant,
        CancellationToken cancellationToken) =>
        RenderPreviewAsync(skinPath, capePath, cacheKey, skinVariant, cancellationToken);

    private async Task<string?> EnsureTextureAsync(
        string? url,
        string prefix,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        var path = Path.Combine(_cacheDirectory, $"{prefix}-{key}.png");
        if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
        try
        {
            await _http.DownloadFileAsync(url, path, null, null, null, cancellationToken);
            return File.Exists(path) ? path : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Warn($"无法下载外观纹理 {Path.GetFileName(path)}：{ex.Message}");
            return null;
        }
    }

    private Task<AppearancePreviewSet> RenderPreviewAsync(
        string? skinPath,
        string? capePath,
        string cacheKey,
        string skinVariant,
        CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var variantKey = skinVariant.Equals("SLIM", StringComparison.OrdinalIgnoreCase)
                ? "slim"
                : "classic";
            var keySource = $"{skinPath}|{capePath}|{cacheKey}|{variantKey}|cape-v6";
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keySource)))
                .ToLowerInvariant();
            var frontPath = Path.Combine(_cacheDirectory, $"front-{key}.png");
            var elytraPath = Path.Combine(_cacheDirectory, $"elytra-{key}.png");
            var capeFrontPath = Path.Combine(_cacheDirectory, $"cape-front-{key}.png");
            var capeBackPath = Path.Combine(_cacheDirectory, $"cape-back-{key}.png");
            if (File.Exists(frontPath) && new FileInfo(frontPath).Length > 0 &&
                File.Exists(elytraPath) && new FileInfo(elytraPath).Length > 0 &&
                (capePath is null ||
                 File.Exists(capeFrontPath) && File.Exists(capeBackPath)))
                return new AppearancePreviewSet(frontPath, elytraPath,
                    capePath is null ? null : capeFrontPath,
                    capePath is null ? null : capeBackPath);

            using var skin = LoadTexture(skinPath);
            using var cape = LoadTexture(capePath);
            RenderFront(skin, cape, variantKey == "slim", frontPath);
            RenderElytra(cape, elytraPath);
            if (cape is not null)
            {
                RenderCapeFace(cape, capeFrontPath, mirrored: false);
                RenderCapeFace(cape, capeBackPath, mirrored: true);
            }
            return new AppearancePreviewSet(frontPath, elytraPath,
                cape is null ? null : capeFrontPath,
                cape is null ? null : capeBackPath);
        }, cancellationToken);

    private static SKBitmap? LoadTexture(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            return SKBitmap.Decode(path);
        }
        catch (Exception ex)
        {
            LogService.Warn($"外观纹理无法解码：{ex.Message}");
            return null;
        }
    }

    private static void RenderFront(SKBitmap? skin, SKBitmap? cape, bool slim, string destination)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(
            PreviewWidth, PreviewHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        const int scale = 5;
        const int left = 24;
        const int top = 12;

        DrawGroundShadow(canvas);
        if (cape is not null) DrawCapeEdges(canvas, cape, left, top, scale);
        if (skin is { Width: >= 64, Height: >= 32 })
            DrawPlayerFront(canvas, skin, left, top, scale, slim);
        else
            DrawFallbackPlayer(canvas, left, top, scale, slim);
        SavePng(bitmap, destination);
    }

    private static void DrawPlayerFront(
        SKCanvas canvas,
        SKBitmap skin,
        int left,
        int top,
        int scale,
        bool slim)
    {
        using var image = SKImage.FromBitmap(skin);
        var armWidth = slim ? 3 : 4;
        var rightArmX = slim ? left + 4 * scale : left;
        var leftArmX = left + (4 + 8) * scale;

        DrawRegion(canvas, image, 4, 20, 4, 12, left + 4 * scale, top + 20 * scale, scale);
        DrawRegion(canvas, image,
            skin.Height >= 64 ? 20 : 4, skin.Height >= 64 ? 52 : 20, 4, 12,
            left + 8 * scale, top + 20 * scale, scale);
        DrawRegion(canvas, image, 20, 20, 8, 12, left + 4 * scale, top + 8 * scale, scale);
        DrawRegion(canvas, image, 44, 20, armWidth, 12, rightArmX, top + 8 * scale, scale);
        DrawRegion(canvas, image,
            skin.Height >= 64 ? 36 : 44, skin.Height >= 64 ? 52 : 20, armWidth, 12,
            leftArmX, top + 8 * scale, scale);
        DrawRegion(canvas, image, 8, 8, 8, 8, left + 4 * scale, top, scale);

        if (skin.Height >= 64)
        {
            DrawRegion(canvas, image, 0, 36, 4, 12, left + 4 * scale, top + 20 * scale, scale);
            DrawRegion(canvas, image, 0, 52, 4, 12, left + 8 * scale, top + 20 * scale, scale);
            DrawRegion(canvas, image, 20, 36, 8, 12, left + 4 * scale, top + 8 * scale, scale);
            DrawRegion(canvas, image, 44, 36, armWidth, 12, rightArmX, top + 8 * scale, scale);
            DrawRegion(canvas, image, 52, 52, armWidth, 12, leftArmX, top + 8 * scale, scale);
            DrawRegion(canvas, image, 40, 8, 8, 8, left + 4 * scale, top, scale);
        }
    }

    private static void DrawFallbackPlayer(SKCanvas canvas, int left, int top, int scale, bool slim)
    {
        using var paint = new SKPaint { IsAntialias = false };
        var armWidth = slim ? 3 : 4;
        var rightArmX = slim ? left + 4 * scale : left;
        var leftArmX = left + 12 * scale;

        paint.Color = new SKColor(0x4B, 0x7A, 0x68);
        canvas.DrawRect(left + 4 * scale, top + 8 * scale, 8 * scale, 12 * scale, paint);
        paint.Color = new SKColor(0xC9, 0x99, 0x73);
        canvas.DrawRect(rightArmX, top + 8 * scale, armWidth * scale, 12 * scale, paint);
        canvas.DrawRect(leftArmX, top + 8 * scale, armWidth * scale, 12 * scale, paint);
        canvas.DrawRect(left + 4 * scale, top, 8 * scale, 8 * scale, paint);
        paint.Color = new SKColor(0x35, 0x4C, 0x67);
        canvas.DrawRect(left + 4 * scale, top + 20 * scale, 4 * scale, 12 * scale, paint);
        canvas.DrawRect(left + 8 * scale, top + 20 * scale, 4 * scale, 12 * scale, paint);
    }

    private static void DrawCapeEdges(SKCanvas canvas, SKBitmap cape, int left, int top, int scale)
    {
        if (cape.Width < 10 || cape.Height < 16) return;
        using var paint = new SKPaint { IsAntialias = false };
        for (var row = 0; row < 16; row++)
        {
            paint.Color = cape.GetPixel(0, row);
            canvas.DrawRect(left - scale, top + (8 + row) * scale, scale, scale, paint);
            paint.Color = cape.GetPixel(9, row);
            canvas.DrawRect(left + 16 * scale, top + (8 + row) * scale, scale, scale, paint);
        }
    }

    private static void RenderElytra(SKBitmap? cape, string destination)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(
            PreviewWidth, PreviewHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        DrawGroundShadow(canvas);

        using var leftWing = CreateWingPath(false);
        using var rightWing = CreateWingPath(true);
        using var leftInnerWing = CreateInnerWingPath(false);
        using var rightInnerWing = CreateInnerWingPath(true);
        using var fallbackPaint = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(64, 36),
                new SKPoint(64, 164),
                [new SKColor(0x54, 0x65, 0x70), new SKColor(0x2C, 0x38, 0x42)],
                [0f, 1f],
                SKShaderTileMode.Clamp)
        };
        canvas.DrawPath(leftWing, fallbackPaint);
        canvas.DrawPath(rightWing, fallbackPaint);

        if (cape is { Width: >= 10, Height: >= 16 })
        {
            using var patch = CreateCapePatch(cape);
            using var image = SKImage.FromBitmap(patch);
            var source = new SKRect(0, 0, patch.Width, patch.Height);
            var leftTarget = new SKRect(4, 23, 62, 174);
            var rightTarget = new SKRect(66, 23, 124, 174);
            DrawClippedCape(canvas, image, source, leftTarget, leftWing, mirror: false);
            DrawClippedCape(canvas, image, source, rightTarget, rightWing, mirror: true);
        }

        using var platePaint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(0xE8, 0xF4, 0xEE, 21)
        };
        canvas.DrawPath(leftInnerWing, platePaint);
        canvas.DrawPath(rightInnerWing, platePaint);

        using var outlinePaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2.5f,
            Color = new SKColor(0x0B, 0x11, 0x16, 150)
        };
        canvas.DrawPath(leftWing, outlinePaint);
        canvas.DrawPath(rightWing, outlinePaint);
        DrawElytraConnector(canvas);
        DrawElytraVeins(canvas);
        SavePng(bitmap, destination);
    }

    private static void RenderCapeFace(SKBitmap cape, string destination, bool mirrored)
    {
        if (cape.Width < 10 || cape.Height < 16) return;
        using var bitmap = new SKBitmap(new SKImageInfo(
            72, 120, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var shape = new SKPath();
        var capeRect = new SKRect(1, 1, 71, 119);
        shape.AddRect(capeRect);

        using var patch = CreateCapePatch(cape);
        using var image = SKImage.FromBitmap(patch);
        var source = new SKRect(0, 0, patch.Width, patch.Height);
        var target = capeRect;
        using var basePaint = new SKPaint
        {
            IsAntialias = false,
            Color = EstimateCapeColor(patch)
        };
        canvas.DrawPath(shape, basePaint);
        DrawClippedCape(canvas, image, source, target, shape, mirrored);
        if (mirrored)
        {
            using var shade = new SKPaint
            {
                IsAntialias = true,
                Color = new SKColor(0, 0, 0, 42)
            };
            canvas.DrawPath(shape, shade);
        }
        using var outline = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2,
            Color = new SKColor(0x08, 0x0C, 0x10, 165)
        };
        canvas.DrawPath(shape, outline);
        SavePng(bitmap, destination);
    }

    private static SKBitmap CreateCapePatch(SKBitmap cape)
    {
        var sourceWidth = Math.Clamp(
            (int)Math.Round(cape.Width * 10.0 / 64.0), 1, cape.Width);
        var sourceHeight = Math.Clamp(
            (int)Math.Round(cape.Height * 16.0 / 32.0), 1, cape.Height);
        var patch = new SKBitmap(new SKImageInfo(
            sourceWidth, sourceHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (var y = 0; y < sourceHeight; y++)
        {
            var first = -1;
            var last = -1;
            for (var x = 0; x < sourceWidth; x++)
            {
                if (cape.GetPixel(x, y).Alpha == 0) continue;
                if (first < 0) first = x;
                last = x;
            }
            if (first < 0)
            {
                for (var x = 0; x < sourceWidth; x++)
                    patch.SetPixel(x, y, SKColors.Transparent);
                continue;
            }
            var left = cape.GetPixel(first, y);
            var right = cape.GetPixel(last, y);
            for (var x = 0; x < sourceWidth; x++)
            {
                var color = cape.GetPixel(x, y);
                if (color.Alpha == 0) color = x < first ? left : x > last ? right : color;
                patch.SetPixel(x, y, color);
            }
        }
        return patch;
    }

    private static SKColor EstimateCapeColor(SKBitmap patch)
    {
        long red = 0, green = 0, blue = 0, count = 0;
        for (var y = 0; y < patch.Height; y++)
            for (var x = 0; x < patch.Width; x++)
            {
                var color = patch.GetPixel(x, y);
                if (color.Alpha == 0) continue;
                red += color.Red;
                green += color.Green;
                blue += color.Blue;
                count++;
            }
        return count == 0
            ? new SKColor(0x31, 0x3C, 0x49)
            : new SKColor((byte)(red / count), (byte)(green / count), (byte)(blue / count));
    }

    private static SKPath CreateWingPath(bool mirror)
    {
        var path = new SKPath();
        if (!mirror)
        {
            path.MoveTo(60, 31);
            path.CubicTo(43, 30, 21, 45, 11, 72);
            path.CubicTo(2, 98, 3, 142, 9, 171);
            path.CubicTo(20, 175, 33, 163, 42, 146);
            path.CubicTo(51, 121, 58, 73, 64, 45);
            path.Close();
        }
        else
        {
            path.MoveTo(68, 31);
            path.CubicTo(85, 30, 107, 45, 117, 72);
            path.CubicTo(126, 98, 125, 142, 119, 171);
            path.CubicTo(108, 175, 95, 163, 86, 146);
            path.CubicTo(77, 121, 70, 73, 64, 45);
            path.Close();
        }
        return path;
    }

    private static SKPath CreateInnerWingPath(bool mirror)
    {
        var path = new SKPath();
        if (!mirror)
        {
            path.MoveTo(56, 51);
            path.CubicTo(43, 61, 30, 85, 22, 112);
            path.CubicTo(17, 130, 15, 148, 17, 158);
            path.CubicTo(26, 152, 34, 138, 39, 122);
            path.CubicTo(46, 98, 53, 72, 60, 58);
            path.Close();
        }
        else
        {
            path.MoveTo(72, 51);
            path.CubicTo(85, 61, 98, 85, 106, 112);
            path.CubicTo(111, 130, 113, 148, 111, 158);
            path.CubicTo(102, 152, 94, 138, 89, 122);
            path.CubicTo(82, 98, 75, 72, 68, 58);
            path.Close();
        }
        return path;
    }

    private static void DrawElytraConnector(SKCanvas canvas)
    {
        using var bodyPaint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(0x18, 0x23, 0x2A, 235)
        };
        canvas.DrawRoundRect(new SKRect(57, 40, 71, 86), 7, 7, bodyPaint);
        using var shinePaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            Color = new SKColor(0x8A, 0xA5, 0x99, 95)
        };
        canvas.DrawLine(64, 47, 64, 79, shinePaint);
    }

    private static void DrawClippedCape(
        SKCanvas canvas,
        SKImage image,
        SKRect source,
        SKRect target,
        SKPath clip,
        bool mirror)
    {
        using var paint = new SKPaint { IsAntialias = true };
        canvas.Save();
        canvas.ClipPath(clip, SKClipOperation.Intersect, true);
        if (mirror) canvas.Scale(-1, 1, target.MidX, target.MidY);
        canvas.DrawImage(image, source, target, NearestSampling, paint);
        canvas.Restore();
    }

    private static void DrawElytraVeins(SKCanvas canvas)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            Color = new SKColor(0xE8, 0xF4, 0xEE, 90)
        };
        foreach (var offset in new[] { 0, 17, 34 })
        {
            using var left = new SKPath();
            left.MoveTo(53 - offset * 0.16f, 55 + offset);
            left.CubicTo(39 - offset * 0.32f, 79 + offset, 24 - offset * 0.25f,
                111 + offset, 13 - offset * 0.08f, 147 + offset);
            canvas.DrawPath(left, paint);
            using var right = new SKPath();
            right.MoveTo(75 + offset * 0.16f, 55 + offset);
            right.CubicTo(89 + offset * 0.32f, 79 + offset, 104 + offset * 0.25f,
                111 + offset, 115 + offset * 0.08f, 147 + offset);
            canvas.DrawPath(right, paint);
        }
    }

    private static void DrawGroundShadow(SKCanvas canvas)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(0x05, 0x0B, 0x0E, 46)
        };
        canvas.DrawOval(new SKRect(24, 169, 104, 181), paint);
    }

    private static void DrawRegion(
        SKCanvas canvas,
        SKImage image,
        int sourceX,
        int sourceY,
        int sourceWidth,
        int sourceHeight,
        int destinationX,
        int destinationY,
        int scale)
    {
        using var paint = new SKPaint { IsAntialias = false };
        canvas.DrawImage(
            image,
            new SKRect(sourceX, sourceY, sourceX + sourceWidth, sourceY + sourceHeight),
            new SKRect(destinationX, destinationY,
                destinationX + sourceWidth * scale, destinationY + sourceHeight * scale),
            NearestSampling,
            paint);
    }

    private static void SavePng(SKBitmap bitmap, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var temporary = destination + ".tmp";
        using (var stream = File.Create(temporary)) encoded.SaveTo(stream);
        File.Move(temporary, destination, true);
    }
}
