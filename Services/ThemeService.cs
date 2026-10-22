using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Kumo.Models;
using Microsoft.Win32;

namespace Kumo.Services;

public static class ThemeService
{
    private static readonly Uri DarkThemeUri = new("Themes/DarkTheme.xaml", UriKind.Relative);
    private static readonly Uri LightThemeUri = new("Themes/LightTheme.xaml", UriKind.Relative);
    private static DispatcherTimer? _systemThemeTimer;
    private static bool? _lastResolvedDark;

    public static LauncherThemeMode CurrentMode { get; private set; } = LauncherThemeMode.Dark;
    public static bool IsDark { get; private set; } = true;
    public static event EventHandler? ThemeChanged;

    public static void Apply(LauncherThemeMode mode)
    {
        var application = Application.Current;
        if (application is null) return;

        CurrentMode = mode;
        var useDark = ResolveDark(mode);
        ReplaceThemeDictionary(application.Resources.MergedDictionaries, useDark);
        _lastResolvedDark = useDark;

        if (IsDark != useDark)
        {
            IsDark = useDark;
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }

        UpdateSystemThemeTimer();
    }

    public static bool ResolveDark(LauncherThemeMode mode) => mode switch
    {
        LauncherThemeMode.Dark => true,
        LauncherThemeMode.Light => false,
        _ => IsSystemLightTheme() == false
    };

    private static void ReplaceThemeDictionary(
        Collection<ResourceDictionary> dictionaries,
        bool useDark)
    {
        var desiredUri = useDark ? DarkThemeUri : LightThemeUri;
        var themeIndex = -1;
        for (var index = 0; index < dictionaries.Count; index++)
        {
            var source = dictionaries[index].Source?.OriginalString;
            if (source is null) continue;
            if (!source.EndsWith("DarkTheme.xaml", StringComparison.OrdinalIgnoreCase) &&
                !source.EndsWith("LightTheme.xaml", StringComparison.OrdinalIgnoreCase))
                continue;

            themeIndex = index;
            if (source.EndsWith(desiredUri.OriginalString, StringComparison.OrdinalIgnoreCase))
                return;
            break;
        }

        var replacement = new ResourceDictionary { Source = desiredUri };
        if (themeIndex >= 0)
            dictionaries[themeIndex] = replacement;
        else
            dictionaries.Insert(0, replacement);
    }

    private static void UpdateSystemThemeTimer()
    {
        if (CurrentMode != LauncherThemeMode.System)
        {
            _systemThemeTimer?.Stop();
            return;
        }

        if (_systemThemeTimer is not null) return;
        _systemThemeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _systemThemeTimer.Tick += (_, _) =>
        {
            if (CurrentMode != LauncherThemeMode.System) return;
            var useDark = ResolveDark(LauncherThemeMode.System);
            if (_lastResolvedDark == useDark) return;

            ReplaceThemeDictionary(Application.Current.Resources.MergedDictionaries, useDark);
            _lastResolvedDark = useDark;
            IsDark = useDark;
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        };
        _systemThemeTimer.Start();
    }

    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
