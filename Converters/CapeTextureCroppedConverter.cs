using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Kumo.Converters;

public sealed class CapeTextureCroppedConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, BitmapSource> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        var key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
        return Cache.GetOrAdd(key, _ => CreateCrop(path));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;

    private static BitmapSource CreateCrop(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();

        var scaleX = bitmap.PixelWidth / 64.0;
        var scaleY = bitmap.PixelHeight / 32.0;
        var x = Math.Clamp((int)Math.Round(scaleX), 0, bitmap.PixelWidth - 1);
        var y = Math.Clamp((int)Math.Round(scaleY), 0, bitmap.PixelHeight - 1);
        var width = Math.Clamp((int)Math.Round(10 * scaleX), 1, bitmap.PixelWidth - x);
        var height = Math.Clamp((int)Math.Round(16 * scaleY), 1, bitmap.PixelHeight - y);
        var crop = new CroppedBitmap(bitmap, new System.Windows.Int32Rect(x, y, width, height));
        crop.Freeze();
        return crop;
    }
}
