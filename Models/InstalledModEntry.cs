using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Kumo.Models;

public sealed class InstalledModEntry : INotifyPropertyChanged
{
    private string? _chineseName;
    private string? _coverImagePath;

    public InstalledModEntry(
        string fileName,
        long sizeBytes,
        bool isEnabled,
        string? modId,
        string? localName,
        string? coverImagePath)
    {
        FileName = fileName;
        SizeBytes = sizeBytes;
        IsEnabled = isEnabled;
        ModId = modId;
        LocalName = localName;
        _coverImagePath = coverImagePath;
        SearchQuery = FirstNonEmpty(localName, modId, CleanFileName(fileName));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string FileName { get; }
    public long SizeBytes { get; }
    public bool IsEnabled { get; }
    public string? ModId { get; }
    public string? LocalName { get; }
    public string SearchQuery { get; }
    public string SizeLabel => FormatSize(SizeBytes);
    public string StateLabel => IsEnabled ? "已启用" : "已禁用";
    public string PrimaryName => FirstNonEmpty(ChineseName, LocalName, SearchQuery, FileName);
    public string Initial => PrimaryName[..1].ToUpperInvariant();
    public string DetailLabel =>
        $"{FileName} · {SizeLabel} · {StateLabel}";

    public string? ChineseName
    {
        get => _chineseName;
        private set
        {
            if (_chineseName == value) return;
            _chineseName = value;
            Notify();
            Notify(nameof(PrimaryName));
            Notify(nameof(Initial));
        }
    }

    public string? CoverImagePath
    {
        get => _coverImagePath;
        private set
        {
            if (_coverImagePath == value) return;
            _coverImagePath = value;
            Notify();
        }
    }

    public void ApplyChineseMetadata(string? chineseName, string? coverImagePath)
    {
        if (!string.IsNullOrWhiteSpace(chineseName))
            ChineseName = chineseName.Trim();
        if (!string.IsNullOrWhiteSpace(coverImagePath))
            CoverImagePath = coverImagePath;
    }

    private static string CleanFileName(string fileName)
    {
        var name = fileName;
        if (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            name = name[..^".disabled".Length];
        if (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            name = name[..^".jar".Length];
        return name.Replace('_', ' ').Trim();
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "Mod";

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / 1024d / 1024 / 1024:0.0} GB";
        if (bytes >= 1024L * 1024)
            return $"{bytes / 1024d / 1024:0.0} MB";
        return $"{Math.Max(1, bytes / 1024d):0.0} KB";
    }

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
