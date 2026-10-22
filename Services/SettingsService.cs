using System.IO;
using System.Text.Json;
using System.Threading;
using Kumo.Models;

namespace Kumo.Services;

public sealed class SettingsService
{
    private static readonly string[] MinecraftDataEntries =
    [
        "assets", "libraries", "versions", "instances", "runtime", "natives", "cache", "meta",
        "mods", "saves", "resourcepacks", "shaderpacks", "config", "logs", "crash-reports",
        "launcher_profiles.json", "options.txt", "servers.dat"
    ];
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private readonly string _appDataRoot;
    private readonly string _configPath;
    private readonly string _defaultGameDirectory;
    private readonly bool _forceDefaultGameDirectory;

    public AppSettings Settings { get; private set; } = new();

    public SettingsService()
        : this(
            AppPaths.AppDataRoot,
            AppPaths.ConfigPath,
            AppPaths.DefaultGameDirectory,
            forceDefaultGameDirectory: true)
    {
    }

    public SettingsService(
        string appDataRoot,
        string configPath,
        string defaultGameDirectory,
        bool forceDefaultGameDirectory = false)
    {
        _appDataRoot = appDataRoot;
        _configPath = configPath;
        _defaultGameDirectory = defaultGameDirectory;
        _forceDefaultGameDirectory = forceDefaultGameDirectory;
    }

    public void Load()
    {
        Directory.CreateDirectory(_appDataRoot);
        if (File.Exists(_configPath))
        {
            try
            {
                Settings = JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(_configPath), Options) ?? new AppSettings();
            }
            catch (Exception ex)
            {
                LogService.Error("读取设置失败，已使用默认设置", ex);
                Settings = new AppSettings();
            }
        }

        var needsSave = false;
        Settings.DataDirectory =
            Environment.ExpandEnvironmentVariables(Settings.DataDirectory ?? "");
        if (_forceDefaultGameDirectory)
        {
            Settings.DataDirectory = _defaultGameDirectory;
            needsSave = true;
        }
        else if (string.IsNullOrWhiteSpace(Settings.DataDirectory))
        {
            Settings.DataDirectory = _defaultGameDirectory;
            needsSave = true;
        }
        else if (IsSamePath(Settings.DataDirectory, AppPaths.PreviousDefaultGameDirectory))
        {
            if (!Directory.Exists(_defaultGameDirectory) &&
                Directory.Exists(AppPaths.PreviousDefaultGameDirectory))
            {
                try
                {
                    Directory.Move(AppPaths.PreviousDefaultGameDirectory, _defaultGameDirectory);
                    LogService.Info($"游戏数据目录已迁移到 {_defaultGameDirectory}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogService.Warn($"旧游戏目录暂时无法迁移：{ex.Message}");
                }
            }

            Settings.DataDirectory = _defaultGameDirectory;
            needsSave = true;
        }
        else if (TryMigrateCustomDirectory(Settings.DataDirectory))
        {
            Settings.DataDirectory = Path.Combine(
                Path.GetFullPath(Settings.DataDirectory), ".minecraft");
            needsSave = true;
        }
        try
        {
            var minecraftDirectory =
                AppPaths.GetMinecraftRootForSelection(Settings.DataDirectory);
            if (!IsSamePath(Settings.DataDirectory, minecraftDirectory))
            {
                Settings.DataDirectory = minecraftDirectory;
                needsSave = true;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or
                                       PathTooLongException)
        {
            LogService.Warn($"数据目录无效，已恢复默认目录：{ex.Message}");
            Settings.DataDirectory = _defaultGameDirectory;
            needsSave = true;
        }
        MigrateLegacyInstancesDirectory(
            Settings.DataDirectory, Settings.VersionIsolation);
        if (Settings.JvmArguments is null)
        {
            Settings.JvmArguments = AppSettings.DefaultJvmArguments;
            needsSave = true;
        }
        if (Settings.GameArguments is null)
        {
            Settings.GameArguments = "";
            needsSave = true;
        }
        if (Settings.GlobalGameOptions is null)
        {
            Settings.GlobalGameOptions = new GlobalMinecraftOptions();
            needsSave = true;
        }
        if (Settings.GlobalGameOptions.Keybindings is null)
        {
            Settings.GlobalGameOptions.Keybindings =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            needsSave = true;
        }
        Directory.CreateDirectory(Settings.DataDirectory);
        if (needsSave) Save();
    }

    private static bool IsSamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryMigrateCustomDirectory(string dataDirectory)
    {
        try
        {
            var parent = Path.GetFullPath(dataDirectory);
            if (string.Equals(Path.GetFileName(parent.TrimEnd(Path.DirectorySeparatorChar)),
                    ".minecraft", StringComparison.OrdinalIgnoreCase))
                return false;
            var driveRoot = Path.GetPathRoot(parent)?.TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar), driveRoot,
                    StringComparison.OrdinalIgnoreCase))
                return false;

            var entries = MinecraftDataEntries
                .Select(name => (Name: name, Source: Path.Combine(parent, name)))
                .Where(entry => File.Exists(entry.Source) || Directory.Exists(entry.Source))
                .ToList();
            if (entries.Count == 0) return false;

            var destination = Path.Combine(parent, ".minecraft");
            var destinationFull = Path.GetFullPath(destination);
            var parentPrefix = parent.TrimEnd(Path.DirectorySeparatorChar) +
                               Path.DirectorySeparatorChar;
            if (!destinationFull.StartsWith(parentPrefix, StringComparison.OrdinalIgnoreCase))
                return false;
            if (entries.Any(entry =>
                    File.Exists(Path.Combine(destinationFull, entry.Name)) ||
                    Directory.Exists(Path.Combine(destinationFull, entry.Name))))
                return false;

            Directory.CreateDirectory(destinationFull);
            foreach (var entry in entries)
            {
                var target = Path.Combine(destinationFull, entry.Name);
                if (Directory.Exists(entry.Source))
                    Directory.Move(entry.Source, target);
                else
                    File.Move(entry.Source, target);
            }
            LogService.Info($"游戏数据目录已迁移到 {destinationFull}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       ArgumentException or NotSupportedException)
        {
            LogService.Warn($"无法把游戏数据迁移到 .minecraft 子目录：{ex.Message}");
            return false;
        }
    }

    private static void MigrateLegacyInstancesDirectory(
        string dataDirectory,
        bool versionIsolation)
    {
        var sourceRoot = Path.Combine(dataDirectory, "instances");
        if (!Directory.Exists(sourceRoot)) return;
        try
        {
            foreach (var source in Directory.EnumerateDirectories(sourceRoot).ToList())
            {
                var id = Path.GetFileName(source);
                var destination = versionIsolation
                    ? Path.Combine(dataDirectory, "versions", id)
                    : dataDirectory;
                if (!Directory.Exists(destination))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    Directory.Move(source, destination);
                }
                else
                {
                    MoveMissingDirectoryContents(source, destination);
                    if (!Directory.EnumerateFileSystemEntries(source).Any())
                        Directory.Delete(source);
                }
            }

            if (!Directory.EnumerateFileSystemEntries(sourceRoot).Any())
                Directory.Delete(sourceRoot);
            LogService.Info("已将旧 instances 数据迁移到标准 versions 目录");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warn($"迁移旧 instances 目录失败：{ex.Message}");
        }
    }

    private static void MoveMissingDirectoryContents(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source).ToList())
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            File.Move(file, target, true);
        }
        foreach (var directory in Directory.EnumerateDirectories(source).ToList())
        {
            var target = Path.Combine(destination, Path.GetFileName(directory));
            if (Directory.Exists(target))
                MoveMissingDirectoryContents(directory, target);
            else
                Directory.Move(directory, target);
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(_appDataRoot);
        var temp = _configPath + ".tmp";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(Settings, Options));
                File.Move(temp, _configPath, true);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(80);
            }
        }
    }
}
