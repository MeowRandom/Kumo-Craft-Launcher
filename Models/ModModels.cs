using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Kumo.Models;

public enum ModSource
{
    Modrinth,
    CurseForge
}

public enum ContentKind
{
    Mod,
    Modpack,
    DataPack,
    ResourcePack,
    Shader
}

public sealed record ModFileChoice(
    string VersionName,
    string FileName,
    string Url,
    string? Sha1,
    long? Size,
    string Channel,
    DateTimeOffset? PublishedAt,
    string SourceLabel,
    string? VersionId = null)
{
    public string DateLabel => PublishedAt is { } published
        ? published.LocalDateTime.ToString("yyyy-MM-dd")
        : "日期未知";

    public string SizeLabel => Size is > 0
        ? Size >= 1024L * 1024
            ? $"{Size / 1024d / 1024:0.0} MB"
            : $"{Size / 1024d:0} KB"
        : "大小未知";

    public string DetailLabel => $"{Channel} · {DateLabel} · {SizeLabel}";
}

public sealed record ModDependencyFile(
    string Name,
    string VersionName,
    string FileName,
    string Url,
    string? Sha1,
    long? Size,
    string SourceLabel)
{
    public string DetailLabel => $"{VersionName} · {FileName} · {SourceLabel}";
}

public sealed class ModSearchHit : INotifyPropertyChanged
{
    private string _status = "下载";
    private string _description = string.Empty;
    private bool _isInstalling;
    private bool _hasChineseDescription;
    private bool _needsChineseDescription;
    private string? _coverImagePath;

    [JsonPropertyName("project_id")]
    public string ProjectId { get; set; } = string.Empty;

    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description
    {
        get => _description;
        set
        {
            if (_description == value) return;
            _description = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Description)));
        }
    }

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("downloads")]
    public long Downloads { get; set; }

    [JsonPropertyName("follows")]
    public long Follows { get; set; }

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; set; }

    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = [];

    [JsonPropertyName("versions")]
    public List<string> Versions { get; set; } = [];

    [JsonPropertyName("gallery")]
    public List<string> Gallery { get; set; } = [];

    [JsonIgnore]
    public string DownloadsLabel => FormatCount(Downloads);

    [JsonIgnore]
    public string AuthorLabel => string.IsNullOrWhiteSpace(Author)
        ? SourceLabel
        : Author;

    [JsonIgnore]
    public string Initial => string.IsNullOrWhiteSpace(Title) ? "M" : Title[..1].ToUpperInvariant();

    [JsonIgnore]
    public string ProjectUrl { get; set; } = string.Empty;

    [JsonIgnore]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonIgnore]
    public ModSource Source { get; set; }

    [JsonIgnore]
    public ContentKind ContentKind { get; set; }

    [JsonIgnore]
    public string? CoverUrl { get; set; }

    [JsonIgnore]
    public string SourceLabel => Source == ModSource.CurseForge ? "CurseForge" : "Modrinth";

    [JsonIgnore]
    public string ContentKindLabel => ContentKind switch
    {
        ContentKind.Modpack => "整合包",
        ContentKind.DataPack => "数据包",
        ContentKind.ResourcePack => "资源包",
        ContentKind.Shader => "光影包",
        _ => "模组"
    };

    [JsonIgnore]
    public string PrimaryActionLabel => ContentKind == ContentKind.Modpack ? "安装" : "下载";

    [JsonIgnore]
    public string PrimaryActionProgressLabel =>
        ContentKind == ContentKind.Modpack ? "安装中…" : "下载中…";

    [JsonIgnore]
    public string InfoLabel => $"{DownloadsLabel} 次下载";

    [JsonIgnore]
    public string InfoCaption => HasChineseDescription ? "中文说明 · 应用内下载" : "社区下载";

    [JsonIgnore]
    public bool HasChineseDescription
    {
        get => _hasChineseDescription;
        set
        {
            if (_hasChineseDescription == value) return;
            _hasChineseDescription = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasChineseDescription)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InfoCaption)));
        }
    }

    [JsonIgnore]
    public bool NeedsChineseDescription
    {
        get => _needsChineseDescription;
        set
        {
            if (_needsChineseDescription == value) return;
            _needsChineseDescription = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NeedsChineseDescription)));
        }
    }

    [JsonIgnore]
    public string FallbackDescription { get; set; } = string.Empty;

    [JsonIgnore]
    public string FullDescription { get; set; } = string.Empty;

    [JsonIgnore]
    public string? McmodUrl { get; set; }

    [JsonIgnore]
    public string? ModrinthUrl { get; set; }

    [JsonIgnore]
    public string? CurseForgeUrl { get; set; }

    [JsonIgnore]
    public string? CoverImagePath
    {
        get => _coverImagePath;
        set
        {
            if (_coverImagePath == value) return;
            _coverImagePath = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverImagePath)));
        }
    }

    [JsonIgnore]
    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }
    }

    [JsonIgnore]
    public bool IsInstalling
    {
        get => _isInstalling;
        set
        {
            if (_isInstalling == value) return;
            _isInstalling = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInstalling)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static string FormatCount(long count) => count switch
    {
        >= 100_000_000 => $"{count / 100_000_000d:0.#}亿",
        >= 10_000 => $"{count / 10_000d:0.#}万",
        _ => count.ToString("N0")
    };
}

public sealed class ModVersion
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("project_id")]
    public string ProjectId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version_number")]
    public string VersionNumber { get; set; } = string.Empty;

    [JsonPropertyName("version_type")]
    public string VersionType { get; set; } = string.Empty;

    [JsonPropertyName("date_published")]
    public DateTimeOffset DatePublished { get; set; }

    [JsonPropertyName("game_versions")]
    public List<string> GameVersions { get; set; } = [];

    [JsonPropertyName("loaders")]
    public List<string> Loaders { get; set; } = [];

    [JsonPropertyName("files")]
    public List<ModFile> Files { get; set; } = [];

    [JsonPropertyName("dependencies")]
    public List<ModDependency> Dependencies { get; set; } = [];
}

public sealed class ModFile
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("primary")]
    public bool Primary { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("hashes")]
    public ModHashes Hashes { get; set; } = new();
}

public sealed class ModHashes
{
    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("sha512")]
    public string? Sha512 { get; set; }
}

public sealed class ModDependency
{
    [JsonPropertyName("version_id")]
    public string? VersionId { get; set; }

    [JsonPropertyName("project_id")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("file_name")]
    public string? FileName { get; set; }

    [JsonPropertyName("dependency_type")]
    public string? DependencyType { get; set; }
}

public sealed record ModSearchResult(int Offset, int Limit, int TotalHits, IReadOnlyList<ModSearchHit> Hits);

public sealed record ModInstallResult(int Installed, int Dependencies, IReadOnlyList<string> Files);

public sealed record ModSourceProbe(ModSource Source, TimeSpan? Latency, string? Error)
{
    public bool IsAvailable => Latency.HasValue && string.IsNullOrWhiteSpace(Error);
}

public sealed record ModSourceSelection(ModSource Source, TimeSpan? Latency, bool UsedFallback);

public sealed class McmodModInfo
{
    public string ClassId { get; set; } = string.Empty;
    public string ChineseTitle { get; set; } = string.Empty;
    public string EnglishTitle { get; set; } = string.Empty;
    public string DisplayTitle { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ProjectUrl { get; set; } = string.Empty;
    public string? CoverUrl { get; set; }
    public string? CurseForgeSlug { get; set; }
    public string? ModrinthSlug { get; set; }

    public string MatchText => string.Join(' ', ChineseTitle, EnglishTitle, DisplayTitle);
}
