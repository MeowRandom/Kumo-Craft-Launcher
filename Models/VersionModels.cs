using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Kumo.Models;

public enum ModLoader
{
    Vanilla,
    Fabric,
    Forge,
    NeoForge,
    Quilt,
    OptiFine
}

public sealed class RemoteVersion : INotifyPropertyChanged
{
    private bool _isInstalled;
    private int _localVersionCount;

    public required string Id { get; init; }
    public required string Type { get; init; }
    public required string Url { get; init; }
    public DateTimeOffset ReleaseTime { get; init; }
    public bool IsLatestRelease { get; init; }

    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (_isInstalled == value) return;
            _isInstalled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActionLabel));
        }
    }

    public int LocalVersionCount
    {
        get => _localVersionCount;
        private set
        {
            var normalized = Math.Max(0, value);
            if (_localVersionCount == normalized) return;
            _localVersionCount = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(InstalledBadgeLabel));
        }
    }

    public void SetInstallationCount(int count)
    {
        LocalVersionCount = count;
        IsInstalled = count > 0;
    }

    [JsonIgnore]
    public string TypeLabel => Type switch
    {
        "release" => "正式版",
        "snapshot" => "快照版",
        "old_beta" => "远古 Beta",
        "old_alpha" => "远古 Alpha",
        _ => Type
    };

    [JsonIgnore]
    public string ReleaseDate => ReleaseTime.LocalDateTime.ToString("yyyy年M月d日");

    [JsonIgnore]
    public string ActionLabel => IsInstalled ? "添加版本" : "下载";

    [JsonIgnore]
    public string InstalledBadgeLabel => LocalVersionCount > 1
        ? $"已安装 {LocalVersionCount} 个版本"
        : "已安装";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class InstalledVersionInfo
{
    public required string Id { get; init; }
    public string BaseVersion { get; init; } = string.Empty;
    public ModLoader Loader { get; init; }
    public string? LoaderVersion { get; init; }
    public bool Ready { get; init; }
    public DateTimeOffset LastWriteTime { get; init; }
    public string? VanillaIconPath { get; init; }
    public string? ModpackName { get; init; }
    public string? DisplayName { get; init; }
    public string? FilterTypeLabel { get; init; }

    public string LoaderLabel => Loader switch
    {
        ModLoader.Fabric => "Fabric",
        ModLoader.Forge => "Forge",
        ModLoader.NeoForge => "NeoForge",
        ModLoader.Quilt => "Quilt",
        ModLoader.OptiFine => "OptiFine",
        _ => "原版"
    };

    public string DisplayLabel => !string.IsNullOrWhiteSpace(DisplayName)
        ? DisplayName
        : !string.IsNullOrWhiteSpace(ModpackName)
            ? $"{ModpackName} · {BaseVersion}"
            : Loader == ModLoader.Vanilla ? Id : $"{BaseVersion} · {LoaderLabel}";

    public string FilterDisplayLabel =>
        !string.IsNullOrWhiteSpace(DisplayName) &&
        string.IsNullOrWhiteSpace(FilterTypeLabel)
            ? $"{DisplayName} -{LoaderLabel} {BaseVersion}"
            : DisplayLabel;

    public string LoaderMark => Loader switch
    {
        ModLoader.Fabric => "F",
        ModLoader.Forge => "F",
        ModLoader.NeoForge => "N",
        ModLoader.Quilt => "Q",
        ModLoader.OptiFine => "O",
        _ => "V"
    };

    public bool HasLoaderIcon =>
        Loader is ModLoader.Vanilla or ModLoader.Fabric or ModLoader.Forge or
            ModLoader.NeoForge or ModLoader.Quilt or ModLoader.OptiFine;
    public bool SupportsMods => Loader is ModLoader.Fabric or ModLoader.Forge or
        ModLoader.NeoForge or ModLoader.Quilt;
    public string ReadyLabel => Ready ? "文件可用" : "需要检查";
    public string SourceLabel => !string.IsNullOrWhiteSpace(ModpackName)
        ? $"整合包版本 · {LoaderLabel}"
        : Loader switch
        {
            ModLoader.Vanilla => "Mojang 官方原版",
            ModLoader.OptiFine => $"基于 Minecraft {BaseVersion} · OptiFine",
            _ => $"基于 Minecraft {BaseVersion}"
        };
}

public sealed record VersionManifest(
    LatestVersions Latest,
    IReadOnlyList<ManifestVersion> Versions,
    string RawJson);

public sealed record LatestVersions(string Release, string Snapshot);

public sealed record ManifestVersion(
    string Id,
    string Type,
    string Url,
    DateTimeOffset ReleaseTime,
    string? Sha1);

public sealed record DownloadProgress(
    string Stage,
    int Completed,
    int Total,
    string CurrentFile,
    long BytesReceived = 0,
    long? TotalBytes = null,
    double BytesPerSecond = 0,
    TimeSpan? EstimatedRemaining = null,
    string? Route = null)
{
    public double Percent => TotalBytes is > 0
        ? Math.Clamp(BytesReceived * 100d / TotalBytes.Value, 0, 100)
        : Total <= 0 ? 0 : Math.Clamp(Completed * 100d / Total, 0, 100);
}
