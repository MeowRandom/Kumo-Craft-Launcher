using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Kumo.Models;

public enum AccountKind
{
    Microsoft,
    Offline
}

public enum OfflineSkinModel
{
    Steve,
    Alex
}

public sealed class AccountInfo : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string? _avatarImagePath;
    private OfflineSkinModel _offlineSkinModel = OfflineSkinModel.Steve;
    private bool _isActive;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Initial));
        }
    }

    public string MinecraftUuid { get; set; } = string.Empty;
    public AccountKind Kind { get; set; }
    public string? Xuid { get; set; }
    public OfflineSkinModel OfflineSkinModel
    {
        get => _offlineSkinModel;
        set
        {
            if (_offlineSkinModel == value) return;
            _offlineSkinModel = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(KindLabel));
        }
    }

    [JsonIgnore]
    public string? SkinUrl { get; set; }

    [JsonIgnore]
    public string? AccessToken { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? RefreshTokenProtected { get; set; }

    [JsonIgnore]
    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();

    [JsonIgnore]
    public string KindLabel => Kind == AccountKind.Microsoft
        ? "Microsoft 正版"
        : $"离线账户 · {OfflineSkinModel}";

    [JsonIgnore]
    public bool IsMicrosoft => Kind == AccountKind.Microsoft;

    [JsonIgnore]
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    public string? AvatarImagePath
    {
        get => _avatarImagePath;
        set
        {
            if (_avatarImagePath == value) return;
            _avatarImagePath = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
