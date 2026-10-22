using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kumo.Models;
using Kumo.Services;

namespace Kumo.Converters;

public sealed class LoaderIconConverter : IValueConverter
{
    private const string ResourcePrefix = "Kumo.Assets.Loaders.";
    private static readonly Dictionary<ModLoader, ImageSource> Cache = [];
    private static readonly Dictionary<string, ImageSource> FileCache = [];

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        GetIcon(value as InstalledVersionInfo);

    public static ImageSource? GetIcon(InstalledVersionInfo? version)
    {
        if (version is null) return null;
        if (version.Loader is ModLoader.Vanilla or ModLoader.OptiFine)
            return LoadFileIcon(version.VanillaIconPath) ??
                   LoadEmbeddedIcon(ModLoader.Vanilla, "vanilla");

        var loader = version.Loader;
        var fileName = loader switch
        {
            ModLoader.Fabric => "fabric",
            ModLoader.Forge => "forge",
            ModLoader.NeoForge => "neoforge",
            ModLoader.Quilt => "quilt",
            _ => null
        };
        if (fileName is null) return null;
        return LoadEmbeddedIcon(loader, fileName);
    }

    private static ImageSource? LoadEmbeddedIcon(ModLoader loader, string fileName)
    {
        if (Cache.TryGetValue(loader, out var cached)) return cached;
        using var stream = typeof(LoaderIconConverter).Assembly
            .GetManifestResourceStream(ResourcePrefix + fileName + ".png");
        if (stream is null) return null;

        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = stream;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        Cache[loader] = image;
        return image;
    }

    private static ImageSource? LoadFileIcon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        if (FileCache.TryGetValue(path, out var cached)) return cached;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.EndInit();
            image.Freeze();
            FileCache[path] = image;
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException or FileFormatException)
        {
            LogService.Warn($"无法解码原版草方块图标：{ex.Message}");
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
