using System.IO;

namespace Kumo.Services;

public static class AppPaths
{
    public const string MetadataDirectoryName = ".kumo";

    public static string ExecutableDirectory { get; } = ResolveExecutableDirectory();

    public static string AppDataRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kumo");

    public static string ConfigPath { get; } = Path.Combine(AppDataRoot, "launcher.json");

    public static string PreviousDefaultGameDirectory { get; } =
        Path.Combine(AppDataRoot, "minecraft");

    public static string PortableGameDirectory { get; } =
        ResolvePortableGameDirectory(ExecutableDirectory);

    public static string DefaultGameDirectory { get; } = PortableGameDirectory;

    public static string GetMinecraftRootForSelection(string selectedDirectory)
    {
        var expanded = Environment.ExpandEnvironmentVariables(selectedDirectory);
        var fullPath = Path.GetFullPath(expanded);
        return string.Equals(
            Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar)),
            ".minecraft",
            StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : Path.Combine(fullPath, ".minecraft");
    }

    public static string GetInstanceMetadataDirectory(string instanceDirectory) =>
        Path.Combine(instanceDirectory, MetadataDirectoryName);

    internal static string ResolvePortableGameDirectory(string executableDirectory)
    {
        var fullPath = Path.GetFullPath(executableDirectory)
            .TrimEnd(Path.DirectorySeparatorChar);
        return Path.GetFileName(fullPath).Equals(
            ".minecraft",
            StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : Path.Combine(fullPath, ".minecraft");
    }

    private static string ResolveExecutableDirectory()
    {
        var processPath = Environment.ProcessPath;
        var directory = string.IsNullOrWhiteSpace(processPath)
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory;
        return Path.GetFullPath(directory);
    }
}
