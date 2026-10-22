using System.Reflection;

namespace Kumo;

public static class AppInfo
{
    private static readonly string Version =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            .Split('+', 2)[0]
        ?? "0.0.0";

    public static string DisplayVersion
    {
        get
        {
            var separator = Version.IndexOf('-');
            if (separator < 0) return Version;
            var label = Version[(separator + 1)..];
            return label.Equals("beta", StringComparison.OrdinalIgnoreCase)
                ? $"{Version[..separator]} Beta"
                : $"{Version[..separator]} {label}";
        }
    }

    public static string CompactVersion
    {
        get
        {
            var numeric = Version.Split('-', 2)[0];
            var parts = numeric.Split('.');
            var shortNumeric = parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : numeric;
            return Version.Contains("-beta", StringComparison.OrdinalIgnoreCase)
                ? shortNumeric + "β"
                : shortNumeric;
        }
    }

    public const string Name = "Kumo";
    public const string ChineseName = "库莫";
    public static string ProductLabel => $"Kumo Launcher {DisplayVersion}";
}
