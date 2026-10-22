using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Kumo.Models;

namespace Kumo.Services;

public static partial class GlobalGameOptionsService
{
    public static bool Apply(string gameDirectory, GlobalMinecraftOptions settings)
    {
        if (!settings.Enabled || string.IsNullOrWhiteSpace(gameDirectory)) return false;

        Directory.CreateDirectory(gameDirectory);
        var path = Path.Combine(gameDirectory, "options.txt");
        var options = new List<(string Key, string Value)>
        {
            ("lang", NormalizeLanguage(settings.Language)),
            ("renderDistance", Math.Clamp(settings.RenderDistance, 2, 32).ToString()),
            ("simulationDistance", Math.Clamp(settings.SimulationDistance, 5, 32).ToString()),
            ("maxFps", Math.Clamp(settings.MaxFps, 0, 260).ToString()),
            ("guiScale", Math.Clamp(settings.GuiScale, 0, 4).ToString()),
            ("fullscreen", settings.Fullscreen.ToString().ToLowerInvariant()),
            ("enableVsync", settings.EnableVsync.ToString().ToLowerInvariant()),
            ("pauseOnLostFocus", settings.PauseOnLostFocus.ToString().ToLowerInvariant())
        };
        if (settings.SyncKeybindings && settings.Keybindings is not null)
        {
            foreach (var binding in settings.Keybindings)
            {
                if (IsValidKeybinding(binding.Key, binding.Value))
                    options.Add((binding.Key, binding.Value));
            }
        }

        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in options)
            replacements[option.Key] = option.Value;
        var missing = replacements.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lines = File.Exists(path) ? File.ReadAllLines(path) : [];
        var output = new List<string>(lines.Length + options.Count);

        foreach (var sourceLine in lines)
        {
            var separator = sourceLine.IndexOf(':');
            if (separator <= 0)
            {
                output.Add(sourceLine);
                continue;
            }

            var key = sourceLine[..separator].Trim();
            if (!replacements.TryGetValue(key, out var value))
            {
                output.Add(sourceLine);
                continue;
            }

            output.Add($"{key}:{value}");
            missing.Remove(key);
        }

        foreach (var option in replacements)
        {
            if (missing.Contains(option.Key))
                output.Add($"{option.Key}:{option.Value}");
        }

        var temporary = path + ".tmp";
        File.WriteAllLines(temporary, output, new UTF8Encoding(false));
        File.Move(temporary, path, true);
        return true;
    }

    public static IReadOnlyDictionary<string, string> ReadKeybindings(string optionsPath)
    {
        var bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(optionsPath)) return bindings;
        foreach (var line in File.ReadLines(optionsPath))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (IsValidKeybinding(key, value)) bindings[key] = value;
        }
        return bindings;
    }

    private static bool IsValidKeybinding(string? key, string? value) =>
        key is not null && value is not null &&
        key.Length is > 4 and <= 128 &&
        value.Length <= 64 &&
        key.StartsWith("key_", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeLanguage(string? language)
    {
        var normalized = language?.Trim().ToLowerInvariant() ?? "";
        return LanguagePattern().IsMatch(normalized) ? normalized : "zh_cn";
    }

    [GeneratedRegex("^[a-z]{2,3}(?:_[a-z]{2,4})?$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguagePattern();
}
