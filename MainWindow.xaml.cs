using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kumo.Behaviors;
using Kumo.Converters;
using Kumo.Models;
using Kumo.Services;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;

namespace Kumo;

public partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool EnableWindow(IntPtr window, bool enable);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(
        int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window, int attribute, ref int value, int size);

    private readonly SettingsService _settingsService = new();
    private readonly ObservableCollection<InstalledVersionInfo> _installedVersions = [];
    private readonly ObservableCollection<InstalledVersionInfo> _modVersions = [];
    private readonly ObservableCollection<InstalledVersionInfo> _modpackVersionFilters = [];
    private readonly ObservableCollection<RemoteVersion> _remoteVersions = [];
    private readonly ObservableCollection<ModSearchHit> _modHits = [];
    private readonly ObservableCollection<AccountInfo> _accounts = [];
    private readonly ObservableCollection<LoaderVersionOption> _loaderVersions = [];
    private readonly Dictionary<string, ManifestVersion> _manifestById = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _pendingLogLines = new();
    private readonly Queue<string> _displayLogLines = new();
    private int _logFlushScheduled;
    private int _homePopupRepositionPending;
    private static readonly LoaderChoiceOption[] ConfirmLoaderChoices =
    [
        new(ModLoader.Vanilla, "仅安装原版"),
        new(ModLoader.OptiFine, "OptiFine"),
        new(ModLoader.Fabric, "Fabric"),
        new(ModLoader.Forge, "Forge"),
        new(ModLoader.NeoForge, "NeoForge"),
        new(ModLoader.Quilt, "Quilt")
    ];
    private static readonly LoaderChoiceOption[] ContentLoaderChoices =
    [
        new(ModLoader.Vanilla, "自动（全部加载器）"),
        new(ModLoader.Fabric, "Fabric"),
        new(ModLoader.Forge, "Forge"),
        new(ModLoader.NeoForge, "NeoForge"),
        new(ModLoader.Quilt, "Quilt")
    ];
    private static readonly PageKind[] AppreciationNavigationSequence =
    [
        PageKind.Home, PageKind.Home, PageKind.Home,
        PageKind.Mods, PageKind.Mods,
        PageKind.MyVersions, PageKind.MyVersions, PageKind.MyVersions,
        PageKind.MyVersions, PageKind.MyVersions
    ];
    private const string OptiFabricLoaderVersion = "0.15.3";
    private const int GwlStyle = -16;
    private const long WsMaximizeBox = 0x00010000L;
    private readonly Dictionary<Grid, OverlayWindowState> _overlayWindows = [];

    private HttpService _http = null!;
    private MinecraftService _minecraft = null!;
    private VersionProfileService _profiles = null!;
    private VersionInstaller _installer = null!;
    private JavaService _java = null!;
    private GameLauncher _launcher = null!;
    private MicrosoftAuthService _auth = null!;
    private MinecraftProfileService _minecraftProfile = null!;
    private MinecraftAppearancePreviewService _appearancePreviews = null!;
    private ModrinthService _modrinth = null!;
    private CurseForgeService _curseForge = null!;
    private McmodService _mcmod = null!;
    private ModSourceSpeedTestService _modSourceSpeedTest = null!;
    private ModCoverCacheService _coverCache = null!;
    private PlayerAvatarService _playerAvatars = null!;
    private ModLoaderCatalogService _loaderCatalog = null!;
    private ModpackService _modpacks = null!;
    private MinecraftArtworkService _minecraftArtwork = null!;

    private ICollectionView? _versionView;
    private CancellationTokenSource? _operationCts;
    private CancellationTokenSource? _modSearchCts;
    private CancellationTokenSource? _loaderCatalogCts;
    private CancellationTokenSource? _confirmLoaderCts;
    private CancellationTokenSource? _confirmOptiFabricCts;
    private CancellationTokenSource? _authCts;
    private CancellationTokenSource? _customizationCts;
    private CancellationTokenSource? _avatarCts;
    private CancellationTokenSource? _modDetailCts;
    private CancellationTokenSource? _loadedModsLookupCts;
    private CancellationTokenSource? _heroBackdropCts;
    private double _heroBackdropAspectRatio;
    private TaskCompletionSource<bool>? _installConfirmation;
    private TaskCompletionSource<ModInstallTarget?>? _modTargetSelection;
    private TaskCompletionSource<ModFileChoice?>? _modVersionSelection;
    private InstalledVersionInfo? _confirmedInstallVersion;
    private LoaderVersionOption? _confirmedLoaderOption;
    private LoaderVersionOption? _confirmedOptiFineOption;
    private RemoteModFile? _confirmOptiFabricFile;
    private RemoteModFile? _confirmedOptiFabricFile;
    private bool _confirmedInstallOptiFabric;
    private string? _confirmedDisplayName;
    private string? _confirmLoaderBaseVersion;
    private DeviceCodeSession? _deviceCodeSession;
    private Process? _runningGame;
    private bool _launchInProgress;
    private bool _launcherHiddenForGame;
    private AccountInfo? _customizationAccount;
    private MinecraftProfileSnapshot? _customizationProfile;
    private string? _pendingSkinPath;
    private string? _customizationSkinTexturePath;
    private IReadOnlyDictionary<string, string> _customizationCapeTextures =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private bool _customizationBusy;
    private bool _shutdownStarted;
    private bool _mainWindowClosing;
    private ModSearchHit? _detailModHit;
    private InstalledVersionInfo? _renamingVersion;
    private InstalledVersionInfo? _loadedModsVersion;
    private readonly ConcurrentDictionary<string, (string? ChineseName, string? CoverUrl)>
        _installedModLookupCache = new(StringComparer.OrdinalIgnoreCase);
    private string? _loaderTargetVersion;
    private bool _trackVersionStatus;
    private bool _initialized;
    private bool _syncingSelections;
    private readonly DispatcherTimer _settingsSaveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(450)
    };
    private RemoteVersion? _remoteVersionPressedItem;
    private bool _remoteVersionPressedWasSelected;
    private bool _remoteVersionPressedOnButton;
    private int _modOffset;
    private int _modTotal;
    private string? _modsLoadedKey;
    private string? _modsLoadingKey;
    private ModSource _activeModSource = ModSource.Modrinth;
    private ContentKind _activeContentKind = ContentKind.Mod;
    private string? _modpackVersionType;
    private ModLoader _selectedLoader = ModLoader.Fabric;
    private bool _modFilterCleared;
    private string? _loaderCatalogKey;
    private bool _sidebarCollapsed;
    private int _sidebarAnimationVersion;
    private PageKind? _currentPage;
    private int _pageTransitionVersion;
    private int _appreciationSequenceProgress;
    private bool _startupAnimationStarted;
    private const int ModPageSize = 20;

    public MainWindow()
    {
        LogService.Initialize(Path.Combine(AppPaths.AppDataRoot, "logs"));
        _settingsService.Load();
        ThemeService.Apply(Settings.ThemeMode);
        InitializeComponent();
        RootGrid.Opacity = 0;
        CustomizationPlayerModel.WheelScrolled += CustomizationPlayerModel_WheelScrolled;
        _settingsSaveTimer.Tick += (_, _) =>
        {
            _settingsSaveTimer.Stop();
            SaveSettingsFromUi();
        };
        ThemeService.ThemeChanged += ThemeService_ThemeChanged;
        LogService.LineWritten += OnLogLine;
        InitializeServices();
        ApplySettingsToUi();

        _versionView = CollectionViewSource.GetDefaultView(_remoteVersions);
        _versionView.Filter = FilterVersion;
        RemoteVersionList.ItemsSource = _versionView;
        HomeVersionCombo.ItemsSource = _installedVersions;
        InstalledVersionsSideCombo.ItemsSource = _installedVersions;
        GlobalGameKeybindingsSourceCombo.ItemsSource = _installedVersions;
        ModsVersionCombo.ItemsSource = _modVersions;
        ModsLoaderCombo.ItemsSource = ContentLoaderChoices;
        ModsLoaderCombo.SelectedIndex = 0;
        MyVersionsList.ItemsSource = _installedVersions;
        ModResultList.ItemsSource = _modHits;
        AccountsList.ItemsSource = _accounts;
        LoaderVersionCombo.ItemsSource = _loaderVersions;

        foreach (var account in _settingsService.Settings.Accounts) _accounts.Add(account);
        _initialized = true;
        UpdateContentKindLabels();
        RefreshInstalledVersions();
        UpdateAccountUi();
        _ = RefreshPlayerAvatarsAsync();
        UpdateSearchHints();
        SetPage(PageKind.Home);
        _ = EnsureModsLoadedAsync();
        LogService.Info("库莫 Kumo Launcher 已启动");
    }

    private AppSettings Settings => _settingsService.Settings;
    private AccountInfo? ActiveAccount =>
        _accounts.FirstOrDefault(x => x.Id == Settings.ActiveAccountId);

    private void InitializeServices()
    {
        Settings.DataDirectory = AppPaths.PortableGameDirectory;
        Directory.CreateDirectory(Settings.DataDirectory);
        _http = new HttpService();
        _minecraft = new MinecraftService(_http, Settings.DataDirectory);
        _profiles = new VersionProfileService(Settings.DataDirectory, _minecraft);
        _installer = new VersionInstaller(_http, _minecraft, _profiles, Settings.DataDirectory,
            () => Settings.DownloadRouteMode);
        _java = new JavaService(_http, Settings.DataDirectory);
        _launcher = new GameLauncher(Settings.DataDirectory, _profiles, _installer, _java);
        _auth = new MicrosoftAuthService();
        _minecraftProfile = new MinecraftProfileService(_http, Settings.DataDirectory);
        _appearancePreviews = new MinecraftAppearancePreviewService(_http, Settings.DataDirectory);
        _mcmod = new McmodService(_http, Settings.DataDirectory);
        _modrinth = new ModrinthService(_http, Settings.DataDirectory);
        _curseForge = new CurseForgeService(_http, _mcmod, Settings.DataDirectory);
        _modSourceSpeedTest = new ModSourceSpeedTestService(_http);
        _coverCache = new ModCoverCacheService(_http, Settings.DataDirectory);
        _playerAvatars = new PlayerAvatarService(_http, Settings.DataDirectory);
        _loaderCatalog = new ModLoaderCatalogService(_http);
        _modpacks = new ModpackService(_http, _curseForge, Settings.DataDirectory);
        _minecraftArtwork = new MinecraftArtworkService(_http, Settings.DataDirectory);
    }

    private void ApplySettingsToUi()
    {
        if (Settings.WindowWidth >= MinWidth) Width = Settings.WindowWidth;
        if (Settings.WindowHeight >= MinHeight) Height = Settings.WindowHeight;
        MemorySlider.Value = Math.Clamp(Settings.MemoryMb, (int)MemorySlider.Minimum,
            (int)MemorySlider.Maximum);
        MemoryValueText.Text = $"{MemorySlider.Value / 1024d:0.#} GB";
        DataDirectoryBox.Text = Settings.DataDirectory;
        DownloadRouteCombo.SelectedValue = Settings.DownloadRouteMode.ToString();
        JavaPathBox.Text = Settings.JavaPath ?? "";
        HideLauncherDuringGameCheck.IsChecked = Settings.HideLauncherDuringGame;
        VersionIsolationCheck.IsChecked = Settings.VersionIsolation;
        var globalGame = Settings.GlobalGameOptions;
        GlobalGameOptionsCheck.IsChecked = globalGame.Enabled;
        SelectComboTag(GlobalGameLanguageCombo, globalGame.Language, "zh_cn");
        SelectComboTag(GlobalGameRenderDistanceCombo, globalGame.RenderDistance.ToString(), "12");
        SelectComboTag(GlobalGameSimulationDistanceCombo, globalGame.SimulationDistance.ToString(), "12");
        SelectComboTag(GlobalGameMaxFpsCombo, globalGame.MaxFps.ToString(), "120");
        SelectComboTag(GlobalGameGuiScaleCombo, globalGame.GuiScale.ToString(), "0");
        GlobalGameFullscreenCheck.IsChecked = globalGame.Fullscreen;
        GlobalGameVsyncCheck.IsChecked = globalGame.EnableVsync;
        GlobalGamePauseCheck.IsChecked = globalGame.PauseOnLostFocus;
        GlobalGameSyncKeybindingsCheck.IsChecked =
            globalGame.SyncKeybindings && globalGame.Keybindings.Count > 0;
        UpdateGlobalGameKeybindingsStatus();
        GlobalGameOptionsPanel.IsEnabled = globalGame.Enabled;
        GlobalGameOptionsPanel.Opacity = globalGame.Enabled ? 1 : 0.55;
        JvmArgumentsBox.Text = Settings.JvmArguments ?? "";
        GameArgumentsBox.Text = Settings.GameArguments ?? "";
        ThemeDarkOption.IsChecked = Settings.ThemeMode == LauncherThemeMode.Dark;
        ThemeLightOption.IsChecked = Settings.ThemeMode == LauncherThemeMode.Light;
        ThemeSystemOption.IsChecked = Settings.ThemeMode == LauncherThemeMode.System;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_startupAnimationStarted)
        {
            _startupAnimationStarted = true;
            RootGrid.Opacity = 1;
            RootGrid.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop
                });
        }

        try
        {
            await _installer.NormalizeLoaderVersionsAsync(CancellationToken.None);
            RefreshInstalledVersions();
        }
        catch (Exception ex)
        {
            LogService.Warn($"整理加载器版本失败：{ex.Message}");
        }

        try
        {
            await LoadManifestAsync();
        }
        catch (Exception ex)
        {
            LogService.Error("加载版本清单失败", ex);
            GlobalStatusText.Text = "版本清单加载失败，可稍后重试";
        }
    }

    private void Window_DragEnter(object sender, DragEventArgs e) =>
        UpdateModpackDragState(e);

    private void Window_DragOver(object sender, DragEventArgs e) =>
        UpdateModpackDragState(e);

    private void Window_DragLeave(object sender, DragEventArgs e) =>
        ModpackDropOverlay.Visibility = Visibility.Collapsed;

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        ModpackDropOverlay.Visibility = Visibility.Collapsed;
        if (GetDroppedModpackPath(e) is not { } path) return;
        e.Handled = true;
        await InstallModpackAsync(path);
    }

    private void UpdateModpackDragState(DragEventArgs e)
    {
        var accepted = GetDroppedModpackPath(e) is not null;
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        ModpackDropOverlay.Visibility = accepted ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private static string? GetDroppedModpackPath(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] files)
            return null;
        return files.FirstOrDefault(path =>
        {
            var extension = Path.GetExtension(path);
            return File.Exists(path) &&
                   (extension.Equals(".mrpack", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".zip", StringComparison.OrdinalIgnoreCase));
        });
    }

    private async Task<bool> InstallModpackAsync(string archivePath)
    {
        if (_operationCts is not null)
        {
            GlobalStatusText.Text = "当前已有任务正在进行，请完成后再安装整合包。";
            return false;
        }

        ModpackInstallPlan plan;
        try
        {
            GlobalStatusText.Text = "正在读取整合包…";
            plan = await _modpacks.InspectAsync(archivePath, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ShowError("无法读取整合包", ex);
            return false;
        }

        var loaderDescription = plan.Loader == ModLoader.Vanilla
            ? "原版"
            : $"{LoaderName(plan.Loader)} {plan.LoaderVersion}";
        var confirmed = await ConfirmInstallAsync(
            "安装整合包",
            "启动器将安装所需原版和 Mod 加载器，创建独立游戏版本，并下载整合包中的 Mod 与配置。可以自定义版本名称。",
            $"{plan.Name} {plan.Version} · Minecraft {plan.MinecraftVersion} · {loaderDescription}",
            "确认安装",
            allowDisplayName: true);
        if (!confirmed) return false;
        var requestedInstanceName = _confirmedDisplayName;
        _confirmedDisplayName = null;
        string requestedInstanceId;
        try
        {
            requestedInstanceId = ResolveModpackInstanceId(
                plan, requestedInstanceName);
            if (_profiles.InstanceIdExists(requestedInstanceId))
                throw new InvalidOperationException(
                    $"版本名称“{requestedInstanceId}”已存在，请换一个名称。");
        }
        catch (Exception ex)
        {
            ShowError("无法创建整合包版本", ex);
            return false;
        }

        var installedSuccessfully = false;
        await RunOperationAsync($"安装整合包 · {plan.Name}", async token =>
        {
            var progress = new Progress<DownloadProgress>(UpdateDownloadProgress);
            var existingVersionIds = _profiles.GetInstanceIds();
            if (!_manifestById.ContainsKey(plan.MinecraftVersion))
                await LoadManifestAsync();
            if (!_minecraft.IsVersionInstalled(plan.MinecraftVersion))
            {
                if (!_manifestById.TryGetValue(plan.MinecraftVersion, out var official))
                    throw new InvalidOperationException(
                        $"找不到 Minecraft {plan.MinecraftVersion} 的官方版本。");
                await _installer.InstallOfficialAsync(
                    official, progress, token,
                    asDependency: true);
            }

            InstalledVersionInfo? loaderInstance = null;
            if (plan.Loader != ModLoader.Vanilla)
            {
                var options = await _loaderCatalog.GetVersionsAsync(
                    plan.Loader, plan.MinecraftVersion, token);
                var requested = NormalizeLoaderVersion(plan.Loader, plan.LoaderVersion);
                var option = options.FirstOrDefault(item =>
                                 string.Equals(item.Version, requested, StringComparison.OrdinalIgnoreCase))
                             ?? throw new InvalidOperationException(
                                 $"找不到 {LoaderName(plan.Loader)} {plan.LoaderVersion} 的安装文件。");
                loaderInstance = await InstallLoaderOptionAsync(
                    plan.MinecraftVersion, option, progress, token);
            }

            var instanceId = requestedInstanceId;
            var source = loaderInstance ??
                         _profiles.GetInstalledVersions().FirstOrDefault(item =>
                             item.Id.Equals(plan.MinecraftVersion, StringComparison.OrdinalIgnoreCase))
                         ?? new InstalledVersionInfo
                         {
                             Id = plan.MinecraftVersion,
                             BaseVersion = plan.MinecraftVersion,
                             Loader = ModLoader.Vanilla,
                             Ready = true
                         };
            await _profiles.MaterializeInstanceAsync(
                source,
                instanceId,
                existingVersionIds.Contains(source.Id),
                token);

            var profile = await _profiles.LoadLocalAsync(instanceId, token);
            profile["id"] = instanceId;
            VersionMetadata.Set(
                profile, VersionMetadata.BaseVersion, plan.MinecraftVersion);
            profile.Remove("inheritsFrom");
            VersionMetadata.Set(profile, VersionMetadata.Modpack, new JsonObject
            {
                ["name"] = plan.Name,
                ["version"] = plan.Version,
                ["source"] = plan.Source
            });
            if (!string.IsNullOrWhiteSpace(requestedInstanceName))
                VersionMetadata.Set(
                    profile, VersionMetadata.DisplayName, requestedInstanceId);

            await _modpacks.InstallContentsAsync(
                plan, instanceId, progress, token, GetVersionGameDirectory(instanceId));
            _profiles.SaveLocal(instanceId, profile.ToJsonString());
            RefreshInstalledVersions();
            var installed = _installedVersions.FirstOrDefault(item => item.Id == instanceId);
            if (installed is not null)
            {
                HomeVersionCombo.SelectedItem = installed;
                Settings.LastVersionId = installed.Id;
                PersistConfig();
            }
            GlobalStatusText.Text = $"{plan.Name} {plan.Version} 安装完成";
            LogService.Info($"整合包 {plan.Name} {plan.Version} 已安装为 {instanceId}");
            installedSuccessfully = true;
        });
        return installedSuccessfully;
    }

    internal static string ResolveModpackInstanceId(
        ModpackInstallPlan plan,
        string? requestedName)
    {
        if (string.IsNullOrWhiteSpace(requestedName))
            return ModpackService.CreateInstanceId(plan);
        if (!VersionProfileService.TryNormalizeInstanceId(
                requestedName, out var instanceId, out var error) ||
            instanceId.Length == 0)
            throw new InvalidOperationException(error);
        return instanceId;
    }

    private static string? NormalizeLoaderVersion(ModLoader loader, string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var prefixes = loader switch
        {
            ModLoader.Fabric => new[] { "fabric-loader-", "fabric-" },
            ModLoader.Quilt => new[] { "quilt-loader-", "quilt-" },
            ModLoader.Forge => new[] { "forge-" },
            ModLoader.NeoForge => new[] { "neoforge-" },
            _ => []
        };
        foreach (var prefix in prefixes)
        {
            if (version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return version[prefix.Length..];
        }
        return version;
    }

    private void ShowOverlayWindow(
        Grid overlay,
        string title,
        Action? cancelAction = null)
    {
        if (_overlayWindows.TryGetValue(overlay, out var existing))
        {
            existing.Window.Activate();
            return;
        }
        if (overlay.Children.Count != 1 || overlay.Children[0] is not FrameworkElement content)
            return;

        overlay.Children.Remove(content);
        overlay.Visibility = Visibility.Collapsed;
        var host = new Grid();
        host.Children.Add(content);
        var useTransparency = !ReferenceEquals(overlay, ProfileCustomizationOverlay);
        var window = new Window
        {
            Title = title,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = useTransparency,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = false,
            Content = host,
            UseLayoutRounding = true,
            Icon = Icon
        };
        window.MaxWidth = Math.Max(480, SystemParameters.WorkArea.Width - 60);
        window.MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height - 60);
        if (useTransparency)
            window.Background = Brushes.Transparent;
        else
            window.SetResourceReference(Window.BackgroundProperty, "BgBrush");

        var state = new OverlayWindowState(overlay, content, window, cancelAction);
        _overlayWindows[overlay] = state;
        window.Closing += (_, args) =>
        {
            if (state.AllowClose) return;
            args.Cancel = true;
            window.Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() =>
                {
                    if (state.CancelAction is not null)
                        state.CancelAction();
                    else
                        HideOverlayWindow(overlay);
                }));
        };
        window.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            window.Close();
        };
        window.SourceInitialized += (_, _) => ApplyRoundedWindowRegion(window);
        window.SizeChanged += (_, _) => ApplyRoundedWindowRegion(window);
        window.DpiChanged += (_, _) => ApplyRoundedWindowRegion(window);
        window.MouseLeftButtonDown += (_, args) =>
        {
            if (IsDialogInteractiveSource(args.OriginalSource as DependencyObject, content))
                return;
            window.DragMove();
            args.Handled = true;
        };
        window.Closed += (_, _) =>
        {
            _overlayWindows.Remove(overlay);
            host.Children.Remove(content);
            window.Content = null;
            overlay.Children.Clear();
            overlay.Children.Add(content);
            overlay.Visibility = Visibility.Collapsed;
            if (ReferenceEquals(overlay, ProfileCustomizationOverlay))
                ResetProfileCustomizationContent();
        };

        UpdateOwnerEnabledState();
        var fadeTarget = useTransparency ? window : window.Content as FrameworkElement;
        if (fadeTarget is not null) fadeTarget.Opacity = 0;
        window.Show();
        if (fadeTarget is not null)
        {
            fadeTarget.Opacity = 1;
            fadeTarget.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop
                });
        }
        window.Activate();
    }

    private static void CompleteOverlayClose(
        Grid overlay,
        OverlayWindowState state,
        Action prepareOwner,
        Func<Grid, bool> isOpen)
    {
        if (!isOpen(overlay)) return;
        prepareOwner();
        state.AllowClose = true;
        state.Window.Close();
    }

    private static void FadeOutOverlayWindow(
        Grid overlay,
        OverlayWindowState state,
        Action prepareOwner,
        Func<Grid, bool> isOpen)
    {
        var fadeTarget = state.Window.AllowsTransparency
            ? (DependencyObject)state.Window
            : (DependencyObject?)state.Window.Content;
        if (fadeTarget is not FrameworkElement visual)
        {
            CompleteOverlayClose(overlay, state, prepareOwner, isOpen);
            return;
        }
        visual.BeginAnimation(UIElement.OpacityProperty, null);
        visual.Opacity = 0;
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop
        };
        fadeOut.Completed += (_, _) =>
            CompleteOverlayClose(overlay, state, prepareOwner, isOpen);
        visual.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    private void HideOverlayWindow(Grid overlay)
    {
        if (_overlayWindows.TryGetValue(overlay, out var state))
        {
            if (state.IsClosing) return;
            state.IsClosing = true;
            FadeOutOverlayWindow(
                overlay,
                state,
                () => PrepareOwnerForOverlayClose(overlay),
                IsOverlayOpen);
            return;
        }
        overlay.Visibility = Visibility.Collapsed;
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var cornerPreference = 2;
        DwmSetWindowAttribute(handle, 33, ref cornerPreference, sizeof(int));
        var style = GetWindowLongPtr(handle, GwlStyle).ToInt64();
        SetWindowLongPtr(handle, GwlStyle, new IntPtr(style & ~WsMaximizeBox));
    }

    private static void ApplyRoundedWindowRegion(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || window.ActualWidth <= 0 || window.ActualHeight <= 0)
            return;
        var cornerPreference = 2;
        if (DwmSetWindowAttribute(handle, 33, ref cornerPreference,
                sizeof(int)) == 0 && !window.AllowsTransparency)
            return;
        if (window.AllowsTransparency) return;
        var scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * scale));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * scale));
        var diameter = Math.Max(8, (int)Math.Round(24 * scale * 2));
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter, diameter);
        if (region == IntPtr.Zero) return;
        if (SetWindowRgn(handle, region, true) == 0)
            DeleteObject(region);
    }

    private static bool IsDialogInteractiveSource(
        DependencyObject? source,
        DependencyObject root)
    {
        while (source is not null && source != root)
        {
            if (source is ButtonBase or TextBoxBase or PasswordBox or ComboBox or
                ScrollViewer or ListBox or ListView or ScrollBar or Slider)
                return true;
            source = source is Visual
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private bool IsOverlayOpen(Grid overlay) => _overlayWindows.ContainsKey(overlay);

    private void UpdateOwnerEnabledState()
    {
        if (_shutdownStarted) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
            EnableWindow(handle, _overlayWindows.Count == 0);
    }

    private void PrepareOwnerForOverlayClose(Grid closingOverlay)
    {
        if (_shutdownStarted || _mainWindowClosing) return;
        var next = _overlayWindows
            .Where(pair => !ReferenceEquals(pair.Key, closingOverlay))
            .Select(pair => pair.Value.Window)
            .LastOrDefault();
        if (next is not null)
        {
            next.Activate();
            var nextHandle = new WindowInteropHelper(next).Handle;
            if (nextHandle != IntPtr.Zero) SetForegroundWindow(nextHandle);
            return;
        }
        var handle = new WindowInteropHelper(this).Handle;
        const uint setWindowPositionFlags =
            0x0001 | 0x0002 | 0x0010; // NOSIZE | NOMOVE | NOACTIVATE
        if (handle != IntPtr.Zero)
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, setWindowPositionFlags);
        EnableWindow(handle, true);
        if (!IsVisible) return;
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        if (handle != IntPtr.Zero) SetForegroundWindow(handle);
    }

    private void CloseAllOverlayWindows()
    {
        foreach (var state in _overlayWindows.Values.ToList())
        {
            state.AllowClose = true;
            state.Window.Close();
        }
        _overlayWindows.Clear();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _mainWindowClosing = true;
        CloseAllOverlayWindows();
        CustomizationPlayerModel.DisposeRenderer();
        _authCts?.Cancel();
        _customizationCts?.Cancel();
        _avatarCts?.Cancel();
        _operationCts?.Cancel();
        _modSearchCts?.Cancel();
        _modDetailCts?.Cancel();
        _heroBackdropCts?.Cancel();
        _loadedModsLookupCts?.Cancel();
        _loaderCatalogCts?.Cancel();
        _confirmLoaderCts?.Cancel();
        _confirmOptiFabricCts?.Cancel();
        FlushPendingSettingsSave();
        ResolveInstallConfirmation(false);
        ResolveModTargetSelection(null);
        ResolveModVersionSelection(null);
        _renamingVersion = null;
        SaveWindowSize();
        PersistConfig();
        LogService.LineWritten -= OnLogLine;
        ThemeService.ThemeChanged -= ThemeService_ThemeChanged;
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        LogService.Info("启动器窗口已关闭，正在退出进程");
        Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
    }

    private void SaveWindowSize()
    {
        if (WindowState == WindowState.Normal)
        {
            Settings.WindowWidth = (int)Width;
            Settings.WindowHeight = (int)Height;
        }
    }

    private async Task LoadManifestAsync(bool force = false)
    {
        GlobalStatusText.Text = "正在连接 Mojang 版本服务…";
        var manifest = await _minecraft.GetManifestAsync(force);
        _manifestById.Clear();
        foreach (var item in manifest.Versions) _manifestById[item.Id] = item;

        var selectedId = (RemoteVersionList.SelectedItem as RemoteVersion)?.Id;
        var installedFamilyCounts = BuildVersionFamilyCounts(_profiles.GetInstalledVersions());
        _remoteVersions.Clear();
        foreach (var item in manifest.Versions)
        {
            var remote = new RemoteVersion
            {
                Id = item.Id,
                Type = item.Type,
                Url = item.Url,
                ReleaseTime = item.ReleaseTime,
                IsLatestRelease = item.Id == manifest.Latest.Release
            };
            remote.SetInstallationCount(
                installedFamilyCounts.GetValueOrDefault(item.Id));
            _remoteVersions.Add(remote);
        }
        RebuildModpackVersionFilters();

        if (selectedId is not null)
            RemoteVersionList.SelectedItem = _remoteVersions.FirstOrDefault(x => x.Id == selectedId);
        _versionView?.Refresh();
        UpdateVersionResultCount();
        LatestVersionText.Text = $"最新正式版 {manifest.Latest.Release}";
        GlobalStatusText.Text = "版本清单已更新";
        LogService.Info($"已载入 {manifest.Versions.Count} 个 Minecraft 版本");

        if (_initialized)
        {
            UpdateModAvailability();
            if (_currentPage == PageKind.Mods)
                _ = EnsureModsLoadedAsync();
        }
    }

    private bool FilterVersion(object item)
    {
        if (item is not RemoteVersion version) return false;
        var query = VersionSearchBox?.Text?.Trim() ?? "";
        if (query.Length > 0 && !version.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            return false;
        if (FilterRelease?.IsChecked == true) return version.Type == "release";
        if (FilterSnapshot?.IsChecked == true) return version.Type == "snapshot";
        return true;
    }

    private void VersionFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _versionView?.Refresh();
        UpdateVersionResultCount();
    }

    private void VersionSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSearchHints();
        if (!_initialized) return;
        _versionView?.Refresh();
        UpdateVersionResultCount();
    }

    private void UpdateVersionResultCount()
    {
        if (_versionView is null) return;
        VersionResultCountText.Text = $"{_versionView.Cast<object>().Count()} 个结果";
    }

    private void RefreshInstalledVersions()
    {
        _syncingSelections = true;
        var homeSelection = (HomeVersionCombo.SelectedItem as InstalledVersionInfo)?.Id;
        var sideSelection = (InstalledVersionsSideCombo.SelectedItem as InstalledVersionInfo)?.Id;
        var modSelectionInfo = ModsVersionCombo.SelectedItem as InstalledVersionInfo;
        var modSelection = modSelectionInfo?.Id;
        var modBaseSelection = modSelectionInfo?.BaseVersion;
        var keybindingSelection =
            (GlobalGameKeybindingsSourceCombo.SelectedItem as InstalledVersionInfo)?.Id;

        _installedVersions.Clear();
        foreach (var item in _profiles.GetInstalledVersions()) _installedVersions.Add(item);

        _modVersions.Clear();
        foreach (var item in _installedVersions.Where(x => x.SupportsMods))
            _modVersions.Add(item);

        HomeVersionCombo.SelectedItem =
            _installedVersions.FirstOrDefault(x => x.Id == (homeSelection ?? Settings.LastVersionId))
            ?? _installedVersions.FirstOrDefault();
        InstalledVersionsSideCombo.SelectedItem =
            _installedVersions.FirstOrDefault(x => x.Id == sideSelection) ?? HomeVersionCombo.SelectedItem;
        ConfigureModVersionFilterSource(modSelection, modBaseSelection);
        GlobalGameKeybindingsSourceCombo.SelectedItem =
            _installedVersions.FirstOrDefault(x => x.Id == keybindingSelection)
            ?? HomeVersionCombo.SelectedItem;

        var installedFamilyCounts = BuildVersionFamilyCounts(_installedVersions);
        foreach (var version in _remoteVersions)
            version.SetInstallationCount(installedFamilyCounts.GetValueOrDefault(version.Id));
        InstalledCountText.Text = _installedVersions.Count.ToString();
        ModdedCountText.Text = _modVersions.Count.ToString();
        UpdateLoaderTargetFromSelection();
        UpdateModAvailability();
        UpdateMyVersionsView();
        UpdateHomeVersionInfo();
        UpdateLaunchState();
        _syncingSelections = false;
    }

    private void HomeVersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelections || !_initialized) return;
        if (HomeVersionCombo.SelectedItem is InstalledVersionInfo selected)
        {
            Settings.LastVersionId = selected.Id;
            PersistConfig();
        }
        UpdateHomeVersionInfo();
        UpdateLaunchState();
    }

    private void HomePage_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 || HomeVersionCombo?.IsDropDownOpen != true)
            return;
        if (Interlocked.Exchange(ref _homePopupRepositionPending, 1) != 0)
            return;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(RepositionHomeVersionPopup));
    }

    private void RepositionHomeVersionPopup()
    {
        Interlocked.Exchange(ref _homePopupRepositionPending, 0);
        if (HomeVersionCombo?.IsDropDownOpen != true) return;
        HomeVersionCombo.ApplyTemplate();
        if (HomeVersionCombo.Template.FindName("PART_Popup", HomeVersionCombo) is not Popup popup)
            return;

        // A tiny offset change asks WPF to recalculate the popup against its moved target.
        popup.VerticalOffset = Math.Abs(popup.VerticalOffset) < 0.005 ? 0.01 : 0;
    }

    private void UpdateHomeVersionInfo()
    {
        if (HomeVersionCombo.SelectedItem is not InstalledVersionInfo selected)
        {
            HomeLoaderText.Text = "尚未安装";
            HomeVersionMeta.Text = "请先在版本管理中下载 Minecraft";
            HomeHeroVersionTitle.Text = "还没有游戏版本";
            HomeHeroVersionSubtitle.Text = "下载一个版本开始游戏";
            HomeHeroVersionIcon.Content = null;
            HomeHeroVersionIcon.Visibility = Visibility.Collapsed;
            UpdateHomeHeroImage(null);
            return;
        }
        HomeLoaderText.Text = selected.LoaderLabel;
        HomeVersionMeta.Text = selected.Loader == ModLoader.Vanilla
            ? $"{selected.Id} · 原版"
            : $"Minecraft {selected.BaseVersion} · {selected.LoaderLabel}";
        HomeHeroVersionTitle.Text = selected.DisplayLabel;
        HomeHeroVersionSubtitle.Text = selected.SourceLabel;
        HomeHeroVersionIcon.Content = selected;
        HomeHeroVersionIcon.Visibility = Visibility.Visible;
        UpdateHomeHeroImage(selected);
    }

    private void InstalledVersionsSideCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelections || !_initialized) return;
        UpdateLoaderTargetFromSelection();
    }

    private void RemoteVersionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateLoaderTargetFromSelection();
        if (_operationCts is null) UpdateSelectedVersionStatus();
    }

    private void UpdateSelectedVersionStatus()
    {
        if (RemoteVersionList.SelectedItem is not RemoteVersion version)
        {
            VersionStatusCard.Visibility = Visibility.Collapsed;
            return;
        }

        VersionStatusCard.Visibility = Visibility.Visible;
        VersionProgressBar.IsIndeterminate = false;
        VersionProgressBar.Value = version.IsInstalled ? 100 : 0;
        VersionTaskStatus.Text = version.LocalVersionCount switch
        {
            > 1 => $"已安装 {version.LocalVersionCount} 个版本",
            > 0 => "下载完成",
            _ => "可下载"
        };
        VersionProgressDetail.Text = version.IsInstalled
            ? $"Minecraft {version.Id} 已有本地版本，可继续添加加载器或独立原版实例"
            : $"Minecraft {version.Id} · {version.TypeLabel}";
    }

    private static Dictionary<string, int> BuildVersionFamilyCounts(
        IEnumerable<InstalledVersionInfo> versions)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var version in versions)
        {
            AddVersionFamily(counts, version.Id);
            if (!string.IsNullOrWhiteSpace(version.BaseVersion) &&
                !string.Equals(version.BaseVersion, version.Id, StringComparison.OrdinalIgnoreCase))
                AddVersionFamily(counts, version.BaseVersion);
        }
        return counts;
    }

    private static void AddVersionFamily(
        IDictionary<string, int> counts,
        string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return;
        counts[versionId] = counts.TryGetValue(versionId, out var count) ? count + 1 : 1;
    }

    private void RemoteVersionList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _remoteVersionPressedItem = null;
        _remoteVersionPressedWasSelected = false;
        _remoteVersionPressedOnButton = false;
        if (e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(RemoteVersionList, source) is not ListBoxItem container)
            return;
        _remoteVersionPressedItem = container.DataContext as RemoteVersion;
        _remoteVersionPressedWasSelected = container.IsSelected;
        _remoteVersionPressedOnButton = FindVisualAncestor<Button>(source) is not null;
    }

    private void RemoteVersionList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var pressedItem = _remoteVersionPressedItem;
        var wasSelected = _remoteVersionPressedWasSelected;
        var pressedOnButton = _remoteVersionPressedOnButton;
        _remoteVersionPressedItem = null;
        _remoteVersionPressedWasSelected = false;
        _remoteVersionPressedOnButton = false;
        if (!wasSelected || pressedOnButton || pressedItem is null ||
            e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(RemoteVersionList, source) is not ListBoxItem container ||
            !ReferenceEquals(container.DataContext, pressedItem))
            return;
        RemoteVersionList.SelectedItem = null;
        e.Handled = true;
    }

    private static T? FindVisualAncestor<T>(DependencyObject source) where T : DependencyObject
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is T match) return match;
            current = current switch
            {
                Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(current),
                FrameworkContentElement contentElement => contentElement.Parent,
                _ => LogicalTreeHelper.GetParent(current)
            };
        }
        return null;
    }

    private static bool IsDescendantOf(DependencyObject? source, DependencyObject ancestor)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor)) return true;
            current = current switch
            {
                Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(current),
                FrameworkContentElement contentElement => contentElement.Parent,
                _ => LogicalTreeHelper.GetParent(current)
            };
        }
        return false;
    }

    private void UpdateLoaderTargetFromSelection()
    {
        string? target = null;
        if (RemoteVersionList.SelectedItem is RemoteVersion remote) target = remote.Id;
        else if (InstalledVersionsSideCombo.SelectedItem is InstalledVersionInfo installed)
            target = installed.BaseVersion;
        _loaderTargetVersion = target;
        LoaderTargetText.Text = target ?? "请从左侧选择版本";
        if (target is null)
        {
            _loaderCatalogCts?.Cancel();
            _loaderVersions.Clear();
            _loaderCatalogKey = null;
            LoaderVersionStatus.Text = "请选择目标版本";
        }
        else if (_initialized)
        {
            _ = RefreshLoaderVersionsAsync();
        }
        UpdateLoaderInstallState();
    }

    private void LoaderOption_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton option ||
            !Enum.TryParse<ModLoader>(option.Tag?.ToString(), out var loader))
            return;
        _selectedLoader = loader;
        _loaderCatalogKey = null;
        if (_initialized) _ = RefreshLoaderVersionsAsync();
    }

    private void LoaderVersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateLoaderInstallState();

    private void UpdateLoaderInstallState()
    {
        var selected = LoaderVersionCombo.SelectedItem as LoaderVersionOption;
        var alreadyInstalled = IsLoaderVersionInstalled(
            _installedVersions, _loaderTargetVersion, selected);
        SetComboBoxAvailability(
            LoaderVersionCombo,
            _loaderTargetVersion is not null && _loaderVersions.Count > 0);
        InstallLoaderButton.Content = alreadyInstalled
            ? "当前版本已安装"
            : "安装所选加载器";
        InstallLoaderButton.IsEnabled =
            _loaderTargetVersion is not null &&
            selected is not null &&
            !alreadyInstalled &&
            _operationCts is null;
    }

    internal static bool IsLoaderVersionInstalled(
        IEnumerable<InstalledVersionInfo> versions,
        string? targetVersion,
        LoaderVersionOption? selected)
    {
        if (string.IsNullOrWhiteSpace(targetVersion) || selected is null)
            return false;
        return versions.Any(version =>
            version.Loader == selected.Loader &&
            string.Equals(
                version.BaseVersion, targetVersion, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                version.LoaderVersion, selected.Version, StringComparison.OrdinalIgnoreCase));
    }

    private async Task RefreshLoaderVersionsAsync()
    {
        var target = _loaderTargetVersion;
        var loader = _selectedLoader;
        if (string.IsNullOrWhiteSpace(target)) return;
        var key = $"{loader}|{target}";
        if (string.Equals(_loaderCatalogKey, key, StringComparison.Ordinal))
            return;

        _loaderCatalogCts?.Cancel();
        _loaderCatalogCts?.Dispose();
        var cancellation = new CancellationTokenSource();
        _loaderCatalogCts = cancellation;
        var token = cancellation.Token;
        _loaderCatalogKey = key;
        _loaderVersions.Clear();
        LoaderVersionStatus.Text = $"正在读取 {LoaderName(loader)} 版本…";
        UpdateLoaderInstallState();
        try
        {
            var versions = await _loaderCatalog.GetVersionsAsync(loader, target, token);
            if (token.IsCancellationRequested || !string.Equals(_loaderCatalogKey, key, StringComparison.Ordinal))
                return;
            foreach (var version in versions) _loaderVersions.Add(version);
            LoaderVersionCombo.SelectedIndex = _loaderVersions.Count > 0 ? 0 : -1;
            LoaderVersionStatus.Text = _loaderVersions.Count > 0
                ? $"共 {_loaderVersions.Count} 个版本；默认选择最新版本。"
                : $"{LoaderName(loader)} 没有适用于 Minecraft {target} 的版本。";
        }
        catch (OperationCanceledException)
        {
            // A newer target or loader selection superseded this request.
        }
        catch (Exception ex)
        {
            _loaderCatalogKey = null;
            LogService.Warn($"读取 {LoaderName(loader)} 版本失败：{ex.Message}");
            LoaderVersionStatus.Text = $"读取版本失败：{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_loaderCatalogCts, cancellation))
                UpdateLoaderInstallState();
        }
    }

    private static string LoaderName(ModLoader loader) => loader switch
    {
        ModLoader.Fabric => "Fabric",
        ModLoader.Forge => "Forge",
        ModLoader.NeoForge => "NeoForge",
        ModLoader.Quilt => "Quilt",
        ModLoader.OptiFine => "OptiFine",
        _ => "原版"
    };

    private void UpdateLaunchState()
    {
        var hasVersion = HomeVersionCombo.SelectedItem is InstalledVersionInfo;
        var running = _runningGame is { HasExited: false };
        var hasInstalledVersions = _installedVersions.Count > 0;
        HomeVersionCombo.IsEnabled = hasInstalledVersions;
        LaunchButton.IsEnabled =
            (hasVersion || !hasInstalledVersions) && _operationCts is null;
        LaunchButton.Content = GetLaunchActionLabel(
            hasVersion, hasInstalledVersions, running, _launchInProgress);
        if (running)
            HomeLaunchStatusText.Text = $"Minecraft 正在运行 · PID {_runningGame!.Id}";
        else if (!hasVersion)
            HomeLaunchStatusText.Text = hasInstalledVersions
                ? "请选择一个已安装的游戏版本。"
                : "还没有下载游戏版本，点击下方按钮开始下载。";
    }

    internal static string GetLaunchActionLabel(
        bool hasSelection,
        bool hasInstalledVersions,
        bool running,
        bool launching) =>
        running
            ? "游戏运行中"
            : launching
                ? "正在启动…"
                : !hasSelection && !hasInstalledVersions
                    ? "下载游戏"
                    : "启动游戏";

    private async void DownloadVersion_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not RemoteVersion remote) return;
        if (!_manifestById.TryGetValue(remote.Id, out var manifestVersion)) return;
        var hasLocalVersions = remote.IsInstalled;
        var confirmed = await ConfirmInstallAsync(
            hasLocalVersions ? "添加 Minecraft 版本" : "确认安装 Minecraft",
            hasLocalVersions
                ? "可以为这个基础版本继续添加不同加载器、加载器版本或独立原版实例。已有版本不会被覆盖。"
                : "启动器将下载客户端、依赖库、资源文件和所需 Java。下载支持断点续传。",
            $"Minecraft {remote.Id} · {remote.TypeLabel}",
            hasLocalVersions ? "添加" : "确认安装",
            loaderBaseVersion: remote.Id,
            allowDisplayName: true);
        if (!confirmed) return;
        var selectedLoader = _confirmedLoaderOption;
        var optiFineOption = _confirmedOptiFineOption;
        var optiFabricFile = _confirmedOptiFabricFile;
        var installOptiFabric = _confirmedInstallOptiFabric;
        var displayName = _confirmedDisplayName;
        _confirmedLoaderOption = null;
        _confirmedOptiFineOption = null;
        _confirmedOptiFabricFile = null;
        _confirmedInstallOptiFabric = false;
        _confirmedDisplayName = null;
        await RunOperationAsync($"安装 Minecraft {remote.Id}", async token =>
        {
            var progress = new Progress<DownloadProgress>(UpdateDownloadProgress);
            var existingVersionIds = _profiles.GetInstanceIds();
            var baseWasInstalled = _minecraft.IsVersionInstalled(remote.Id);
            if (selectedLoader is null || !baseWasInstalled)
            {
                await _installer.InstallOfficialAsync(
                    manifestVersion, progress, token,
                    asDependency: selectedLoader is not null);
            }
            InstalledVersionInfo? installed = null;
            if (selectedLoader is not null)
                installed = await InstallLoaderOptionAsync(remote.Id, selectedLoader, progress, token);
            if (installOptiFabric && installed is not null &&
                optiFineOption is not null && optiFabricFile is not null)
                await InstallOptiFabricFilesAsync(
                    installed, optiFineOption, optiFabricFile, progress, token);
            var sourceInstalled = installed ??
                                  _profiles.GetInstalledVersions()
                                      .FirstOrDefault(item =>
                                          item.Id.Equals(remote.Id, StringComparison.OrdinalIgnoreCase))
                                  ?? new InstalledVersionInfo
                                  {
                                      Id = remote.Id,
                                      BaseVersion = remote.Id,
                                      Loader = ModLoader.Vanilla,
                                      Ready = true
                                  };
            var finalInstalled = await FinalizeVersionInstallationAsync(
                sourceInstalled, displayName, existingVersionIds, token);
            var installedId = finalInstalled.Id;
            RefreshInstalledVersions();
            HomeVersionCombo.SelectedItem =
                _installedVersions.FirstOrDefault(x => x.Id == installedId) ?? HomeVersionCombo.SelectedItem;
            GlobalStatusText.Text = installOptiFabric
                ? $"Fabric + OptiFabric {optiFineOption?.DisplayName} 已安装"
                : selectedLoader is null
                    ? $"Minecraft {remote.Id} 已安装"
                    : $"{LoaderName(selectedLoader.Loader)} {selectedLoader.Version} 已安装";
        }, trackVersionStatus: true);
    }

    private async void InstallSelectedLoader_Click(object sender, RoutedEventArgs e)
    {
        var target = _loaderTargetVersion;
        if (string.IsNullOrWhiteSpace(target))
        {
            MessageBox.Show(this, "请先选择一个 Minecraft 原版版本。", "选择版本",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (LoaderVersionCombo.SelectedItem is not LoaderVersionOption option) return;
        if (IsLoaderVersionInstalled(_installedVersions, target, option))
        {
            UpdateLoaderInstallState();
            return;
        }

        var baseInstalled = _minecraft.IsVersionInstalled(target);
        var confirmed = await ConfirmInstallAsync(
            $"安装 {LoaderName(option.Loader)} {option.Version}",
            baseInstalled
                ? $"将为 Minecraft {target} 安装 {LoaderName(option.Loader)} {option.Version}。创建加载器版本不会覆盖原版文件。"
                : $"原版 {target} 尚未安装，将先下载原版及所需资源，再安装 {LoaderName(option.Loader)} {option.Version}。",
            $"Minecraft {target} · {LoaderName(option.Loader)} {option.Version}",
            "确认安装",
            allowDisplayName: true);
        if (!confirmed) return;
        var displayName = _confirmedDisplayName;
        _confirmedDisplayName = null;

        await RunOperationAsync($"安装 {LoaderName(option.Loader)} {option.Version}", async token =>
        {
            var existingVersionIds = _profiles.GetInstanceIds();
            if (!_minecraft.IsVersionInstalled(target))
            {
                if (!_manifestById.TryGetValue(target, out var official))
                    throw new InvalidOperationException($"找不到原版 {target} 的下载信息。");
                await _installer.InstallOfficialAsync(official,
                    new Progress<DownloadProgress>(UpdateDownloadProgress), token,
                    asDependency: true);
            }
            var downloadProgress = new Progress<DownloadProgress>(UpdateDownloadProgress);
            var installed = await InstallLoaderOptionAsync(
                target, option, downloadProgress, token);
            installed = await FinalizeVersionInstallationAsync(
                installed, displayName, existingVersionIds, token);
            RefreshInstalledVersions();
            HomeVersionCombo.SelectedItem =
                _installedVersions.FirstOrDefault(x => x.Id == installed.Id) ?? HomeVersionCombo.SelectedItem;
            ModsVersionCombo.SelectedItem =
                _modVersions.FirstOrDefault(x => x.Id == installed.Id) ?? ModsVersionCombo.SelectedItem;
            GlobalStatusText.Text = $"{installed.LoaderLabel} 已安装到 {target}";
        }, trackVersionStatus: true);
    }

    private async Task<InstalledVersionInfo> InstallLoaderOptionAsync(
        string target,
        LoaderVersionOption option,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken) =>
        option.Loader switch
        {
            ModLoader.Fabric => await _installer.InstallFabricVersionAsync(
                target, option.Version, progress, cancellationToken),
            ModLoader.Quilt => await _installer.InstallQuiltVersionAsync(
                target, option.Version, progress, cancellationToken),
            ModLoader.Forge or ModLoader.NeoForge => await InstallInstallerLoaderAsync(
                target, option, progress, cancellationToken),
            ModLoader.OptiFine => await InstallOptiFineLoaderAsync(
                target, option, progress, cancellationToken),
            _ => throw new InvalidOperationException("不支持的 Mod 加载器。")
        };

    private async Task<InstalledVersionInfo> InstallOptiFineLoaderAsync(
        string target,
        LoaderVersionOption option,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        var baseProfile = await _profiles.ResolveAsync(target, cancellationToken);
        var javaRuntime = await _java.EnsureJavaAsync(
            baseProfile, Settings.JavaPath, progress, cancellationToken,
            requireExactMajor: true);
        return await _installer.InstallOptiFineAsync(
            target, option, javaRuntime.Executable, progress, cancellationToken);
    }

    private async Task InstallOptiFabricFilesAsync(
        InstalledVersionInfo instance,
        LoaderVersionOption optiFine,
        RemoteModFile optiFabric,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        var modsDirectory = Path.Combine(
            GetVersionGameDirectory(instance.Id), "mods");
        Directory.CreateDirectory(modsDirectory);
        var fabricApi = await _curseForge.FindProjectFileAsync(
            "fabric-api", instance.BaseVersion, ModLoader.Fabric, cancellationToken);
        var files = new List<(string Name, string Url, long Size)>
        {
            ($"OptiFine_{instance.BaseVersion}_{optiFine.Version}.jar",
                optiFine.InstallerUrl!, 0),
            (Path.GetFileName(optiFabric.Name), optiFabric.Url, optiFabric.Size)
        };
        if (fabricApi is not null)
            files.Add((Path.GetFileName(fabricApi.Name), fabricApi.Url, fabricApi.Size));

        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            progress.Report(new DownloadProgress(
                "正在安装 OptiFabric", index, files.Count, file.Name, 0, null, 0, null,
                "CurseForge"));
            await _http.DownloadFileAsync(
                file.Url,
                Path.Combine(modsDirectory, file.Name),
                null,
                file.Size > 0 ? file.Size : null,
                null,
                cancellationToken);
        }
        LogService.Info($"已为 {instance.Id} 安装 OptiFine、OptiFabric 和 Fabric API");
    }

    private async Task<InstalledVersionInfo> InstallInstallerLoaderAsync(
        string target,
        LoaderVersionOption option,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        var baseProfile = await _profiles.ResolveAsync(target, cancellationToken);
        var installerJava = await _java.EnsureJavaAsync(
            baseProfile, Settings.JavaPath, progress, cancellationToken,
            requireExactMajor: true);
        return await _installer.InstallWithInstallerAsync(
            target, option, installerJava.Executable, progress, cancellationToken);
    }

    private async void RefreshManifest_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoadManifestAsync(true);
        }
        catch (Exception ex)
        {
            ShowError("刷新版本清单失败", ex);
        }
    }

    private void RefreshInstalled_Click(object sender, RoutedEventArgs e)
    {
        RefreshInstalledVersions();
        GlobalStatusText.Text = "本地版本已刷新";
    }

    private void UpdateMyVersionsView()
    {
        var vanilla = _installedVersions.Count(version => version.Loader == ModLoader.Vanilla);
        var fabric = _installedVersions.Count(version => version.Loader == ModLoader.Fabric);
        var forge = _installedVersions.Count(version => version.Loader == ModLoader.Forge);
        var neoForge = _installedVersions.Count(version => version.Loader == ModLoader.NeoForge);
        var quilt = _installedVersions.Count(version => version.Loader == ModLoader.Quilt);
        MyVersionsSummaryText.Text = _installedVersions.Count == 0
            ? "所有已安装版本集中显示在这里"
            : $"原版 {vanilla} · Fabric {fabric} · Forge {forge} · NeoForge {neoForge} · Quilt {quilt}";
        MyVersionsCountText.Text = $"{_installedVersions.Count} 个版本";
        MyVersionsList.Visibility = _installedVersions.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        MyVersionsEmpty.Visibility = _installedVersions.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        MyVersionsPathText.Text = Settings.VersionIsolation
            ? Path.Combine(Settings.DataDirectory, "versions")
            : Settings.DataDirectory + "（共用目录）";
    }

    private void RefreshMyVersions_Click(object sender, RoutedEventArgs e)
    {
        RefreshInstalledVersions();
        GlobalStatusText.Text = "我的版本已刷新";
    }

    private void DownloadNewVersion_Click(object sender, RoutedEventArgs e) => SetPage(PageKind.Versions);

    private void MyVersionsOpenRoot_Click(object sender, RoutedEventArgs e) =>
        OpenDirectory(Settings.VersionIsolation
            ? Path.Combine(Settings.DataDirectory, "versions")
            : Settings.DataDirectory);

    private async void MyVersionLaunch_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not InstalledVersionInfo version) return;
        HomeVersionCombo.SelectedItem =
            _installedVersions.FirstOrDefault(item => item.Id == version.Id) ?? version;
        await LaunchVersionAsync(version);
    }

    private void MyVersionMods_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not InstalledVersionInfo version) return;
        var modVersion = _modVersions.FirstOrDefault(item => item.Id == version.Id);
        if (modVersion is null)
        {
            var message = "原版版本不能直接安装 Mod，请先安装 Fabric、Quilt、Forge 或 NeoForge 版本。";
            MessageBox.Show(this, message, "暂不可用", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ModsVersionCombo.SelectedItem = modVersion;
        SetPage(PageKind.Mods);
    }

    private void MyVersionLoadedMods_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not InstalledVersionInfo version) return;
        _loadedModsVersion = version;
        LoadedModsVersionText.Text =
            $"{version.DisplayLabel} · Minecraft {version.BaseVersion}";
        var mods = RefreshLoadedMods();
        ShowOverlayWindow(
            LoadedModsOverlay,
            $"已加载 Mod · {version.DisplayLabel}",
            () =>
            {
                _loadedModsLookupCts?.Cancel();
                HideOverlayWindow(LoadedModsOverlay);
            });
        StartLoadedModsLookup(mods);
    }

    private void RefreshLoadedMods_Click(object sender, RoutedEventArgs e)
    {
        var mods = RefreshLoadedMods();
        StartLoadedModsLookup(mods);
    }

    private void ToggleLoadedMod_Click(object sender, RoutedEventArgs e)
    {
        if (_loadedModsVersion is null ||
            (sender as Button)?.Tag is not InstalledModEntry mod)
            return;

        try
        {
            var modsDirectory = Path.Combine(
                GetVersionGameDirectory(_loadedModsVersion.Id), "mods");
            InstalledModService.SetEnabled(
                Path.Combine(modsDirectory, mod.FileName), !mod.IsEnabled);
            var mods = RefreshLoadedMods();
            StartLoadedModsLookup(mods);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            LogService.Warn($"修改 Mod 状态失败 {mod.FileName}：{ex.Message}");
            MessageBox.Show(
                this,
                $"无法修改“{mod.PrimaryName}”的状态。\n\n" +
                "如果游戏正在运行，请先完全退出游戏再重试。\n" +
                ex.Message,
                "修改 Mod 状态失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private IReadOnlyList<InstalledModEntry> RefreshLoadedMods()
    {
        if (_loadedModsVersion is null) return [];
        try
        {
            var modsDirectory = Path.Combine(
                GetVersionGameDirectory(_loadedModsVersion.Id), "mods");
            var iconCacheDirectory = Path.Combine(
                Settings.DataDirectory, "cache", "installed-mod-icons-v1");
            var mods = InstalledModService.Scan(modsDirectory, iconCacheDirectory);
            foreach (var entry in mods)
            {
                if (_installedModLookupCache.TryGetValue(
                        GetInstalledModCacheKey(entry),
                        out var cached))
                    entry.ApplyChineseMetadata(cached.ChineseName, null);
            }
            LoadedModsList.ItemsSource = mods;
            LoadedModsList.Visibility = mods.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            LoadedModsEmptyState.Visibility = mods.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            UpdateLoadedModsCount(mods);
            return mods;
        }
        catch (Exception ex)
        {
            ShowError("读取已加载 Mod 失败", ex);
            return [];
        }
    }

    private void StartLoadedModsLookup(IReadOnlyList<InstalledModEntry> mods)
    {
        _loadedModsLookupCts?.Cancel();
        _loadedModsLookupCts?.Dispose();
        _loadedModsLookupCts = null;
        if (mods.Count == 0) return;

        var cancellation = new CancellationTokenSource();
        _loadedModsLookupCts = cancellation;
        _ = EnrichLoadedModsAsync(mods, cancellation);
    }

    private async Task EnrichLoadedModsAsync(
        IReadOnlyList<InstalledModEntry> mods,
        CancellationTokenSource cancellation)
    {
        try
        {
            for (var index = 0; index < mods.Count; index++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var entry = mods[index];
                try
                {
                    var key = GetInstalledModCacheKey(entry);
                    if (!_installedModLookupCache.TryGetValue(key, out var metadata))
                    {
                        var match = await _mcmod.FindBestMatchAsync(
                            entry.SearchQuery, cancellation.Token);
                        var chineseName = match?.ChineseTitle;
                        if (string.IsNullOrWhiteSpace(chineseName) &&
                            !string.IsNullOrWhiteSpace(match?.DisplayTitle) &&
                            Regex.IsMatch(match.DisplayTitle, @"[\u3400-\u9fff]"))
                            chineseName = match.DisplayTitle;
                        metadata = (chineseName, match?.CoverUrl);
                        _installedModLookupCache[key] = metadata;
                    }

                    string? coverPath = null;
                    if (!string.IsNullOrWhiteSpace(metadata.CoverUrl))
                    {
                        coverPath = await _coverCache.GetThemeCoverPathAsync(
                            metadata.CoverUrl, !ThemeService.IsDark, cancellation.Token);
                    }
                    entry.ApplyChineseMetadata(metadata.ChineseName, coverPath);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogService.Warn(
                        $"自动匹配 Mod {entry.FileName} 的中文信息失败：{ex.Message}");
                }
                LoadedModsCountText.Text =
                    $"{mods.Count} 个 Mod · 正在匹配中文名和图标 {index + 1}/{mods.Count}";
            }
        }
        catch (OperationCanceledException)
        {
            // The dialog was closed or refreshed.
        }
        catch (Exception ex)
        {
            LogService.Warn($"自动匹配已加载 Mod 信息失败：{ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_loadedModsLookupCts, cancellation))
            {
                _loadedModsLookupCts = null;
                UpdateLoadedModsCount(mods);
            }
            cancellation.Dispose();
        }
    }

    private static string GetInstalledModCacheKey(InstalledModEntry entry) =>
        $"{entry.FileName}|{entry.SizeBytes}";

    private void UpdateLoadedModsCount(IReadOnlyList<InstalledModEntry> mods)
    {
        var enabled = mods.Count(mod => mod.IsEnabled);
        LoadedModsCountText.Text = mods.Count == 0
            ? "0 个 Mod"
            : $"{mods.Count} 个 Mod · {enabled} 个已启用 · {mods.Count - enabled} 个已关闭";
    }

    private void OpenLoadedModsDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (_loadedModsVersion is null) return;
        OpenDirectory(Path.Combine(
            GetVersionGameDirectory(_loadedModsVersion.Id), "mods"));
    }

    private void CloseLoadedMods_Click(object sender, RoutedEventArgs e)
    {
        _loadedModsLookupCts?.Cancel();
        HideOverlayWindow(LoadedModsOverlay);
    }

    private void MyVersionOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not InstalledVersionInfo version) return;
        OpenDirectory(GetVersionGameDirectory(version.Id));
    }

    private async void MyVersionRepair_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not InstalledVersionInfo version) return;
        var confirmed = await ConfirmInstallAsync(
            "检查并补齐文件",
            "启动器会检查该版本所需文件，仅下载缺失或大小不正确的部分。",
            version.DisplayLabel,
            "开始检查");
        if (!confirmed) return;
        await RunOperationAsync($"检查 {version.DisplayLabel}", async token =>
        {
            await _installer.EnsureVersionAsync(version.Id,
                new Progress<DownloadProgress>(UpdateDownloadProgress), token);
            RefreshInstalledVersions();
            GlobalStatusText.Text = $"{version.DisplayLabel} 文件检查完成";
        });
    }

    private void MyVersionRename_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not InstalledVersionInfo version) return;
        _renamingVersion = version;
        VersionRenameBox.Text = version.DisplayLabel;
        ShowOverlayWindow(
            VersionRenameOverlay,
            "重命名版本",
            () => CancelVersionRename_Click(this, new RoutedEventArgs()));
        Dispatcher.BeginInvoke(() =>
        {
            VersionRenameBox.Focus();
            VersionRenameBox.SelectAll();
        });
    }

    private void VersionRenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ConfirmVersionRename_Click(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelVersionRename_Click(sender, e);
        }
    }

    private async void ConfirmVersionRename_Click(object sender, RoutedEventArgs e)
    {
        var version = _renamingVersion;
        var displayName = VersionRenameBox.Text.Trim();
        if (version is null) return;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            VersionRenameBox.Focus();
            return;
        }
        try
        {
            var profile = await _profiles.LoadLocalAsync(version.Id);
            VersionMetadata.Set(profile, VersionMetadata.DisplayName, displayName);
            _profiles.SaveLocal(version.Id, profile.ToJsonString());
            HideOverlayWindow(VersionRenameOverlay);
            _renamingVersion = null;
            RefreshInstalledVersions();
            HomeVersionCombo.SelectedItem =
                _installedVersions.FirstOrDefault(item => item.Id == version.Id)
                ?? HomeVersionCombo.SelectedItem;
            GlobalStatusText.Text = $"版本已重命名为 {displayName}";
        }
        catch (Exception ex)
        {
            ShowError("重命名版本失败", ex);
        }
    }

    private void CancelVersionRename_Click(object sender, RoutedEventArgs e)
    {
        HideOverlayWindow(VersionRenameOverlay);
        _renamingVersion = null;
    }

    private async void MyVersionDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not InstalledVersionInfo version) return;
        if (_runningGame is { HasExited: false })
        {
            MessageBox.Show(this, "请先退出正在运行的 Minecraft，再复制版本。",
                "无法复制", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await RunOperationAsync($"复制 {version.DisplayLabel}", async token =>
        {
            var dataRoot = Path.GetFullPath(Settings.DataDirectory);
            var versionsRoot = Path.Combine(dataRoot, "versions");
            var source = Path.Combine(versionsRoot, version.Id);
            if (!Directory.Exists(source))
                throw new DirectoryNotFoundException("找不到要复制的版本目录。");

            var suffix = 1;
            string newId;
            string destination;
            do
            {
                newId = version.Id + (suffix == 1 ? "-copy" : $"-copy-{suffix}");
                destination = Path.Combine(versionsRoot, newId);
                suffix++;
            } while (Directory.Exists(destination));

            var copyName = version.DisplayLabel + " 副本";
            await Task.Run(() => CloneVersionDirectory(
                source, destination, version.Id, newId, copyName, token), token);
            RefreshInstalledVersions();
            var copied = _installedVersions.FirstOrDefault(item => item.Id == newId);
            if (copied is not null)
            {
                HomeVersionCombo.SelectedItem = copied;
                Settings.LastVersionId = copied.Id;
                PersistConfig();
            }
            GlobalStatusText.Text = $"{version.DisplayLabel} 已复制为 {copyName}";
        });
    }

    private static void CloneVersionDirectory(
        string source,
        string destination,
        string sourceId,
        string destinationId,
        string displayName,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(
                     source, "*", System.IO.SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }
        foreach (var file in Directory.EnumerateFiles(
                     source, "*", System.IO.SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }

        var sourceJson = Path.Combine(destination, sourceId + ".json");
        var destinationJson = Path.Combine(destination, destinationId + ".json");
        JsonNode? profile;
        if (File.Exists(sourceJson))
        {
            profile = JsonNode.Parse(File.ReadAllText(sourceJson)) as JsonObject;
        }
        else
        {
            profile = File.Exists(destinationJson)
                ? JsonNode.Parse(File.ReadAllText(destinationJson)) as JsonObject
                : null;
        }
        if (profile is not JsonObject profileObject)
            throw new InvalidDataException("复制后的版本配置无效。");
        profileObject["id"] = destinationId;
        VersionMetadata.Set(profileObject, VersionMetadata.DisplayName, displayName);
        File.WriteAllText(destinationJson, profileObject.ToJsonString());
        if (!string.Equals(sourceJson, destinationJson, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(sourceJson))
            File.Delete(sourceJson);

        var sourceJar = Path.Combine(destination, sourceId + ".jar");
        if (File.Exists(sourceJar))
            File.Move(sourceJar, Path.Combine(destination, destinationId + ".jar"), true);
        var sourceMappings = Path.Combine(destination, sourceId + "-mappings.txt");
        if (File.Exists(sourceMappings))
            File.Move(sourceMappings,
                Path.Combine(destination, destinationId + "-mappings.txt"), true);
    }

    private async Task<InstalledVersionInfo> FinalizeVersionInstallationAsync(
        InstalledVersionInfo source,
        string? requestedName,
        IReadOnlySet<string> existingVersionIds,
        CancellationToken cancellationToken)
    {
        if (!VersionProfileService.TryNormalizeInstanceId(
                requestedName, out var instanceId, out var error) ||
            instanceId.Length == 0)
        {
            if (error.Length > 0) throw new InvalidOperationException(error);
            return source;
        }

        var sourceWasInstalled = existingVersionIds.Contains(source.Id);
        return await _profiles.MaterializeInstanceAsync(
            source, instanceId, sourceWasInstalled, cancellationToken);
    }

    private async void MyVersionDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not InstalledVersionInfo version) return;
        if (_runningGame is { HasExited: false })
        {
            MessageBox.Show(this, "请先退出正在运行的 Minecraft，再删除版本。", "无法删除",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (version.Loader == ModLoader.Vanilla &&
            _profiles.HasInheritedDependents(version.Id))
        {
            MessageBox.Show(this,
                $"仍有版本直接继承 Minecraft {version.Id}。请先删除这些继承版本。",
                "版本仍被使用", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dataRoot = Path.GetFullPath(Settings.DataDirectory);
        var versionDirectory = Path.Combine(dataRoot, "versions", version.Id);
        var size = SafeDirectorySize(versionDirectory);
        var confirmed = await ConfirmInstallAsync(
            "删除版本",
            Settings.VersionIsolation
                ? "该版本的游戏存档、Mod 和配置会移入 Windows 回收站，可在清空回收站前恢复。共享的 Java、依赖库和资源缓存会保留。"
                : "当前已关闭版本隔离。删除版本只会移除版本配置，共用目录中的存档、Mod 和配置会保留。",
            $"{version.DisplayLabel} · {FormatBytes(size)}",
            "确认删除",
            destructive: true);
        if (!confirmed) return;

        await RunOperationAsync($"删除 {version.DisplayLabel}", async token =>
        {
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                DeleteDirectoryToRecycleBin(dataRoot, versionDirectory);
                DeleteDirectoryToRecycleBin(dataRoot, Path.Combine(dataRoot, "natives", version.Id));
            }, token);
            if (Settings.LastVersionId == version.Id) Settings.LastVersionId = null;
            RefreshInstalledVersions();
            Settings.LastVersionId = (HomeVersionCombo.SelectedItem as InstalledVersionInfo)?.Id;
            PersistConfig();
            LogService.Info($"已将版本 {version.DisplayLabel} 移入回收站");
            GlobalStatusText.Text = $"{version.DisplayLabel} 已移入回收站";
        });
    }

    private async void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runningGame is { HasExited: false })
        {
            HomeLaunchStatusText.Text =
                $"Minecraft 已在运行（PID {_runningGame.Id}），请查看任务栏或游戏窗口。";
            GlobalStatusText.Text = "Minecraft 已在运行";
            return;
        }
        if (_installedVersions.Count == 0)
        {
            HomeLaunchStatusText.Text = "请选择要下载的 Minecraft 版本。";
            GlobalStatusText.Text = "已打开版本下载";
            SetPage(PageKind.Versions);
            return;
        }
        if (HomeVersionCombo.SelectedItem is not InstalledVersionInfo version)
        {
            HomeLaunchStatusText.Text = "请先安装并选择一个游戏版本。";
            MessageBox.Show(this, "请先安装并选择一个游戏版本。", "无法启动",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await LaunchVersionAsync(version);
    }

    private async Task LaunchVersionAsync(InstalledVersionInfo version)
    {
        if (HasIncompatibleOptiFabric(version))
        {
            HomeLaunchStatusText.Text =
                "该 OptiFabric 版本使用了不兼容的新版 Fabric Loader，请重新安装 OptiFabric 版本。";
            MessageBox.Show(this,
                "此版本中的 OptiFabric 1.14.3 与当前 Fabric Loader 不兼容。\n\n" +
                "请在“版本管理”中重新安装该 Minecraft 版本，加载器选择 Fabric 并开启 OptiFabric；" +
                "启动器会自动使用兼容的 Fabric Loader 0.15.3。",
                "OptiFabric 版本不兼容", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var account = ActiveAccount;
        if (account is null)
        {
            HomeLaunchStatusText.Text = "尚未选择账户，请点击右上角头像登录离线账户或 Microsoft 账户。";
            MessageBox.Show(this, "请点击右上角头像，创建离线账户或登录 Microsoft 账户。", "需要账户",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _launchInProgress = true;
        HomeLaunchStatusText.Text = "正在检查版本文件、Java 和启动参数…";
        UpdateLaunchState();
        try
        {
            await RunOperationAsync("准备启动 Minecraft", async token =>
            {
                await RepairIncompatibleFabricAsync(version, token);
                if (account.Kind == AccountKind.Microsoft &&
                    (account.AccessToken is null || account.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2)))
                {
                    HomeLaunchStatusText.Text = "正在刷新 Microsoft 登录…";
                    GlobalStatusText.Text = "正在刷新 Microsoft 登录…";
                    var refreshed = await _auth.RefreshAsync(account, token);
                    account.Name = refreshed.Name;
                    account.MinecraftUuid = refreshed.MinecraftUuid;
                    account.Xuid = refreshed.Xuid;
                    account.SkinUrl = refreshed.SkinUrl;
                    account.AccessToken = refreshed.AccessToken;
                    account.ExpiresAt = refreshed.ExpiresAt;
                    account.RefreshTokenProtected = refreshed.RefreshTokenProtected;
                    UpdateAccountUi();
                    _ = RefreshPlayerAvatarsAsync();
                    PersistConfig();
                }

                var launchOutput = new ConcurrentQueue<string>();
                var launchClock = Stopwatch.StartNew();
                var prepared = await _launcher.LaunchAsync(
                    version.Id,
                    account,
                    (int)MemorySlider.Value,
                    Settings.JavaPath,
                    new Progress<DownloadProgress>(UpdateDownloadProgress),
                    message =>
                    {
                        LogService.Info(message);
                        launchOutput.Enqueue(message);
                        while (launchOutput.Count > 100) launchOutput.TryDequeue(out _);
                    },
                    token,
                    Settings.JvmArguments,
                    Settings.GameArguments,
                    Settings.VersionIsolation,
                    Settings.GlobalGameOptions);
                _runningGame = prepared.Process;
                _ = MonitorGameWindowAsync(prepared.Process);
                prepared.Process.Exited += (_, _) => Dispatcher.InvokeAsync(() =>
                {
                    RestoreLauncherAfterGame();
                    var code = _runningGame?.ExitCode;
                    LogService.Info($"Minecraft 进程已退出，代码 {code}");
                    GlobalStatusText.Text = "游戏已退出";
                    HomeLaunchStatusText.Text = "Minecraft 已退出";
                    GlobalProgressBar.Value = 0;
                    UpdateLaunchState();
                    if (launchClock.Elapsed < TimeSpan.FromSeconds(30) && code != 0)
                    {
                        var details = string.Join(Environment.NewLine, launchOutput.TakeLast(40));
                        if (string.IsNullOrWhiteSpace(details)) details = "Minecraft 没有输出错误信息。";
                        ShowError("Minecraft 启动失败",
                            new InvalidOperationException(
                                $"游戏进程提前退出，代码 {code}。\n\n{details}"));
                    }
                });
                HomeLaunchStatusText.Text =
                    $"Minecraft 进程已启动 · PID {prepared.Process.Id}，正在等待游戏窗口…";
                GlobalStatusText.Text = "Minecraft 正在运行";
                UpdateLaunchState();
                LogService.Info($"启动参数：" + string.Join(" ", prepared.ArgumentsForDisplay.Select(QuoteForLog)));
                HideLauncherForGameIfNeeded();
            });
        }
        finally
        {
            _launchInProgress = false;
            if (_runningGame is not { HasExited: false })
            {
                HomeLaunchStatusText.Text = GlobalStatusText.Text.Contains("失败", StringComparison.Ordinal)
                    ? "启动失败，请打开启动日志查看原因。"
                    : GlobalStatusText.Text.Contains("取消", StringComparison.Ordinal)
                        ? "已取消启动。"
                        : "启动任务已结束。";
            }
            UpdateLaunchState();
        }
    }

    private async Task RepairIncompatibleFabricAsync(
        InstalledVersionInfo version,
        CancellationToken cancellationToken)
    {
        if (version.Loader != ModLoader.Fabric) return;
        var profile = await _profiles.ResolveAsync(version.Id, cancellationToken);
        var javaMajor = profile.Obj("javaVersion")?["majorVersion"]?.GetValue<int>() ?? 0;
        if (!VersionInstaller.NeedsFabricUpgradeForJava(
                version.LoaderVersion, javaMajor))
            return;

        HomeLaunchStatusText.Text =
            $"Fabric {version.LoaderVersion} 与 Java {javaMajor} 不兼容，正在升级加载器…";
        GlobalStatusText.Text = "正在修复旧版 Fabric Loader";
        var versions = await _loaderCatalog.GetVersionsAsync(
            ModLoader.Fabric, version.BaseVersion, cancellationToken);
        var latest = versions.FirstOrDefault();
        if (latest is null)
            throw new InvalidOperationException(
                $"找不到适用于 Minecraft {version.BaseVersion} 的新版 Fabric Loader。");

        await _installer.RepairFabricVersionAsync(
            version.Id,
            version.BaseVersion,
            latest.Version,
            new Progress<DownloadProgress>(UpdateDownloadProgress),
            cancellationToken);
        RefreshInstalledVersions();
        HomeLaunchStatusText.Text =
            $"Fabric 已升级到 {latest.Version}，正在继续启动…";
        GlobalStatusText.Text = $"Fabric {latest.Version} 已修复";
    }

    private bool HasIncompatibleOptiFabric(InstalledVersionInfo version)
    {
        if (version.Loader != ModLoader.Fabric) return false;
        var parts = version.Id.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !Version.TryParse(parts[2], out var loaderVersion) ||
            loaderVersion <= Version.Parse(OptiFabricLoaderVersion))
            return false;
        var modsDirectory = Path.Combine(GetVersionGameDirectory(version.Id), "mods");
        if (!Directory.Exists(modsDirectory)) return false;
        try
        {
            return Directory.EnumerateFiles(modsDirectory, "*.jar").Any(path =>
                Path.GetFileName(path).Contains("optifabric", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private async Task MonitorGameWindowAsync(Process process)
    {
        var found = false;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                if (process.HasExited) return;
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(process.MainWindowHandle, 9);
                    SetForegroundWindow(process.MainWindowHandle);
                    found = true;
                    break;
                }
            }
            catch
            {
                return;
            }
            await Task.Delay(500);
        }
        if (!found && !process.HasExited)
        {
            HomeLaunchStatusText.Text =
                $"游戏进程正在运行（PID {process.Id}），但没有检测到窗口。诊断日志已写入程序目录。";
            LogService.Warn("Minecraft 进程正在运行，但 20 秒内没有检测到窗口。");
        }
    }

    private void HideLauncherForGameIfNeeded()
    {
        if (!Settings.HideLauncherDuringGame || _launcherHiddenForGame || !IsVisible) return;
        _launcherHiddenForGame = true;
        Hide();
        LogService.Info("已按设置隐藏启动器，等待 Minecraft 退出。");
    }

    private void RestoreLauncherAfterGame()
    {
        if (!_launcherHiddenForGame) return;
        _launcherHiddenForGame = false;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!IsVisible) Show();
        Activate();
        LogService.Info("Minecraft 已退出，启动器已恢复显示。");
    }

    private static string QuoteForLog(string arg) =>
        arg.Contains(' ') ? '"' + arg.Replace("\"", "'") + '"' : arg;

    private void HeaderAccountButton_Click(object sender, RoutedEventArgs e) => SetPage(PageKind.Accounts);
    private void HomeAccountAvatar_Click(object sender, RoutedEventArgs e) => SetPage(PageKind.Accounts);
    private void KumoCard_Click(object sender, RoutedEventArgs e) => SetPage(PageKind.Settings);
    private void GoToVersions_Click(object sender, RoutedEventArgs e) => SetPage(PageKind.Versions);

    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        ApplySidebarState();
    }

    private void ApplySidebarState()
    {
        var collapsed = _sidebarCollapsed;
        var version = ++_sidebarAnimationVersion;
        var targetWidth = collapsed ? 76d : 210d;
        var startWidth = SidebarPanel.ActualWidth > 0
            ? SidebarPanel.ActualWidth
            : collapsed ? 210d : 76d;
        var settingsStartX = SidebarSettingsContent.IsLoaded
            ? SidebarSettingsContent.TranslatePoint(new Point(0, 0), SidebarPanel).X
            : 0;
        SetSidebarContentState(
            expanded: false,
            animateText: false,
            updateSettingsLayout: !collapsed);
        SidebarToggleButton.ToolTip = collapsed ? "展开侧栏" : "收起侧栏";
        var icon = Geometry.Parse(collapsed ? "M 2,1 L 7,8 L 2,15" : "M 7,1 L 2,8 L 7,15");
        icon.Freeze();
        SidebarToggleIcon.Data = icon;

        SidebarPanel.BeginAnimation(WidthProperty, null);
        SidebarPanel.Width = targetWidth;
        var widthAnimation = new DoubleAnimation(
            startWidth,
            targetWidth,
            TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        widthAnimation.Completed += (_, _) =>
        {
            ResetSidebarSettingsTransform();
            if (version != _sidebarAnimationVersion) return;
            if (_sidebarCollapsed)
            {
                var settingsEndX = SidebarSettingsContent.TranslatePoint(
                    new Point(0, 0), SidebarPanel).X;
                SetSidebarContentState(
                    expanded: false,
                    animateText: false,
                    updateSettingsLayout: false);
                AnimateSidebarSettingsIntoPlace(
                    settingsEndX,
                    expanded: false,
                    durationMilliseconds: 120);
                return;
            }
            SetSidebarContentState(expanded: true, animateText: true);
        };
        SidebarPanel.BeginAnimation(
            WidthProperty,
            widthAnimation,
            HandoffBehavior.SnapshotAndReplace);
        if (SidebarSettingsContent.IsLoaded && !collapsed)
            AnimateSidebarSettingsIntoPlace(
                settingsStartX,
                expanded: true,
                durationMilliseconds: 240);
    }

    private void SetSidebarContentState(
        bool expanded,
        bool animateText,
        bool updateSettingsLayout = true)
    {
        SidebarHeader.Margin = new Thickness(14, 23, 18, 14);
        SidebarLogoColumn.Width = new GridLength(expanded ? 48 : 42);
        SidebarBrandPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        SidebarWorkspaceLabel.Visibility = expanded ? Visibility.Visible : Visibility.Hidden;
        if (updateSettingsLayout)
            SetSidebarSettingsLayout(expanded);
        SidebarSettingsText.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;

        var textElements = new List<FrameworkElement>
        {
            SidebarBrandPanel,
            SidebarWorkspaceLabel,
            SidebarSettingsText
        };
        foreach (var navigation in new[] { HomeNav, ModsNav, VersionsNav, MyVersionsNav })
        {
            navigation.ApplyTemplate();
            if (navigation.Template.FindName("Label", navigation) is TextBlock label)
            {
                label.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                textElements.Add(label);
            }
            navigation.ToolTip = expanded ? null : navigation.Content?.ToString();
        }

        foreach (var element in textElements)
        {
            element.BeginAnimation(OpacityProperty, null);
            if (!expanded)
            {
                element.Opacity = 0;
                continue;
            }
            element.Opacity = 1;
            if (!animateText) continue;
            element.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop
                });
        }
    }

    private void AnimateSidebarSettingsIntoPlace(
        double startX,
        bool expanded,
        int durationMilliseconds)
    {
        var transform = SidebarSettingsContent.RenderTransform as TranslateTransform
                        ?? new TranslateTransform();
        SidebarSettingsContent.RenderTransform = transform;
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = 0;
        SetSidebarSettingsLayout(expanded);
        SidebarPanel.UpdateLayout();
        var targetX = SidebarSettingsContent.TranslatePoint(new Point(0, 0), SidebarPanel).X;
        var offset = startX - targetX;
        transform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(offset, 0, TimeSpan.FromMilliseconds(durationMilliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
    }

    private void SetSidebarSettingsLayout(bool expanded)
    {
        SidebarSettingsButton.Margin = expanded
            ? new Thickness(15, 0, 15, 15)
            : new Thickness(10, 0, 10, 15);
        SidebarSettingsButton.Padding = expanded
            ? new Thickness(12)
            : new Thickness(0);
        SidebarSettingsContent.HorizontalAlignment = expanded
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Center;
    }

    private void ResetSidebarSettingsTransform()
    {
        if (SidebarSettingsContent.RenderTransform is not TranslateTransform transform)
            return;
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = 0;
    }

    private void HomePreviousAccount_Click(object sender, RoutedEventArgs e) =>
        SwitchAdjacentHomeAccount(-1);

    private void HomeNextAccount_Click(object sender, RoutedEventArgs e) =>
        SwitchAdjacentHomeAccount(1);

    private void SwitchAdjacentHomeAccount(int offset)
    {
        if (_accounts.Count < 2) return;
        var current = ActiveAccount;
        var index = current is null ? 0 : _accounts.IndexOf(current);
        if (index < 0) index = 0;
        index = (index + offset + _accounts.Count) % _accounts.Count;
        var account = _accounts[index];
        if (account.Id == Settings.ActiveAccountId) return;
        Settings.ActiveAccountId = account.Id;
        PersistConfig();
        UpdateAccountUi();
        _ = RefreshPlayerAvatarsAsync();
        GlobalStatusText.Text = $"当前账户：{account.Name}";
    }

    private void ChooseHomeBackground_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择首页封面图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _ = LoadBitmap(dialog.FileName);
            var directory = Path.Combine(AppPaths.AppDataRoot, "cache", "home-background");
            Directory.CreateDirectory(directory);
            var extension = Path.GetExtension(dialog.FileName).ToLowerInvariant();
            if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif"))
                extension = ".png";
            var previousPath = Settings.HomeBackgroundImagePath;
            var destination = Path.Combine(directory, $"cover-{Guid.NewGuid():N}{extension}");
            File.Copy(dialog.FileName, destination, false);
            Settings.HomeBackgroundImagePath = destination;
            PersistConfig();
            UpdateHomeHeroImage(HomeVersionCombo.SelectedItem as InstalledVersionInfo);
            DeleteReplacedHomeBackground(previousPath, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       NotSupportedException or FileFormatException)
        {
            ShowError("无法设置首页封面图片", ex);
        }
    }

    private static void DeleteReplacedHomeBackground(string? previousPath, string currentPath)
    {
        if (string.IsNullOrWhiteSpace(previousPath) ||
            string.Equals(previousPath, currentPath, StringComparison.OrdinalIgnoreCase))
            return;
        var cacheDirectory = Path.GetFullPath(
            Path.Combine(AppPaths.AppDataRoot, "cache", "home-background"));
        var fullPath = Path.GetFullPath(previousPath);
        if (!fullPath.StartsWith(cacheDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            File.Delete(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warn($"无法清理旧首页封面缓存：{ex.Message}");
        }
    }

    private void ResetHomeBackground_Click(object sender, RoutedEventArgs e)
    {
        Settings.HomeBackgroundImagePath = null;
        PersistConfig();
        UpdateHomeHeroImage(HomeVersionCombo.SelectedItem as InstalledVersionInfo);
    }

    private void UpdateHomeHeroImage(InstalledVersionInfo? selected)
    {
        if (HomeHeroImage is null) return;
        var customPath = Settings.HomeBackgroundImagePath;
        HomeResetBackgroundButton.Visibility =
            !string.IsNullOrWhiteSpace(customPath)
                ? Visibility.Visible
                : Visibility.Collapsed;

        if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
        {
            try
            {
                var image = LoadBitmap(customPath);
                _heroBackdropCts?.Cancel();
                _heroBackdropCts = null;
                _heroBackdropAspectRatio = image.PixelWidth / (double)image.PixelHeight;
                ApplyHeroBackdropAspect();
                HomeHeroBackdropFill.Source = image;
                HomeHeroBackdrop.Source = image;
                HomeHeroImage.Source = null;
                HomeHeroImage.Clip = null;
                HomeHeroArtFrame.Visibility = Visibility.Collapsed;
                return;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or
                                           FileFormatException or UriFormatException)
            {
                LogService.Warn($"首页自定义背景读取失败：{ex.Message}");
            }
        }

        UpdateHomeHeroBackdrop(selected);
        var versionIcon = LoaderIconConverter.GetIcon(selected);
        HomeHeroImage.Source = versionIcon ?? LoadPackBitmap("Assets/logo-mark.png");
        HomeHeroArtFrame.Visibility =
            selected is null || versionIcon is null ? Visibility.Visible : Visibility.Collapsed;
        HomeHeroArtFrame.Width = 160;
        HomeHeroArtFrame.Height = 160;
        HomeHeroArtFrame.Margin = new Thickness(0, 64, 50, 0);
        HomeHeroArtFrame.Padding = new Thickness(0);
        HomeHeroArtFrame.CornerRadius = new CornerRadius(0);
        HomeHeroArtFrame.Background = Brushes.Transparent;
        HomeHeroArtFrame.BorderBrush = Brushes.Transparent;
        HomeHeroArtFrame.BorderThickness = new Thickness(0);
        HomeHeroArtFrame.HorizontalAlignment = HorizontalAlignment.Right;
        HomeHeroArtFrame.VerticalAlignment = VerticalAlignment.Top;
        HomeHeroImage.Margin = new Thickness(0);
        HomeHeroImage.Stretch = Stretch.Uniform;
        HomeHeroImage.Width = versionIcon is null ? double.NaN : 144;
        HomeHeroImage.Height = versionIcon is null ? double.NaN : 144;
        HomeHeroImage.HorizontalAlignment = versionIcon is null
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Center;
        HomeHeroImage.VerticalAlignment = versionIcon is null
            ? VerticalAlignment.Stretch
            : VerticalAlignment.Center;
        HomeHeroImage.Opacity = 0.96;
        HomeHeroImage.Clip = null;
        RenderOptions.SetBitmapScalingMode(
            HomeHeroImage, BitmapScalingMode.NearestNeighbor);
    }

    private void UpdateHomeHeroBackdrop(InstalledVersionInfo? selected)
    {
        _heroBackdropCts?.Cancel();
        _heroBackdropCts = null;
        HomeHeroBackdrop.Source = null;
        HomeHeroBackdropFill.Source = null;
        if (selected is null)
        {
            ResetHeroBackdropAspect();
            return;
        }

        var version = string.IsNullOrWhiteSpace(selected.BaseVersion)
            ? selected.Id
            : selected.BaseVersion;
        var cancellation = new CancellationTokenSource();
        _heroBackdropCts = cancellation;
        _ = LoadHomeHeroBackdropAsync(version, cancellation);
    }

    private async Task LoadHomeHeroBackdropAsync(
        string minecraftVersion,
        CancellationTokenSource cancellation)
    {
        try
        {
            var path = await _minecraftArtwork.GetBackgroundPathAsync(
                minecraftVersion, cancellation.Token);
            if (cancellation.IsCancellationRequested ||
                !ReferenceEquals(_heroBackdropCts, cancellation))
                return;
            if (string.IsNullOrWhiteSpace(path))
            {
                ResetHeroBackdropAspect();
                return;
            }
            var image = LoadBitmap(path);
            _heroBackdropAspectRatio = image.PixelWidth / (double)image.PixelHeight;
            ApplyHeroBackdropAspect();
            HomeHeroBackdropFill.Source = image;
            HomeHeroBackdrop.Source = image;
        }
        catch (OperationCanceledException)
        {
            // Selecting another version superseded this image.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       NotSupportedException or FileFormatException or
                                       UriFormatException or InvalidDataException or
                                       HttpRequestException)
        {
            LogService.Warn(
                $"无法加载 Minecraft {minecraftVersion} 的宣传图：{ex.Message}");
            if (ReferenceEquals(_heroBackdropCts, cancellation))
                ResetHeroBackdropAspect();
        }
        finally
        {
            if (ReferenceEquals(_heroBackdropCts, cancellation))
                _heroBackdropCts = null;
            cancellation.Dispose();
        }
    }

    internal static double GetHeroHeightForAspect(double width, double aspectRatio) =>
        width > 0 && aspectRatio > 0
            ? width / aspectRatio
            : 330;

    private void ApplyHeroBackdropAspect()
    {
        if (_heroBackdropAspectRatio <= 0) return;
        var width = HomeHeroCard.ActualWidth > 0 ? HomeHeroCard.ActualWidth : 990;
        HomeHeroCard.Height = GetHeroHeightForAspect(width, _heroBackdropAspectRatio);
    }

    private void ResetHeroBackdropAspect()
    {
        _heroBackdropAspectRatio = 0;
        HomeHeroCard.Height = 330;
    }

    private static BitmapImage LoadPackBitmap(string relativePath)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(
            $"pack://application:,,,/{relativePath.TrimStart('/')}", UriKind.Absolute);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void HomeHeroImage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
        var radius = HomeHeroArtFrame.Padding.Left > 0 ? 16d : 28d;
        var clip = new RectangleGeometry(
            new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), radius, radius);
        clip.Freeze();
        HomeHeroImage.Clip = clip;
    }

    private void HomeHeroCard_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
        var clip = new RectangleGeometry(
            new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 24, 24);
        clip.Freeze();
        HomeHeroCard.Clip = clip;
        if (_heroBackdropAspectRatio > 0)
        {
            var targetHeight = GetHeroHeightForAspect(
                e.NewSize.Width, _heroBackdropAspectRatio);
            if (Math.Abs(HomeHeroCard.Height - targetHeight) > 0.5)
                HomeHeroCard.Height = targetHeight;
        }
    }

    private void Navigation_Click(object sender, RoutedEventArgs e)
    {
        PageKind? page = sender == HomeNav
            ? PageKind.Home
            : sender == MyVersionsNav
                ? PageKind.MyVersions
                : sender == VersionsNav
                    ? PageKind.Versions
                    : sender == ModsNav
                        ? PageKind.Mods
                        : null;
        if (page is null) return;

        var appreciationUnlocked = TrackAppreciationNavigation(page.Value);
        SetPage(page.Value);
        if (appreciationUnlocked)
            ShowOverlayWindow(AppreciationOverlay, "支持 Kumo");
    }

    private bool TrackAppreciationNavigation(PageKind page)
    {
        if (page == AppreciationNavigationSequence[_appreciationSequenceProgress])
        {
            _appreciationSequenceProgress++;
            if (_appreciationSequenceProgress < AppreciationNavigationSequence.Length)
                return false;

            _appreciationSequenceProgress = 0;
            return true;
        }

        _appreciationSequenceProgress = page == PageKind.Home ? 1 : 0;
        return false;
    }

    private void CloseAppreciation_Click(object sender, RoutedEventArgs e) =>
        HideOverlayWindow(AppreciationOverlay);

    private void SupportAuthor_Click(object sender, MouseButtonEventArgs e)
    {
        ShowOverlayWindow(AppreciationOverlay, "支持 Kumo");
        e.Handled = true;
    }

    private void SetPage(PageKind page)
    {
        var target = page switch
        {
            PageKind.Home => HomePage,
            PageKind.MyVersions => MyVersionsPage,
            PageKind.Versions => VersionsPage,
            PageKind.Mods => ModsPage,
            PageKind.Accounts => AccountsPage,
            _ => SettingsPage
        };
        var changed = _currentPage != page;
        var version = ++_pageTransitionVersion;
        var previous = _currentPage is { } currentPage
            ? PageFor(currentPage)
            : null;
        foreach (var child in new FrameworkElement[]
                 {
                     HomePage, MyVersionsPage, VersionsPage, ModsPage, AccountsPage, SettingsPage
                 })
        {
            child.BeginAnimation(OpacityProperty, null);
            child.ClearValue(Panel.BackgroundProperty);
            child.Opacity = 1;
            child.Visibility = ReferenceEquals(child, target) ||
                               changed && ReferenceEquals(child, previous)
                ? Visibility.Visible
                : Visibility.Collapsed;
            Panel.SetZIndex(child, ReferenceEquals(child, target) ? 1 : 0);
        }
        _currentPage = page;
        if (changed)
        {
            if (IsLoaded)
                AnimatePageTransition(target, previous, version);
            else if (previous is not null)
                previous.Visibility = Visibility.Collapsed;
        }

        HomeNav.IsChecked = page == PageKind.Home;
        MyVersionsNav.IsChecked = page == PageKind.MyVersions;
        VersionsNav.IsChecked = page == PageKind.Versions;
        ModsNav.IsChecked = page == PageKind.Mods;
        (HeaderTitle.Text, HeaderSubtitle.Text) = page switch
        {
            PageKind.Home => ("首页", "准备开始今天的方块冒险"),
            PageKind.MyVersions => ("我的版本", "管理已安装的游戏与 Mod 版本"),
            PageKind.Versions => ("版本管理", "下载官方版本与 Mod 加载器"),
            PageKind.Mods => ("资源中心", "从 Modrinth 与 CurseForge 发现游戏资源"),
            PageKind.Accounts => ("账户", "管理 Microsoft 与离线档案"),
            _ => ("设置", "调整外观、性能、存储与启动行为")
        };
        if (page == PageKind.Mods && _initialized) _ = EnsureModsLoadedAsync();
        if (page == PageKind.Versions && _operationCts is null)
            UpdateSelectedVersionStatus();
    }

    private FrameworkElement PageFor(PageKind page) => page switch
    {
        PageKind.Home => HomePage,
        PageKind.MyVersions => MyVersionsPage,
        PageKind.Versions => VersionsPage,
        PageKind.Mods => ModsPage,
        PageKind.Accounts => AccountsPage,
        _ => SettingsPage
    };

    private void AnimatePageTransition(
        FrameworkElement target,
        FrameworkElement? previous,
        int version)
    {
        var fadeInDuration = TimeSpan.FromMilliseconds(230);
        var fadeOutDuration = TimeSpan.FromMilliseconds(170);
        target.SetResourceReference(Panel.BackgroundProperty, "BgBrush");
        var fadeIn = new DoubleAnimation(0, 1, fadeInDuration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        fadeIn.Completed += (_, _) =>
        {
            if (version != _pageTransitionVersion) return;
            target.ClearValue(Panel.BackgroundProperty);
        };
        target.BeginAnimation(OpacityProperty, fadeIn);

        if (previous is null) return;
        previous.Opacity = 0;
        var fadeOut = new DoubleAnimation(1, 0, fadeOutDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop
        };
        fadeOut.Completed += (_, _) =>
        {
            if (version != _pageTransitionVersion ||
                ReferenceEquals(previous, target) ||
                previous.Visibility != Visibility.Visible)
                return;
            previous.Visibility = Visibility.Collapsed;
            previous.Opacity = 1;
        };
        previous.BeginAnimation(OpacityProperty, fadeOut);
    }

    private async void MicrosoftLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_authCts is not null) return;
        try
        {
            _authCts = new CancellationTokenSource();
            _deviceCodeSession = await _auth.StartDeviceCodeAsync(_authCts.Token);
            DeviceCodeText.Text = _deviceCodeSession.UserCode;
            AuthOverlayStatus.Text = "等待你在浏览器中完成登录…";
            ShowOverlayWindow(
                AuthOverlay,
                "登录 Microsoft",
                () => CancelAuth_Click(this, new RoutedEventArgs()));
            OpenBrowser(_deviceCodeSession.VerificationUri);
            try { Clipboard.SetText(_deviceCodeSession.UserCode); }
            catch { /* Clipboard can be locked by another process. */ }

            var status = new Progress<string>(text => AuthOverlayStatus.Text = text);
            var loggedInAccount = await _auth.CompleteDeviceCodeAsync(_deviceCodeSession,
                text => ((IProgress<string>)status).Report(text),
                _authCts.Token);
            var existing = _accounts.FirstOrDefault(x =>
                x.Kind == AccountKind.Microsoft &&
                x.MinecraftUuid.Equals(loggedInAccount.MinecraftUuid, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.Name = loggedInAccount.Name;
                existing.Xuid = loggedInAccount.Xuid;
                existing.SkinUrl = loggedInAccount.SkinUrl;
                existing.AccessToken = loggedInAccount.AccessToken;
                existing.ExpiresAt = loggedInAccount.ExpiresAt;
                existing.RefreshTokenProtected = loggedInAccount.RefreshTokenProtected;
                Settings.ActiveAccountId = existing.Id;
            }
            else
            {
                _accounts.Add(loggedInAccount);
                Settings.ActiveAccountId = loggedInAccount.Id;
            }
            PersistConfig();
            UpdateAccountUi();
            _ = RefreshPlayerAvatarsAsync();
            HideOverlayWindow(AuthOverlay);
            GlobalStatusText.Text = $"已登录为 {loggedInAccount.Name}";
            LogService.Info($"Microsoft 账户 {loggedInAccount.Name} 登录成功");
        }
        catch (OperationCanceledException)
        {
            HideOverlayWindow(AuthOverlay);
            GlobalStatusText.Text = "已取消 Microsoft 登录";
        }
        catch (Exception ex)
        {
            AuthOverlayStatus.Text = "登录失败：" + ex.Message;
            LogService.Error("Microsoft 登录失败", ex);
        }
        finally
        {
            _authCts?.Dispose();
            _authCts = null;
            _deviceCodeSession = null;
        }
    }

    private void CancelAuth_Click(object sender, RoutedEventArgs e)
    {
        var wasActive = _authCts is not null;
        _authCts?.Cancel();
        HideOverlayWindow(AuthOverlay);
        GlobalStatusText.Text = wasActive ? "已取消 Microsoft 登录" : "已关闭登录窗口";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (IsOverlayOpen(ProfileCustomizationOverlay))
        {
            CloseProfileCustomization_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (IsOverlayOpen(AuthOverlay))
        {
            CancelAuth_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (IsOverlayOpen(ConfirmOverlay))
        {
            ResolveInstallConfirmation(false);
            e.Handled = true;
        }
        else if (IsOverlayOpen(ModDetailOverlay))
        {
            CloseModDetails_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void CopyDeviceCode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DeviceCodeText.Text);
            AuthOverlayStatus.Text = "设备码已复制";
        }
        catch
        {
            AuthOverlayStatus.Text = "无法访问剪贴板，请手动选择设备码";
        }
    }

    private void OpenAuthPage_Click(object sender, RoutedEventArgs e)
    {
        if (_deviceCodeSession is not null) OpenBrowser(_deviceCodeSession.VerificationUri);
    }

    private static void OpenBrowser(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private void OfflineLogin_Click(object sender, RoutedEventArgs e)
    {
        var name = OfflineNameBox.Text.Trim();
        if (!Regex.IsMatch(name, @"^[\p{L}\p{N}_]{1,16}$"))
        {
            MessageBox.Show(this, "玩家名需为 1–16 个字母、数字、中文或下划线，且不能包含空格。",
                "玩家名无效", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var account = _accounts.FirstOrDefault(x =>
            x.Kind == AccountKind.Offline && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var skinModel = Enum.TryParse<OfflineSkinModel>(
            (OfflineSkinModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var selectedSkinModel)
            ? selectedSkinModel
            : OfflineSkinModel.Steve;
        if (account is null)
        {
            account = new AccountInfo
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                MinecraftUuid = GameLauncher.CreateOfflineUuid(name),
                Kind = AccountKind.Offline,
                OfflineSkinModel = skinModel,
                AccessToken = "offline"
            };
            _accounts.Add(account);
        }
        else
        {
            account.MinecraftUuid = GameLauncher.CreateOfflineUuid(name);
            account.OfflineSkinModel = skinModel;
            account.AccessToken = "offline";
        }
        Settings.ActiveAccountId = account.Id;
        PersistConfig();
        UpdateAccountUi();
        _ = RefreshPlayerAvatarsAsync();
        GlobalStatusText.Text = $"已使用离线账户 {name}";
        LogService.Info($"已创建或切换离线账户 {name}");
    }

    private void AccountCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            FindVisualAncestor<Button>(source) is not null)
            return;
        if ((sender as FrameworkElement)?.Tag is not AccountInfo account ||
            account.Id == Settings.ActiveAccountId)
            return;

        Settings.ActiveAccountId = account.Id;
        PersistConfig();
        UpdateAccountUi();
        _ = RefreshPlayerAvatarsAsync();
        GlobalStatusText.Text = $"当前账户：{account.Name}";
    }

    private async void CustomizeAccount_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not AccountInfo account) return;
        if (account.Kind != AccountKind.Microsoft)
        {
            GlobalStatusText.Text = "离线账户使用 Steve 或 Alex 默认头像，不需要上传正版皮肤。";
            return;
        }
        if (_customizationCts is not null) return;

        _customizationAccount = account;
        _customizationProfile = null;
        _pendingSkinPath = null;
        _customizationSkinTexturePath = null;
        _customizationCapeTextures =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CustomizationNameText.Text = account.Name;
        CustomizationModelText.Text = "正在读取皮肤模型…";
        CustomizationSkinFileText.Text = "尚未选择新皮肤";
        CustomizationPlayerModel.ClearAppearance();
        CustomizationPlayerModel.ResetView();
        CustomizationCapeList.ItemsSource = null;
        CustomizationCapeCountText.Text = "披风 (0)";
        CustomizationStatusText.Text = "正在读取 Minecraft 档案…";
        ShowOverlayWindow(
            ProfileCustomizationOverlay,
            "自定义外观",
            () => CloseProfileCustomization_Click(this, new RoutedEventArgs()));

        var cachedProfile = _minecraftProfile.GetCachedProfile(account);
        if (cachedProfile is not null)
        {
            try
            {
                await ApplyCustomizationProfileAsync(
                    account, cachedProfile.Profile, CancellationToken.None);
                CustomizationStatusText.Text = "已使用本地缓存。修改皮肤或披风时会验证正版账户。";
                SetCustomizationBusy(false);
                return;
            }
            catch (Exception ex)
            {
                LogService.Warn($"无法使用 Minecraft 外观缓存：{ex.Message}");
            }
        }

        SetCustomizationBusy(true);
        await RunCustomizationOperationAsync(async token =>
        {
            await EnsureMicrosoftAccountReadyAsync(account, token);
            var profile = await _minecraftProfile.GetProfileAsync(account, token);
            account.SkinUrl = profile.SkinUrl;
            await ApplyCustomizationProfileAsync(account, profile, token);
            CustomizationStatusText.Text = profile.Capes.Count == 0
                ? "档案已载入。这个账户当前没有可更换的官方披风。"
                : "档案已载入，可选择新皮肤或更换披风。";
        });
    }

    private async void BrowseCustomizationSkin_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Minecraft 皮肤",
            Filter = "Minecraft 皮肤 (*.png)|*.png",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            MinecraftProfileService.ValidateSkinFile(dialog.FileName);
            _pendingSkinPath = dialog.FileName;
            CustomizationSkinFileText.Text = Path.GetFileName(dialog.FileName);
            CustomizationStatusText.Text = "正在生成皮肤预览…";
            await UpdatePendingSkinPreviewAsync(dialog.FileName);
        }
        catch (Exception ex)
        {
            _pendingSkinPath = null;
            CustomizationSkinFileText.Text = "尚未选择新皮肤";
            CustomizationStatusText.Text = ex.Message;
        }
        UpdateCustomizationControls();
    }

    private async void CustomizationSkinModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomizationPlayerModel is null || _customizationProfile is null) return;
        try
        {
            if (string.IsNullOrWhiteSpace(_pendingSkinPath))
            {
                UpdateCustomizationModelPreview();
                return;
            }
            CustomizationStatusText.Text = "正在更新皮肤模型预览…";
            await UpdatePendingSkinPreviewAsync(_pendingSkinPath);
        }
        catch (Exception ex)
        {
            CustomizationStatusText.Text = ex.Message;
        }
    }

    private Task UpdatePendingSkinPreviewAsync(string skinPath)
    {
        if (_pendingSkinPath != skinPath) return Task.CompletedTask;
        UpdateCustomizationModelPreview(skinPath);
        CustomizationStatusText.Text = "皮肤文件有效，选择模型后即可上传。";
        return Task.CompletedTask;
    }

    private async void UploadCustomizationSkin_Click(object sender, RoutedEventArgs e)
    {
        var account = _customizationAccount;
        var skinPath = _pendingSkinPath;
        if (account is null || skinPath is null || string.IsNullOrWhiteSpace(skinPath)) return;
        var slim = string.Equals(
            (CustomizationSkinModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
            "Slim", StringComparison.OrdinalIgnoreCase);
        await RunCustomizationOperationAsync(async token =>
        {
            CustomizationStatusText.Text = "正在上传并应用皮肤…";
            await EnsureMicrosoftAccountReadyAsync(account, token);
            await _minecraftProfile.UploadSkinAsync(account, skinPath, slim, token);
            var profile = await _minecraftProfile.GetProfileAsync(account, token);
            account.SkinUrl = profile.SkinUrl;
            await RefreshPlayerAvatarsAsync();
            _pendingSkinPath = null;
            await ApplyCustomizationProfileAsync(account, profile, token);
            CustomizationStatusText.Text = "Minecraft 皮肤已更新。";
            GlobalStatusText.Text = $"{account.Name} 的皮肤已更新";
            LogService.Info($"已更新 Microsoft 账户 {account.Name} 的 Minecraft 皮肤");
        });
    }

    private async void ApplyCustomizationCape_Click(object sender, RoutedEventArgs e)
    {
        var account = _customizationAccount;
        if (account is null || CustomizationCapeList.SelectedItem is not MinecraftCapeChoice choice) return;
        await RunCustomizationOperationAsync(async token =>
        {
            CustomizationStatusText.Text = choice.Id is null ? "正在移除披风…" : "正在更换披风…";
            await EnsureMicrosoftAccountReadyAsync(account, token);
            if (choice.Id is null)
                await _minecraftProfile.ClearCapeAsync(account, token);
            else
                await _minecraftProfile.SetCapeAsync(account, choice.Id, token);
            var profile = await _minecraftProfile.GetProfileAsync(account, token);
            account.SkinUrl = profile.SkinUrl;
            await ApplyCustomizationProfileAsync(account, profile, token);
            CustomizationStatusText.Text = choice.Id is null ? "已移除当前披风。" : $"已应用“{choice.Name}”。";
            GlobalStatusText.Text = $"{account.Name} 的披风设置已更新";
            LogService.Info($"已更新 Microsoft 账户 {account.Name} 的 Minecraft 披风");
        });
    }

    private void CustomizationCapeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_customizationAccount is null || _customizationProfile is null ||
            CustomizationPlayerModel is null ||
            CustomizationApplyCapeButton is null) return;
        if (CustomizationCapeList.SelectedItem is not MinecraftCapeChoice)
        {
            UpdateCustomizationModelPreview();
            UpdateCustomizationControls();
            return;
        }
        UpdateCustomizationModelPreview();
        UpdateCustomizationControls();
    }

    private void ProfileCustomizationContent_PreviewMouseWheel(
        object sender,
        MouseWheelEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ReferenceEquals(FindVisualAncestor<ListBox>(source), CustomizationCapeList))
            return;
        if (SmoothScroll.ScrollByWheel(ProfileCustomizationScrollViewer, e.Delta))
            e.Handled = true;
    }

    private void CustomizationPlayerModel_WheelScrolled(object? sender, int delta)
    {
        if (IsOverlayOpen(ProfileCustomizationOverlay))
            SmoothScroll.ScrollByWheel(ProfileCustomizationScrollViewer, delta);
    }

    private void UpdateCustomizationModelPreview(string? localSkinPath = null)
    {
        var slim = string.Equals(
            (CustomizationSkinModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
            "Slim", StringComparison.OrdinalIgnoreCase);
        var capePath = CustomizationCapeList.SelectedItem is MinecraftCapeChoice { Id: { } capeId } &&
                       _customizationCapeTextures.TryGetValue(capeId, out var texturePath)
            ? texturePath
            : null;
        CustomizationPlayerModel.SetAppearance(
            localSkinPath ?? _customizationSkinTexturePath,
            capePath,
            slim);
    }

    private async Task RunCustomizationOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (_customizationCts is not null) return;
        var cancellation = new CancellationTokenSource();
        _customizationCts = cancellation;
        SetCustomizationBusy(true);
        try
        {
            await operation(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Closing the dialog cancels pending profile requests.
        }
        catch (Exception ex)
        {
            if (IsOverlayOpen(ProfileCustomizationOverlay))
                CustomizationStatusText.Text = ex switch
                {
                    MicrosoftAuthException => "正版登录需要重新验证，请稍后重试。",
                    System.Net.Http.HttpRequestException or IOException or TimeoutException or
                        System.Net.Sockets.SocketException =>
                        "网络连接失败，请检查网络后重试。",
                    _ => "操作失败：" + ex.Message
                };
            LogService.Error("自定义 Minecraft 外观失败", ex);
        }
        finally
        {
            if (ReferenceEquals(_customizationCts, cancellation)) _customizationCts = null;
            cancellation.Dispose();
            SetCustomizationBusy(false);
        }
    }

    private void SetCustomizationBusy(bool busy)
    {
        _customizationBusy = busy;
        CustomizationProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateCustomizationControls();
    }

    private void UpdateCustomizationControls()
    {
        var ready = !_customizationBusy && _customizationAccount is not null &&
                    _customizationProfile is not null;
        CustomizationBrowseSkinButton.IsEnabled = ready;
        CustomizationSkinModelCombo.IsEnabled = ready;
        CustomizationUploadSkinButton.IsEnabled =
            ready && _pendingSkinPath is not null && File.Exists(_pendingSkinPath);
        CustomizationCapeList.IsEnabled = true;
        CustomizationCapeList.IsHitTestVisible = ready;
        CustomizationApplyCapeButton.IsEnabled =
            ready && CustomizationCapeList.SelectedItem is MinecraftCapeChoice;
    }

    private async Task ApplyCustomizationProfileAsync(
        AccountInfo account,
        MinecraftProfileSnapshot profile,
        CancellationToken cancellationToken)
    {
        var assets = await _appearancePreviews.CreateAsync(profile, cancellationToken);
        _customizationSkinTexturePath = assets.SkinPath;
        _customizationCapeTextures = assets.CapeTextures;
        _customizationProfile = profile;
        account.SkinUrl = profile.SkinUrl;
        CustomizationNameText.Text = profile.Name;
        var slim = profile.SkinVariant.Equals("SLIM", StringComparison.OrdinalIgnoreCase);
        CustomizationModelText.Text = slim ? "纤细（Alex）皮肤模型" : "经典（Steve）皮肤模型";
        CustomizationSkinModelCombo.SelectedValue = slim ? "Slim" : "Classic";
        CustomizationSkinFileText.Text = "尚未选择新皮肤";

        var choices = new List<MinecraftCapeChoice>
        {
            new(null, "无披风", null)
        };
        choices.AddRange(profile.Capes.Select(cape =>
        {
            assets.CapeTextures.TryGetValue(cape.Id, out var texturePath);
            return new MinecraftCapeChoice(
                cape.Id,
                DisplayCapeName(cape.Alias),
                texturePath);
        }));
        CustomizationCapeCountText.Text = $"披风 ({profile.Capes.Count})";
        CustomizationCapeList.ItemsSource = choices;
        var activeCapeId = profile.Capes.FirstOrDefault(cape => cape.IsActive)?.Id;
        CustomizationCapeList.SelectedItem =
            choices.FirstOrDefault(choice => choice.Id == activeCapeId) ?? choices[0];
        UpdateCustomizationControls();
    }

    private static string DisplayCapeName(string alias) => alias.ToLowerInvariant() switch
    {
        "vanilla" => "原版披风",
        "migrator" => "迁移者披风",
        "realms mapmaker" => "Realms 地图制作者披风",
        "founder's" => "创始人披风",
        "mojang" => "Mojang 披风",
        "translator" => "译者披风",
        "turtle" => "海龟披风",
        "pan" => "煎饼披风",
        "minecon 2011" => "MineCon 2011 披风",
        "minecon 2012" => "MineCon 2012 披风",
        "minecon 2013" => "MineCon 2013 披风",
        "minecon 2015" => "MineCon 2015 披风",
        "minecon 2016" => "MineCon 2016 披风",
        _ => alias
    };

    private async Task EnsureMicrosoftAccountReadyAsync(AccountInfo account, CancellationToken cancellationToken)
    {
        if (account.Kind != AccountKind.Microsoft)
            throw new MinecraftProfileException("只有 Microsoft 正版账户可以修改官方皮肤和披风。");
        if (!string.IsNullOrWhiteSpace(account.AccessToken) &&
            (account.ExpiresAt is null || account.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)))
            return;

        var refreshed = await _auth.RefreshAsync(account, cancellationToken);
        account.Name = refreshed.Name;
        account.MinecraftUuid = refreshed.MinecraftUuid;
        account.Xuid = refreshed.Xuid;
        account.SkinUrl = refreshed.SkinUrl;
        account.AccessToken = refreshed.AccessToken;
        account.ExpiresAt = refreshed.ExpiresAt;
        account.RefreshTokenProtected = refreshed.RefreshTokenProtected;
        PersistConfig();
        UpdateAccountUi();
    }

    private void CloseProfileCustomization_Click(object sender, RoutedEventArgs e)
    {
        _customizationCts?.Cancel();
        _customizationAccount = null;
        _customizationProfile = null;
        _pendingSkinPath = null;
        _customizationSkinTexturePath = null;
        _customizationCapeTextures =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        HideOverlayWindow(ProfileCustomizationOverlay);
    }

    private void ResetProfileCustomizationContent()
    {
        CustomizationCapeList.ItemsSource = null;
        CustomizationCapeCountText.Text = "披风 (0)";
        CustomizationPlayerModel.ClearAppearance();
        UpdateCustomizationControls();
    }

    private void RemoveAccount_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not AccountInfo account) return;
        if (MessageBox.Show(this, $"确定移除账户“{account.Name}”吗？\n仅删除本机保存的登录信息。",
                "移除账户", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _accounts.Remove(account);
        if (Settings.ActiveAccountId == account.Id)
            Settings.ActiveAccountId = _accounts.FirstOrDefault()?.Id;
        PersistConfig();
        UpdateAccountUi();
        _ = RefreshPlayerAvatarsAsync();
    }

    private void UpdateAccountUi()
    {
        var account = ActiveAccount;
        foreach (var item in _accounts) item.IsActive = item.Id == account?.Id;
        HeaderAccountInitial.Text = account?.Initial ?? "?";
        HeaderAccountName.Text = account?.Name ?? "未登录";
        HeaderAccountKind.Text = account?.KindLabel ?? "点击添加账户";
        AccountCountText.Text = _accounts.Count.ToString();
        AccountsTotalText.Text = $"{_accounts.Count} 个账户";
        OfflineNameBox.Text = account?.Kind == AccountKind.Offline ? account.Name : "";
        OfflineSkinModelCombo.SelectedValue =
            (account?.Kind == AccountKind.Offline ? account.OfflineSkinModel : OfflineSkinModel.Steve).ToString();
        UpdatePlayerAvatarControls();
        UpdateLaunchState();
    }

    private void PersistConfig()
    {
        Settings.Accounts = _accounts.ToList();
        try
        {
            _settingsService.Save();
        }
        catch (Exception ex)
        {
            LogService.Warn($"保存设置失败：{ex.Message}");
        }
    }

    private async Task RefreshPlayerAvatarsAsync()
    {
        _avatarCts?.Cancel();
        var cancellation = new CancellationTokenSource();
        _avatarCts = cancellation;
        using var gate = new SemaphoreSlim(3);

        try
        {
            await Task.WhenAll(_accounts.Select(async account =>
            {
                await gate.WaitAsync(cancellation.Token);
                try
                {
                    var path = await _playerAvatars.GetAvatarAsync(account, cancellation.Token);
                    if (!cancellation.IsCancellationRequested)
                        account.AvatarImagePath = path;
                }
                finally
                {
                    gate.Release();
                }
            }));

            if (cancellation.IsCancellationRequested) return;
            UpdatePlayerAvatarControls();
        }
        catch (OperationCanceledException)
        {
            // A newer account update superseded this refresh.
        }
        catch (Exception ex)
        {
            LogService.Warn($"玩家头像加载失败：{ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_avatarCts, cancellation)) _avatarCts = null;
            cancellation.Dispose();
        }
    }

    private void UpdatePlayerAvatarControls()
    {
        var account = ActiveAccount;
        SetAvatar(HeaderAvatarImage, HeaderAccountInitial, account?.AvatarImagePath);
        UpdateHomeAccountPreview();
    }

    private void UpdateHomeAccountPreview()
    {
        var account = ActiveAccount;
        HomeAccountInitial.Text = account?.Initial ?? "?";
        HomeAccountName.Text = account?.Name ?? "未登录";
        HomeAccountKind.Text = account?.KindLabel ?? "离线或正版账户";
        SetAvatar(HomeAvatarImage, HomeAccountInitial, account?.AvatarImagePath);

        var multipleAccounts = _accounts.Count > 1;
        var arrowsVisibility = multipleAccounts
            ? Visibility.Visible
            : Visibility.Collapsed;
        HomePreviousAccountButton.Visibility = arrowsVisibility;
        HomeNextAccountButton.Visibility = arrowsVisibility;
    }

    private static void SetAvatar(Image image, TextBlock fallback, string? path)
    {
        image.Source = string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : LoadBitmap(path);
        image.Visibility = image.Source is null ? Visibility.Collapsed : Visibility.Visible;
        fallback.Visibility = image.Source is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private async void ContentKindFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not RadioButton option ||
            !Enum.TryParse<ContentKind>(option.Tag?.ToString(), out var contentKind) ||
            _activeContentKind == contentKind)
            return;

        var previousSelection = ModsVersionCombo.SelectedItem as InstalledVersionInfo;
        _activeContentKind = contentKind;
        ConfigureModVersionFilterSource(
            previousSelection?.Id, previousSelection?.BaseVersion);
        _modSearchCts?.Cancel();
        _modsLoadedKey = null;
        _modsLoadingKey = null;
        _modHits.Clear();
        _modTotal = 0;
        _modOffset = 0;
        UpdateContentKindLabels();
        UpdateModAvailability();
        await SelectModSourceAsync();
        await SearchModsAsync(true);
    }

    private void ConfigureModVersionFilterSource(
        string? preferredId = null,
        string? preferredBase = null)
    {
        if (ModsVersionCombo is null) return;
        var current = ModsVersionCombo.SelectedItem as InstalledVersionInfo;
        preferredId ??= current?.Id;
        preferredBase ??= current?.BaseVersion;
        var source = _activeContentKind == ContentKind.Modpack
            ? _modpackVersionFilters
            : _modVersions;
        var wasSyncing = _syncingSelections;
        _syncingSelections = true;
        try
        {
            ModsVersionCombo.ItemsSource = source;
            ModsVersionCombo.SelectedItem = _modFilterCleared
                ? null
                : source.FirstOrDefault(item =>
                      !string.IsNullOrWhiteSpace(preferredId) &&
                      item.Id.Equals(preferredId, StringComparison.OrdinalIgnoreCase))
                  ?? source.FirstOrDefault(item =>
                      !string.IsNullOrWhiteSpace(preferredBase) &&
                      (item.BaseVersion.Equals(preferredBase, StringComparison.OrdinalIgnoreCase) ||
                       item.Id.Equals(preferredBase, StringComparison.OrdinalIgnoreCase)))
                  ?? source.FirstOrDefault();
        }
        finally
        {
            _syncingSelections = wasSyncing;
        }
        UpdateModFilterAvailability();
    }

    private void RebuildModpackVersionFilters()
    {
        var selected = _activeContentKind == ContentKind.Modpack
            ? ModsVersionCombo.SelectedItem as InstalledVersionInfo
            : null;
        _syncingSelections = true;
        try
        {
            _modpackVersionFilters.Clear();
            foreach (var version in BuildModpackVersionFilters(
                         _remoteVersions, _modpackVersionType))
                _modpackVersionFilters.Add(version);
        }
        finally
        {
            _syncingSelections = false;
        }
        if (_activeContentKind == ContentKind.Modpack)
            ConfigureModVersionFilterSource(selected?.Id, selected?.BaseVersion);
        else
            UpdateModFilterAvailability();
    }

    internal static IReadOnlyList<InstalledVersionInfo> BuildModpackVersionFilters(
        IEnumerable<RemoteVersion> versions,
        string? versionType = null) =>
        versions
            .Where(version => string.IsNullOrWhiteSpace(versionType) ||
                              version.Type.Equals(versionType, StringComparison.OrdinalIgnoreCase))
            .Select(version => new InstalledVersionInfo
        {
            Id = version.Id,
            BaseVersion = version.Id,
            Loader = ModLoader.Vanilla,
            DisplayName = version.Id,
            FilterTypeLabel = version.TypeLabel,
            Ready = false
        }).ToList();

    private void UpdateContentKindLabels()
    {
        var label = ContentKindLabel(_activeContentKind);
        if (ModSearchLabel is null) return;
        var modpackMode = _activeContentKind == ContentKind.Modpack;
        var loaderMode = _activeContentKind is ContentKind.Mod or ContentKind.Modpack;
        ModsLoaderLabel.Visibility = loaderMode ? Visibility.Visible : Visibility.Collapsed;
        ModsLoaderCombo.Visibility = loaderMode ? Visibility.Visible : Visibility.Collapsed;
        ModsVersionTypeLabel.Visibility =
            modpackMode ? Visibility.Visible : Visibility.Collapsed;
        ModsVersionTypeCombo.Visibility =
            modpackMode ? Visibility.Visible : Visibility.Collapsed;
        UpdateModFilterAvailability();
        ModSearchLabel.Text = "搜索";
        ModSearchHint.Text = $"可选：输入关键词筛选热门{label}";
        ModDetailDescriptionLabel.Text = $"{label}介绍";
        ModTargetTitleText.Text = $"选择{label}下载目标";
        if (_modHits.Count == 0)
        {
            ModsEmptyTitle.Text = $"探索{label}";
            ModsEmptyDescription.Text = $"当前版本的热门{label}会自动加载，也可以输入关键词筛选。";
        }
        UpdateSearchHints();
    }

    private void UpdateModFilterAvailability()
    {
        if (ModsVersionCombo is null) return;
        SetComboBoxAvailability(ModsVersionCombo, ModsVersionCombo.Items.Count > 0);
        SetComboBoxAvailability(
            ModsLoaderCombo,
            _activeContentKind is ContentKind.Mod or ContentKind.Modpack &&
            ModsLoaderCombo.Items.Count > 0);
        SetComboBoxAvailability(
            ModsVersionTypeCombo,
            _activeContentKind == ContentKind.Modpack && _remoteVersions.Count > 0);
    }

    private static void SetComboBoxAvailability(ComboBox comboBox, bool available)
    {
        if (!available && comboBox.IsDropDownOpen)
            comboBox.IsDropDownOpen = false;
        comboBox.IsEnabled = available;
    }

    private async void ModsVersionTypeCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingSelections || !_initialized ||
            _activeContentKind != ContentKind.Modpack)
            return;
        _modpackVersionType =
            (ModsVersionTypeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        _modFilterCleared = false;
        RebuildModpackVersionFilters();
        UpdateModAvailability();
        await SelectModSourceAsync();
        await SearchModsAsync(true);
    }

    private async void ModsLoaderCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingSelections || !_initialized ||
            _activeContentKind is not (ContentKind.Mod or ContentKind.Modpack))
            return;
        UpdateModAvailability();
        if (ModsPage.Visibility != Visibility.Visible) return;
        await SelectModSourceAsync();
        await SearchModsAsync(true);
    }

    private static string ContentKindLabel(ContentKind contentKind) => contentKind switch
    {
        ContentKind.Modpack => "整合包",
        ContentKind.DataPack => "数据包",
        ContentKind.ResourcePack => "资源包",
        ContentKind.Shader => "光影包",
        _ => "模组"
    };

    private async void ModsVersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelections || !_initialized) return;
        if (ModsVersionCombo.SelectedItem is InstalledVersionInfo) _modFilterCleared = false;
        UpdateModAvailability();
        if (ModsPage.Visibility != Visibility.Visible) return;
        await SelectModSourceAsync();
        await SearchModsAsync(true);
    }

    private void ModsVersionCombo_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ComboBox comboBox || comboBox.IsDropDownOpen ||
            comboBox.Items.Count == 0 || e.Delta == 0)
            return;
        var direction = e.Delta > 0 ? -1 : 1;
        var current = comboBox.SelectedIndex;
        comboBox.SelectedIndex = current < 0
            ? direction > 0 ? 0 : comboBox.Items.Count - 1
            : Math.Clamp(current + direction, 0, comboBox.Items.Count - 1);
        e.Handled = true;
    }

    private async void ClearModFilter_Click(object sender, RoutedEventArgs e)
    {
        _modFilterCleared = true;
        _modpackVersionType = null;
        _syncingSelections = true;
        ModsLoaderCombo.SelectedIndex = 0;
        ModsVersionTypeCombo.SelectedItem = null;
        ModsVersionCombo.SelectedItem = null;
        _syncingSelections = false;
        RebuildModpackVersionFilters();
        UpdateModAvailability();
        await SelectModSourceAsync();
        await SearchModsAsync(true);
    }

    private async Task SelectModSourceAsync()
    {
        if (GetModCatalogTarget() is null) return;
        var selection = await _modSourceSpeedTest.SelectFastestAsync();
        _activeModSource = selection.Source;
        ModSearchLabel.Text = "搜索";
    }

    private ModCatalogTarget? GetModCatalogTarget()
    {
        var home = HomeVersionCombo.SelectedItem as InstalledVersionInfo;
        if (_modFilterCleared)
        {
            var loader = ResolveContentLoader(null, home);
            return new ModCatalogTarget(
                "", loader, $"全部 Minecraft 版本 · {ContentLoaderLabel(loader)}",
                _activeContentKind);
        }

        if (ModsVersionCombo.SelectedItem is InstalledVersionInfo selected &&
            (_activeContentKind != ContentKind.Mod || selected.SupportsMods))
        {
            var loader = ResolveContentLoader(selected, home);
            return new ModCatalogTarget(
                selected.BaseVersion, loader,
                $"Minecraft {selected.BaseVersion} · {ContentLoaderLabel(loader)}",
                _activeContentKind);
        }

        if (home is not null)
        {
            var baseVersion = string.IsNullOrWhiteSpace(home.BaseVersion)
                ? home.Id
                : home.BaseVersion;
            var loader = ResolveContentLoader(null, home);
            return new ModCatalogTarget(
                baseVersion, loader,
                $"Minecraft {baseVersion} · {ContentLoaderLabel(loader)}",
                _activeContentKind);
        }

        var fallbackVersion = !string.IsNullOrWhiteSpace(_loaderTargetVersion)
            ? _loaderTargetVersion
            : _remoteVersions.FirstOrDefault(version => version.IsLatestRelease)?.Id
              ?? _remoteVersions.FirstOrDefault(version => version.Type == "release")?.Id
              ?? _remoteVersions.FirstOrDefault()?.Id;
        return string.IsNullOrWhiteSpace(fallbackVersion)
            ? null
            : new ModCatalogTarget(
                fallbackVersion, ResolveContentLoader(null, null),
                $"Minecraft {fallbackVersion}", _activeContentKind);
    }

    private ModLoader ResolveContentLoader(
        InstalledVersionInfo? selected,
        InstalledVersionInfo? home)
    {
        var explicitChoice =
            (ModsLoaderCombo.SelectedItem as LoaderChoiceOption)?.Loader ??
            ModLoader.Vanilla;
        if (explicitChoice != ModLoader.Vanilla)
            return explicitChoice;
        if (_activeContentKind == ContentKind.Modpack)
            return ModLoader.Vanilla;
        if (selected is { SupportsMods: true })
            return selected.Loader;
        if (home is { SupportsMods: true })
            return home.Loader;
        return ModLoader.Fabric;
    }

    private string ContentLoaderLabel(ModLoader loader) =>
        _activeContentKind == ContentKind.Modpack && loader == ModLoader.Vanilla
            ? "全部加载器"
            : LoaderName(loader);

    private void UpdateModAvailability()
    {
        UpdateModFilterAvailability();
        var selected = ModsVersionCombo.SelectedItem as InstalledVersionInfo;
        var target = GetModCatalogTarget();
        if (target is null)
        {
            _modSearchCts?.Cancel();
            _modsLoadedKey = null;
            _modsLoadingKey = null;
            _modHits.Clear();
            _modTotal = 0;
            ModResultCountText.Text = "";
            SearchModsButton.IsEnabled = false;
            ModSearchBox.IsEnabled = false;
            ModTargetText.Text = "正在读取 Minecraft 版本清单…";
            ModsEmptyState.Visibility = Visibility.Visible;
            ModsEmptyTitle.Text = $"暂时无法浏览{ContentKindLabel(_activeContentKind)}";
            ModsEmptyDescription.Text = "请先刷新 Minecraft 版本清单。";
            GoToVersionsButton.Visibility = Visibility.Collapsed;
            UpdateModPager();
            return;
        }
        SearchModsButton.IsEnabled = true;
        ModSearchBox.IsEnabled = true;
        ModTargetText.Text = selected is null
            ? $"{target.DisplayLabel} · 下载时可选择目标游戏版本"
            : _activeContentKind is ContentKind.Mod or ContentKind.Modpack
                ? target.DisplayLabel
            : _activeContentKind is ContentKind.DataPack or ContentKind.ResourcePack or
                ContentKind.Shader
                ? $"Minecraft {selected.BaseVersion} · {selected.Id}"
                : $"Minecraft {selected.BaseVersion} · {selected.LoaderLabel} · {selected.Id}";
        GoToVersionsButton.Visibility = Visibility.Collapsed;
        if (_modHits.Count == 0)
        {
            ModsEmptyState.Visibility = Visibility.Visible;
            var contentLabel = ContentKindLabel(_activeContentKind);
            ModsEmptyTitle.Text =
                $"探索{contentLabel} · {(_activeModSource == ModSource.CurseForge ? "CurseForge" : "Modrinth")}";
            ModsEmptyDescription.Text =
                $"当前版本的热门{contentLabel}会自动加载，也可以输入关键词筛选。";
        }
        UpdateModPager();
    }

    private async void SearchMods_Click(object sender, RoutedEventArgs e)
    {
        await SelectModSourceAsync();
        await SearchModsAsync(true);
    }

    private async Task EnsureModsLoadedAsync()
    {
        var target = GetModCatalogTarget();
        if (target is null)
        {
            UpdateModAvailability();
            return;
        }

        await SelectModSourceAsync();
        var source = _activeModSource;
        var key = BuildModsLoadKey(source, target, ModSearchBox.Text);
        if (string.Equals(_modsLoadedKey, key, StringComparison.Ordinal) ||
            string.Equals(_modsLoadingKey, key, StringComparison.Ordinal))
            return;
        await SearchModsAsync(true);
    }

    private async Task SearchModsAsync(bool reset)
    {
        var source = _activeModSource;
        var target = GetModCatalogTarget();
        var query = ModSearchBox.Text.Trim();
        if (target is null)
        {
            UpdateModAvailability();
            return;
        }
        var contentKind = target.ContentKind;
        var contentLabel = ContentKindLabel(contentKind);
        var loadKey = BuildModsLoadKey(source, target, query);
        if (reset)
        {
            _modOffset = 0;
            _modHits.Clear();
            _modTotal = 0;
            UpdateModPager();
            ModsEmptyState.Visibility = Visibility.Visible;
            ModsEmptyTitle.Text = $"正在加载{contentLabel}";
            ModsEmptyDescription.Text = source == ModSource.CurseForge
                ? $"正在整理 CurseForge 热门{contentLabel}与中文说明…"
                : $"正在获取当前版本可用的热门{contentLabel}…";
            GoToVersionsButton.Visibility = Visibility.Collapsed;
            ModResultCountText.Text = "正在加载…";
        }
        _modsLoadingKey = loadKey;
        _modSearchCts?.Cancel();
        _modSearchCts?.Dispose();
        _modSearchCts = new CancellationTokenSource();
        var token = _modSearchCts.Token;
        try
        {
            GlobalStatusText.Text = source == ModSource.CurseForge
                ? "正在搜索 CurseForge…"
                : "正在搜索 Modrinth…";
            var result = await SearchModSourceAsync(
                source, query, target, _modOffset, ModPageSize, token);
            var supplemented = 0;
            if (_modOffset == 0 && result.Hits.Count < ModPageSize)
            {
                var secondarySource = source == ModSource.CurseForge
                    ? ModSource.Modrinth
                    : ModSource.CurseForge;
                ModSearchResult? secondary = null;
                try
                {
                    secondary = await SearchModSourceAsync(
                        secondarySource, query, target, 0, ModPageSize, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogService.Warn($"备用内容来源补齐失败：{ex.Message}");
                }

                if (secondary is not null)
                {
                    if (secondary.TotalHits > result.TotalHits && secondary.Hits.Count > 0)
                    {
                        source = secondarySource;
                        _activeModSource = source;
                        result = secondary;
                        loadKey = BuildModsLoadKey(source, target, query);
                        _modsLoadingKey = loadKey;
                        ModSearchLabel.Text = "搜索";
                    }
                    else
                    {
                        var combined = result.Hits.ToList();
                        var seenProjects = new HashSet<string>(
                            combined.Select(hit => hit.ProjectId), StringComparer.OrdinalIgnoreCase);
                        var seenTitles = new HashSet<string>(
                            combined.Select(hit => McmodService.NormalizeTitle(hit.Title)),
                            StringComparer.Ordinal);
                        foreach (var hit in secondary.Hits)
                        {
                            if (combined.Count >= ModPageSize) break;
                            if (!seenProjects.Add(hit.ProjectId) ||
                                !seenTitles.Add(McmodService.NormalizeTitle(hit.Title)))
                                continue;
                            combined.Add(hit);
                            supplemented++;
                        }

                        if (supplemented > 0)
                        {
                            result = new ModSearchResult(
                                result.Offset,
                                result.Limit,
                                Math.Max(result.TotalHits, combined.Count),
                                combined);
                        }
                    }
                }
            }
            if (token.IsCancellationRequested) return;
            _modTotal = result.TotalHits;
            _modHits.Clear();
            foreach (var hit in result.Hits) _modHits.Add(hit);
            _ = LoadModCoversAsync(result.Hits);
            _ = _mcmod.EnrichMissingDescriptionsAsync(result.Hits, token);
            ModsEmptyState.Visibility = _modHits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_modHits.Count == 0)
            {
                ModsEmptyTitle.Text = $"没有找到匹配的{contentLabel}";
                ModsEmptyDescription.Text =
                    $"尝试缩短关键词，或确认目标 Minecraft 游戏版本{(contentKind is ContentKind.Mod or ContentKind.Modpack ? "与加载器" : "")}。";
            }
            ModResultCountText.Text = $"{_modTotal:N0} 个兼容{contentLabel}";
            var from = _modTotal == 0 ? 0 : _modOffset + 1;
            var to = Math.Min(_modOffset + ModPageSize, _modTotal);
            ModsPageText.Text = $"{from}–{to} / {_modTotal:N0}";
            UpdateModPager();
            GlobalStatusText.Text = $"找到 {_modTotal:N0} 个兼容{contentLabel}";
            _modsLoadedKey = loadKey;
        }
        catch (OperationCanceledException)
        {
            // A new search superseded this request.
        }
        catch (Exception ex)
        {
            LogService.Error($"搜索{contentLabel}失败", ex);
            GlobalStatusText.Text =
                (source == ModSource.CurseForge ? "CurseForge 搜索失败：" : "Modrinth 搜索失败：") +
                ex.Message;
            if (_modHits.Count == 0)
            {
                _modTotal = 0;
                UpdateModPager();
                ModsEmptyState.Visibility = Visibility.Visible;
                ModsEmptyTitle.Text = source == ModSource.CurseForge
                    ? "无法连接 CurseForge"
                    : "无法连接 Modrinth";
                ModsEmptyDescription.Text = ex.Message;
            }
        }
        finally
        {
            if (string.Equals(_modsLoadingKey, loadKey, StringComparison.Ordinal))
                _modsLoadingKey = null;
        }
    }

    private async Task<ModSearchResult> SearchModSourceAsync(
        ModSource source,
        string query,
        ModCatalogTarget target,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        if (source == ModSource.CurseForge)
        {
            var curseResult = await _curseForge.SearchAsync(
                query,
                string.IsNullOrWhiteSpace(target.BaseVersion) ? null : target.BaseVersion,
                target.Loader, offset, limit, cancellationToken, target.ContentKind);
            await EnrichFromMcmodAsync(curseResult.Hits, query, cancellationToken);
            return curseResult;
        }

        var result = await _modrinth.SearchAsync(
            query,
            string.IsNullOrWhiteSpace(target.BaseVersion) ? null : target.BaseVersion,
            target.Loader, offset, limit, cancellationToken, target.ContentKind);
        await EnrichFromMcmodAsync(result.Hits, query, cancellationToken);
        return result;
    }

    private async Task EnrichFromMcmodAsync(
        IReadOnlyList<ModSearchHit> hits,
        string query,
        CancellationToken cancellationToken)
    {
        try
        {
            await _mcmod.EnrichModrinthHitsAsync(hits, query, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Warn($"补全 Modrinth 中文说明失败：{ex.Message}");
        }
    }

    private static string BuildModsLoadKey(
        ModSource source,
        ModCatalogTarget target,
        string query) =>
        $"{(int)source}|{(int)target.ContentKind}|{(string.IsNullOrWhiteSpace(target.BaseVersion) ? "all" : target.BaseVersion)}|{target.Loader}|{query.Trim()}";

    private async Task LoadModCoversAsync(IReadOnlyList<ModSearchHit> hits)
    {
        using var gate = new SemaphoreSlim(6);
        await Task.WhenAll(hits.Select(async hit =>
        {
            await gate.WaitAsync();
            try
            {
                var useLightBackground = !ThemeService.IsDark;
                var path = await _coverCache.GetThemeCoverPathAsync(
                    hit.CoverUrl, useLightBackground, CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(path) &&
                    useLightBackground == !ThemeService.IsDark &&
                    _modHits.Contains(hit))
                    hit.CoverImagePath = path;
            }
            finally
            {
                gate.Release();
            }
        }));
    }

    private void ThemeService_ThemeChanged(object? sender, EventArgs e)
    {
        if (!_initialized || _modHits.Count == 0) return;
        foreach (var hit in _modHits) hit.CoverImagePath = null;
        _ = LoadModCoversAsync(_modHits.ToList());
    }

    private async void ModsPrev_Click(object sender, RoutedEventArgs e)
    {
        _modOffset = Math.Max(0, _modOffset - ModPageSize);
        await SearchModsAsync(false);
    }

    private void UpdateModPager()
    {
        var visible = _modTotal > 0;
        ModsPrevButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ModsNextButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ModsPageText.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ModsPrevButton.IsEnabled = visible && _modOffset > 0;
        ModsNextButton.IsEnabled = visible && _modOffset + ModPageSize < _modTotal;
    }

    private async void ModsNext_Click(object sender, RoutedEventArgs e)
    {
        if (_modOffset + ModPageSize >= _modTotal) return;
        _modOffset += ModPageSize;
        await SearchModsAsync(false);
    }

    private async void ModSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchModsAsync(true);
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateSearchHints();

    private async void OpenModDetails_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ModSearchHit hit) return;
        _modDetailCts?.Cancel();
        _modDetailCts?.Dispose();
        _modDetailCts = new CancellationTokenSource();
        var token = _modDetailCts.Token;
        _detailModHit = hit;
        ShowOverlayWindow(
            ModDetailOverlay,
            $"{hit.ContentKindLabel}详情",
            () => CloseModDetails_Click(this, new RoutedEventArgs()));
        ModDetailTitleText.Text = hit.Title;
        ModDetailSourceText.Text = hit.SourceLabel;
        ModDetailMetaText.Text =
            $"{hit.ContentKindLabel} · {hit.AuthorLabel} · {hit.InfoLabel}";
        ModDetailDescriptionLabel.Text = $"{hit.ContentKindLabel}介绍";
        ModDetailDescriptionText.Text = !string.IsNullOrWhiteSpace(hit.FullDescription)
            ? hit.FullDescription
            : !string.IsNullOrWhiteSpace(hit.Description)
                ? hit.Description
                : $"正在加载{hit.ContentKindLabel}介绍…";
        ModDetailInitialText.Text = hit.Initial;
        var coverPath = hit.CoverImagePath;
        ModDetailCoverImage.Source = string.IsNullOrWhiteSpace(coverPath) || !File.Exists(coverPath)
            ? null
            : LoadBitmap(coverPath);
        ModDetailInitialText.Visibility = ModDetailCoverImage.Source is null
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateModDetailLinks(hit);
        RefreshModDetailInstallState(hit);

        var linkTask = ResolveModDetailLinksAsync(hit, token);
        var descriptionTask = ResolveModDetailDescriptionAsync(hit, token);
        await Task.WhenAll(linkTask, descriptionTask);
        if (!token.IsCancellationRequested && ReferenceEquals(_detailModHit, hit))
        {
            UpdateModDetailLinks(hit);
            if (!string.IsNullOrWhiteSpace(hit.McmodUrl) &&
                !string.IsNullOrWhiteSpace(hit.Description))
            {
                hit.FullDescription = hit.Description;
                ModDetailDescriptionText.Text = hit.Description;
            }
            else if (string.IsNullOrWhiteSpace(hit.FullDescription))
                ModDetailDescriptionText.Text = string.IsNullOrWhiteSpace(hit.Description)
                    ? "暂无说明。"
                    : hit.Description;
            RefreshModDetailInstallState(hit);
        }
    }

    private async Task ResolveModDetailLinksAsync(ModSearchHit hit, CancellationToken token)
    {
        var sourceUrl = hit.CurseForgeUrl;
        var modrinthUrl = hit.ModrinthUrl;
        try
        {
            await _mcmod.ResolveHitLinksAsync(hit, token);
            hit.CurseForgeUrl = hit.ContentKind == ContentKind.Mod
                ? sourceUrl ?? hit.CurseForgeUrl
                : sourceUrl;
            hit.ModrinthUrl = hit.ContentKind == ContentKind.Mod
                ? modrinthUrl ?? hit.ModrinthUrl
                : modrinthUrl;
            if (!string.IsNullOrWhiteSpace(hit.McmodUrl) &&
                !string.IsNullOrWhiteSpace(hit.Description))
                hit.FullDescription = hit.Description;
        }
        catch (OperationCanceledException)
        {
            // The dialog was closed or another Mod was opened.
        }
        catch (Exception ex)
        {
            LogService.Warn($"解析 {hit.Title} 的项目链接失败：{ex.Message}");
        }
    }

    private async Task ResolveModDetailDescriptionAsync(ModSearchHit hit, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(hit.FullDescription)) return;
        try
        {
            var description = hit.Source == ModSource.CurseForge
                ? await _curseForge.GetProjectDescriptionAsync(hit, token)
                : await _modrinth.GetProjectDescriptionAsync(hit.ProjectId, token);
            token.ThrowIfCancellationRequested();
            hit.FullDescription = description;
            if (ReferenceEquals(_detailModHit, hit))
                ModDetailDescriptionText.Text = description;
        }
        catch (OperationCanceledException)
        {
            // The dialog was closed or another Mod was opened.
        }
        catch (Exception ex)
        {
            LogService.Warn($"读取 {hit.Title} 的完整介绍失败：{ex.Message}");
            if (ReferenceEquals(_detailModHit, hit) &&
                string.IsNullOrWhiteSpace(ModDetailDescriptionText.Text))
                ModDetailDescriptionText.Text = hit.Description;
        }
    }

    private void UpdateModDetailLinks(ModSearchHit hit)
    {
        SetModDetailLink(ModDetailMcmodButton, hit.McmodUrl);
        SetModDetailLink(ModDetailModrinthButton, hit.ModrinthUrl);
        SetModDetailLink(ModDetailCurseForgeButton, hit.CurseForgeUrl);
    }

    private static void SetModDetailLink(Button button, string? url)
    {
        button.Tag = string.IsNullOrWhiteSpace(url) ? null : url;
        button.Visibility = string.IsNullOrWhiteSpace(url) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshModDetailInstallState(ModSearchHit hit)
    {
        if (!ReferenceEquals(_detailModHit, hit)) return;
        ModDetailInstallButton.Tag = hit;
        ModDetailInstallButton.Content = hit.IsInstalling
            ? hit.PrimaryActionProgressLabel
            : hit.PrimaryActionLabel;
        ModDetailInstallButton.IsEnabled = !hit.IsInstalling;
        var target = ModsVersionCombo.SelectedItem as InstalledVersionInfo;
        ModDetailInstallStatusText.Text = target is null
            ? "下载时会选择目标游戏版本和保存位置。"
            : $"{target.DisplayLabel} · {hit.Status}";
    }

    private void CloseModDetails_Click(object sender, RoutedEventArgs e)
    {
        _modDetailCts?.Cancel();
        _modDetailCts?.Dispose();
        _modDetailCts = null;
        _detailModHit = null;
        HideOverlayWindow(ModDetailOverlay);
    }

    private void OpenModDetailLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string url && Uri.TryCreate(url, UriKind.Absolute, out _))
            OpenBrowser(url);
    }

    private async void InstallMod_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ModSearchHit hit) return;
        var contentLabel = hit.ContentKindLabel;
        var selected = ModsVersionCombo.SelectedItem as InstalledVersionInfo;
        if (hit.ContentKind == ContentKind.Modpack)
        {
            await InstallModpackFromCatalogAsync(hit, selected);
            return;
        }

        ModInstallTarget target;
        if (selected is not null)
        {
            var catalogTarget = GetModCatalogTarget();
            target = catalogTarget is not null
                ? new ModInstallTarget(catalogTarget.BaseVersion, catalogTarget.Loader)
                : new ModInstallTarget(
                    selected.BaseVersion,
                    ResolveContentLoader(selected, HomeVersionCombo.SelectedItem as InstalledVersionInfo));
        }
        else
        {
            var chosen = await ShowModInstallTargetDialogAsync(hit);
            if (chosen is null) return;
            target = chosen;
        }

        ResolvedModFile? file = null;
        string? selectedVersionId = null;
        if (hit.ContentKind == ContentKind.Mod)
        {
            IReadOnlyList<ModFileChoice> choices;
            try
            {
                choices = hit.Source == ModSource.CurseForge
                    ? await _curseForge.GetFileChoicesAsync(
                        hit, target.BaseVersion, target.Loader, CancellationToken.None)
                    : await _modrinth.GetFileChoicesAsync(
                        hit, target.BaseVersion, target.Loader, CancellationToken.None);
            }
            catch (Exception ex)
            {
                ShowError("读取模组历史版本失败", ex);
                return;
            }
            var choice = await ShowModVersionSelectionAsync(hit, choices);
            if (choice is null) return;
            selectedVersionId = choice.VersionId;
            file = new ResolvedModFile(
                choice.FileName,
                choice.Url,
                choice.Sha1,
                choice.Size,
                choice.SourceLabel);
        }
        else
        {
            try
            {
                file = hit.Source == ModSource.CurseForge
                    ? await _curseForge.ResolveCompatibleFileAsync(
                        hit, target.BaseVersion, target.Loader, CancellationToken.None)
                    : await _modrinth.ResolveCompatibleFileAsync(
                        hit, target.BaseVersion, target.Loader, CancellationToken.None);
            }
            catch (Exception ex) when (selected is null)
            {
                LogService.Warn($"筛选为空时未找到目标兼容文件，改用最新文件：{ex.Message}");
                try
                {
                    file = hit.Source == ModSource.CurseForge
                        ? await _curseForge.ResolveLatestFileAsync(
                            hit, target.Loader, CancellationToken.None)
                        : await _modrinth.ResolveLatestFileAsync(
                            hit, target.Loader, CancellationToken.None);
                    GlobalStatusText.Text = "目标版本没有兼容文件，已选择最新可下载版本";
                }
                catch (Exception fallbackError)
                {
                    ShowError($"读取{contentLabel}下载文件失败", fallbackError);
                    return;
                }
            }
            catch (Exception ex)
            {
                ShowError($"读取{contentLabel}下载文件失败", ex);
                return;
            }
        }
        if (file is null) return;

        IReadOnlyList<ModDependencyFile> dependencies = [];
        if (hit.ContentKind == ContentKind.Mod)
        {
            dependencies = await ResolveModDependenciesAsync(
                hit, target, selectedVersionId);
            if (dependencies.Count > 0)
            {
                var proceed = await ConfirmInstallAsync(
                    "下载 Mod 前置",
                    $"“{hit.Title}”需要以下前置 Mod。确认后会先下载前置，再下载 Mod 本体。",
                    file.FileName,
                    "下载前置并继续",
                    targetLabel: "Mod 文件",
                    dependencies: dependencies);
                if (!proceed) return;
            }
        }

        string preferredDirectory;
        try
        {
            preferredDirectory = selected is not null
                ? Path.Combine(
                    GetVersionGameDirectory(selected.Id),
                    ContentSubdirectory(hit.ContentKind))
                : Directory.Exists(Settings.LastModDownloadDirectory)
                    ? Settings.LastModDownloadDirectory!
                    : Settings.DataDirectory;
            Directory.CreateDirectory(preferredDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or
                                       NotSupportedException or UnauthorizedAccessException)
        {
            LogService.Warn($"无法创建内容保存目录，将使用安全目录：{ex.Message}");
            preferredDirectory = Settings.DataDirectory;
        }
        var (filter, defaultExtension) = ContentDownloadFormat(hit.ContentKind);
        var fileName = SanitizeModFileName(file.FileName, defaultExtension);
        LogService.Info($"打开内容保存窗口：{preferredDirectory} / {fileName}");
        (bool? Accepted, SaveFileDialog Dialog) selection;
        try
        {
            selection = ShowModSaveDialog(
                contentLabel, fileName, preferredDirectory, filter,
                defaultExtension);
        }
        catch (Exception ex) when (ex is ArgumentException or COMException or
                                       InvalidOperationException or NotSupportedException)
        {
            var fallbackDirectory = GetSafeSaveDialogDirectory();
            LogService.Warn(
                $"保存窗口初始位置无效，已回退到 {fallbackDirectory}：{ex.Message}");
            try
            {
                selection = ShowModSaveDialog(
                    contentLabel, fileName, fallbackDirectory, filter,
                    defaultExtension);
            }
            catch (Exception fallbackError)
            {
                ShowError("无法打开保存窗口", fallbackError);
                return;
            }
        }
        if (selection.Accepted != true) return;
        var destination = selection.Dialog.FileName;
        Settings.LastModDownloadDirectory = Path.GetDirectoryName(destination);
        PersistConfig();

        hit.IsInstalling = true;
        hit.Status = hit.PrimaryActionProgressLabel;
        RefreshModDetailInstallState(hit);
        try
        {
            await RunOperationAsync($"下载 {contentLabel} · {hit.Title}", async token =>
            {
                var progress = new Progress<DownloadProgress>(UpdateDownloadProgress);
                var destinationDirectory = Path.GetDirectoryName(destination)
                                           ?? Settings.DataDirectory;
                for (var index = 0; index < dependencies.Count; index++)
                {
                    var dependency = dependencies[index];
                    var dependencyName = SanitizeModFileName(dependency.FileName, ".jar");
                    var dependencyPath = Path.Combine(destinationDirectory, dependencyName);
                    if (string.Equals(
                            dependencyPath, destination, StringComparison.OrdinalIgnoreCase))
                        continue;
                    await _http.DownloadFileAsync(
                        dependency.Url,
                        dependencyPath,
                        dependency.Sha1,
                        dependency.Size,
                        (received, total) =>
                        {
                            ((IProgress<DownloadProgress>)progress).Report(
                                new DownloadProgress(
                                    $"正在下载前置 {index + 1}/{dependencies.Count}",
                                    1,
                                    1,
                                    dependencyName,
                                    received,
                                    total,
                                    0,
                                    null,
                                    dependency.SourceLabel));
                        },
                        token,
                        verifyExistingHash: true);
                }
                await _http.DownloadFileAsync(
                    file.Url,
                    destination,
                    file.Sha1,
                    file.Size,
                    (received, total) =>
                    {
                        ((IProgress<DownloadProgress>)progress).Report(new DownloadProgress(
                            $"正在下载{contentLabel}",
                            1,
                            1,
                            Path.GetFileName(destination),
                            received,
                            total,
                            0,
                            null,
                            file.SourceLabel));
                    },
                    token,
                    verifyExistingHash: true);
                hit.Status = dependencies.Count > 0 ? "已下载（含前置）" : "已下载";
                GlobalStatusText.Text = dependencies.Count > 0
                    ? $"{hit.Title} 和 {dependencies.Count} 个前置已保存到 {destinationDirectory}"
                    : $"{hit.Title} 已保存到 {destination}";
            });
        }
        catch (Exception ex)
        {
            ShowError($"下载{contentLabel}失败", ex);
            hit.Status = hit.PrimaryActionLabel;
        }
        finally
        {
            hit.IsInstalling = false;
            if (hit.Status == hit.PrimaryActionProgressLabel)
                hit.Status = hit.PrimaryActionLabel;
            RefreshModDetailInstallState(hit);
        }
    }

    private async Task<IReadOnlyList<ModDependencyFile>> ResolveModDependenciesAsync(
        ModSearchHit hit,
        ModInstallTarget target,
        string? selectedVersionId)
    {
        if (target.Loader == ModLoader.Vanilla) return [];
        try
        {
            if (hit.Source == ModSource.Modrinth &&
                !string.IsNullOrWhiteSpace(selectedVersionId))
            {
                return await _modrinth.ResolveRequiredDependenciesAsync(
                    selectedVersionId,
                    target.BaseVersion,
                    target.Loader,
                    CancellationToken.None);
            }

            var slug = GetModrinthSlug(hit);
            return string.IsNullOrWhiteSpace(slug)
                ? []
                : await _modrinth.ResolveRequiredDependenciesBySlugAsync(
                    slug,
                    target.BaseVersion,
                    target.Loader,
                    CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogService.Warn($"检测 Mod 前置失败 {hit.Title}：{ex.Message}");
            return [];
        }
    }

    private static string? GetModrinthSlug(ModSearchHit hit)
    {
        if (Uri.TryCreate(hit.ModrinthUrl, UriKind.Absolute, out var uri))
        {
            var slug = uri.Segments
                .Select(segment => segment.Trim('/'))
                .LastOrDefault(segment => !string.IsNullOrWhiteSpace(segment));
            if (!string.IsNullOrWhiteSpace(slug))
                return Uri.UnescapeDataString(slug);
        }
        return string.IsNullOrWhiteSpace(hit.Slug) ? null : hit.Slug.Trim('/');
    }

    private async Task InstallModpackFromCatalogAsync(
        ModSearchHit hit,
        InstalledVersionInfo? selected)
    {
        var target = GetModCatalogTarget();
        ResolvedModFile file;
        try
        {
            file = target is not null && !string.IsNullOrWhiteSpace(target.BaseVersion)
                ? hit.Source == ModSource.CurseForge
                    ? await _curseForge.ResolveCompatibleFileAsync(
                        hit, target.BaseVersion, target.Loader, CancellationToken.None)
                    : await _modrinth.ResolveCompatibleFileAsync(
                        hit, target.BaseVersion, target.Loader, CancellationToken.None)
                : hit.Source == ModSource.CurseForge
                    ? await _curseForge.ResolveLatestFileAsync(
                        hit, target?.Loader ?? ResolveContentLoader(selected, null),
                        CancellationToken.None)
                    : await _modrinth.ResolveLatestFileAsync(
                        hit, target?.Loader ?? ResolveContentLoader(selected, null),
                        CancellationToken.None);
        }
        catch (Exception ex)
        {
            ShowError("读取整合包下载文件失败", ex);
            return;
        }
        await DownloadAndInstallModpackAsync(hit, file);
    }

    private async Task DownloadAndInstallModpackAsync(
        ModSearchHit hit,
        ResolvedModFile file)
    {
        var extension = Path.GetExtension(file.FileName);
        if (!extension.Equals(".mrpack", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            extension = hit.Source == ModSource.Modrinth ? ".mrpack" : ".zip";
        }

        var cacheDirectory = Path.Combine(
            Settings.DataDirectory, "cache", "modpacks-v1", "downloads");
        var archivePath = Path.Combine(
            cacheDirectory, $"remote-{Guid.NewGuid():N}{extension.ToLowerInvariant()}");
        var downloaded = false;
        var installed = false;
        hit.IsInstalling = true;
        hit.Status = hit.PrimaryActionProgressLabel;
        RefreshModDetailInstallState(hit);
        try
        {
            await RunOperationAsync($"获取整合包 · {hit.Title}", async token =>
            {
                Directory.CreateDirectory(cacheDirectory);
                var progress = new Progress<DownloadProgress>(UpdateDownloadProgress);
                await _http.DownloadFileAsync(
                    file.Url,
                    archivePath,
                    file.Sha1,
                    file.Size,
                    (received, total) =>
                    {
                        ((IProgress<DownloadProgress>)progress).Report(new DownloadProgress(
                            "正在获取整合包",
                            1,
                            1,
                            Path.GetFileName(archivePath),
                            received,
                            total,
                            0,
                            null,
                            file.SourceLabel));
                    },
                    token,
                    verifyExistingHash: true);
                downloaded = File.Exists(archivePath) &&
                             new FileInfo(archivePath).Length > 0;
            });

            if (!downloaded) return;
            GlobalStatusText.Text = "整合包已获取，正在读取安装信息…";
            installed = await InstallModpackAsync(archivePath);
            hit.Status = installed ? "已安装" : hit.PrimaryActionLabel;
        }
        catch (Exception ex)
        {
            ShowError("安装整合包失败", ex);
            hit.Status = hit.PrimaryActionLabel;
        }
        finally
        {
            TryDeleteTemporaryFile(archivePath);
            TryDeleteTemporaryFile(archivePath + ".part");
            hit.IsInstalling = false;
            if (!installed && hit.Status == hit.PrimaryActionProgressLabel)
                hit.Status = hit.PrimaryActionLabel;
            RefreshModDetailInstallState(hit);
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warn($"清理整合包临时文件失败：{path} · {ex.Message}");
        }
    }

    private (bool? Accepted, SaveFileDialog Dialog) ShowModSaveDialog(
        string contentLabel,
        string fileName,
        string initialDirectory,
        string filter,
        string defaultExtension)
    {
        var saveDialog = new SaveFileDialog
        {
            Title = $"保存{contentLabel}文件",
            FileName = fileName,
            InitialDirectory = initialDirectory,
            Filter = filter,
            DefaultExt = defaultExtension,
            AddExtension = true,
            OverwritePrompt = true
        };
        var result = saveDialog.ShowDialog(this);
        return (result, saveDialog);
    }

    internal static string SanitizeModFileName(string fileName, string defaultExtension)
    {
        var value = Path.GetFileName(fileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(value)) value = "download" + defaultExtension;
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        value = new string(value.Select(character =>
            invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray());
        value = value.Trim().TrimEnd('.', ' ');
        if (value is "." or ".." || string.IsNullOrWhiteSpace(value))
            value = "download";
        var deviceName = value.Split('.')[0];
        if (deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (deviceName.Length == 4 &&
             (deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
              deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
             char.IsDigit(deviceName[3])))
            value = "_" + value;
        if (string.IsNullOrWhiteSpace(Path.GetExtension(value)))
            value += defaultExtension;
        return value;
    }

    private string GetSafeSaveDialogDirectory()
    {
        foreach (var candidate in new[]
                 {
                     Settings.DataDirectory,
                     Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                     Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     Path.GetTempPath()
                 })
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                LogService.Warn($"忽略无效的保存目录回退项：{candidate}");
            }
        }
        return AppContext.BaseDirectory;
    }

    private async Task<ModInstallTarget?> ShowModInstallTargetDialogAsync(ModSearchHit hit)
    {
        if (_modTargetSelection is not null) return null;
        if (_remoteVersions.Count == 0)
        {
            try
            {
                await LoadManifestAsync();
            }
            catch (Exception ex)
            {
                ShowError("读取 Minecraft 版本失败", ex);
                return null;
            }
        }

        var releases = _remoteVersions
            .Where(version => string.Equals(version.Type, "release", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (releases.Count == 0)
        {
            MessageBox.Show(this, "没有可用的 Minecraft 正式版，请先刷新版本清单。",
                "选择安装目标", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        var home = HomeVersionCombo.SelectedItem as InstalledVersionInfo;
        var preferredBase = home is null
            ? _loaderTargetVersion
            : string.IsNullOrWhiteSpace(home.BaseVersion) ? home.Id : home.BaseVersion;
        var preferredLoader = home is { SupportsMods: true } ? home.Loader : ModLoader.Fabric;

        var contentLabel = hit.ContentKindLabel;
        ModTargetTitleText.Text = $"选择{contentLabel}下载目标";
        ModTargetModNameText.Text = hit.Title;
        ModTargetVersionCombo.ItemsSource = releases;
        ModTargetVersionCombo.SelectedItem =
            releases.FirstOrDefault(version => version.Id == preferredBase)
            ?? releases.FirstOrDefault(version => version.IsLatestRelease)
            ?? releases[0];
        var loaderChoices = ConfirmLoaderChoices
            .Where(option => option.Loader is ModLoader.Fabric or ModLoader.Quilt or
                ModLoader.Forge or ModLoader.NeoForge)
            .ToList();
        ModTargetLoaderCombo.ItemsSource = loaderChoices;
        ModTargetLoaderCombo.SelectedItem =
            loaderChoices.FirstOrDefault(option => option.Loader == preferredLoader)
            ?? loaderChoices.First();
        var needsLoader = hit.ContentKind is ContentKind.Mod or ContentKind.Modpack;
        var loaderVisibility = needsLoader ? Visibility.Visible : Visibility.Collapsed;
        ModTargetLoaderLabel.Visibility = loaderVisibility;
        ModTargetLoaderCombo.Visibility = loaderVisibility;
        ModTargetStatusText.Text = home is null
            ? $"确认后将弹出系统保存窗口，可自行选择{contentLabel}保存位置。"
            : $"已根据当前选择预填 Minecraft {preferredBase}，确认后可更改保存位置。";

        var completion = new TaskCompletionSource<ModInstallTarget?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _modTargetSelection = completion;
        ShowOverlayWindow(
            ModTargetOverlay,
            "选择下载目标",
            () => ResolveModTargetSelection(null));
        return await completion.Task;
    }

    private void AcceptModTargetSelection_Click(object sender, RoutedEventArgs e)
    {
        if (ModTargetVersionCombo.SelectedItem is not RemoteVersion version)
            return;
        var loader = (ModTargetLoaderCombo.SelectedItem as LoaderChoiceOption)?.Loader
                     ?? ModLoader.Fabric;
        ResolveModTargetSelection(new ModInstallTarget(version.Id, loader));
    }

    private void CancelModTargetSelection_Click(object sender, RoutedEventArgs e) =>
        ResolveModTargetSelection(null);

    private void ResolveModTargetSelection(ModInstallTarget? target)
    {
        HideOverlayWindow(ModTargetOverlay);
        var completion = _modTargetSelection;
        _modTargetSelection = null;
        completion?.TrySetResult(target);
    }

    private async Task<ModFileChoice?> ShowModVersionSelectionAsync(
        ModSearchHit hit,
        IReadOnlyList<ModFileChoice> choices)
    {
        if (_modVersionSelection is not null) return null;
        ModVersionProjectText.Text = hit.Title;
        ModVersionList.ItemsSource = choices;
        ModVersionList.SelectedIndex = choices.Count > 0 ? 0 : -1;
        ModVersionList.Visibility = choices.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        ModVersionEmptyText.Visibility = choices.Count > 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        SelectModVersionButton.IsEnabled = choices.Count > 0;
        var completion = new TaskCompletionSource<ModFileChoice?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _modVersionSelection = completion;
        ShowOverlayWindow(
            ModVersionOverlay,
            "选择模组版本",
            () => ResolveModVersionSelection(null));
        return await completion.Task;
    }

    private void ModVersionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectModVersionButton is not null)
            SelectModVersionButton.IsEnabled =
                ModVersionList.SelectedItem is ModFileChoice;
    }

    private void AcceptModVersionSelection_Click(object sender, RoutedEventArgs e)
    {
        if (ModVersionList.SelectedItem is ModFileChoice choice)
            ResolveModVersionSelection(choice);
    }

    private void CancelModVersionSelection_Click(object sender, RoutedEventArgs e) =>
        ResolveModVersionSelection(null);

    private void ResolveModVersionSelection(ModFileChoice? choice)
    {
        HideOverlayWindow(ModVersionOverlay);
        ModVersionList.ItemsSource = null;
        var completion = _modVersionSelection;
        _modVersionSelection = null;
        completion?.TrySetResult(choice);
    }

    private void MemorySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MemoryValueText is not null)
        {
            var megabytes = (int)Math.Round(e.NewValue);
            MemoryValueText.Text = $"{megabytes / 1024d:0.#} GB";
            MemorySlider.ToolTip = $"{megabytes} MB";
        }
        ScheduleAutoSave();
    }

    private void BrowseJava_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Java 可执行文件",
            Filter = "Java|java.exe;javaw.exe|所有文件|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) JavaPathBox.Text = dialog.FileName;
    }

    private void ClearJava_Click(object sender, RoutedEventArgs e) => JavaPathBox.Text = "";

    private void ThemeOption_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not RadioButton option ||
            !Enum.TryParse<LauncherThemeMode>(option.Tag?.ToString(), out var mode))
            return;

        Settings.ThemeMode = mode;
        ThemeService.Apply(mode);
        PersistConfig();
        GlobalStatusText.Text = mode switch
        {
            LauncherThemeMode.Dark => "已切换为深色主题",
            LauncherThemeMode.Light => "已切换为浅色主题",
            _ => "已切换为跟随系统主题"
        };
    }

    private void AutoSettingsTextChanged(object sender, TextChangedEventArgs e) =>
        ScheduleAutoSave();

    private void AutoSettingsToggle_Changed(object sender, RoutedEventArgs e) =>
        ScheduleAutoSave();

    private void GlobalGameOptionsEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (GlobalGameOptionsPanel is null) return;
        var enabled = GlobalGameOptionsCheck.IsChecked == true;
        GlobalGameOptionsPanel.IsEnabled = enabled;
        GlobalGameOptionsPanel.Opacity = enabled ? 1 : 0.55;
        ScheduleAutoSave();
    }

    private void GlobalGameOption_Changed(object sender, SelectionChangedEventArgs e) =>
        ScheduleAutoSave();

    private void GlobalGameOptionToggle_Changed(object sender, RoutedEventArgs e) =>
        ScheduleAutoSave();

    private void GlobalGameSyncKeybindings_Changed(object sender, RoutedEventArgs e)
    {
        UpdateGlobalGameKeybindingsStatus();
        ScheduleAutoSave();
    }

    private void ImportGlobalGameKeybindings_Click(object sender, RoutedEventArgs e)
    {
        if (GlobalGameKeybindingsSourceCombo.SelectedItem is not InstalledVersionInfo version)
        {
            MessageBox.Show(this, "请先选择一个已经启动过的游戏版本。", "导入按键",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var optionsPath = Path.Combine(GetVersionGameDirectory(version.Id), "options.txt");
        if (!File.Exists(optionsPath))
        {
            MessageBox.Show(this, "这个版本还没有生成 options.txt，请先启动一次游戏再导入。",
                "导入按键", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var keybindings = GlobalGameOptionsService.ReadKeybindings(optionsPath);
        if (keybindings.Count == 0)
        {
            MessageBox.Show(this, "这个版本中没有找到可导入的按键设置。", "导入按键",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Settings.GlobalGameOptions.Keybindings =
            new Dictionary<string, string>(keybindings, StringComparer.OrdinalIgnoreCase);
        Settings.GlobalGameOptions.SyncKeybindings = true;
        GlobalGameSyncKeybindingsCheck.IsChecked = true;
        UpdateGlobalGameKeybindingsStatus();
        SaveSettingsFromUi();
        GlobalStatusText.Text = $"已从 {version.DisplayLabel} 导入 {keybindings.Count} 个按键";
    }

    private void ClearGlobalGameKeybindings_Click(object sender, RoutedEventArgs e)
    {
        Settings.GlobalGameOptions.Keybindings.Clear();
        Settings.GlobalGameOptions.SyncKeybindings = false;
        GlobalGameSyncKeybindingsCheck.IsChecked = false;
        UpdateGlobalGameKeybindingsStatus();
        SaveSettingsFromUi();
        GlobalStatusText.Text = "已清除全局按键方案";
    }

    private void DownloadRouteCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ScheduleAutoSave();

    private void UpdateGlobalGameKeybindingsStatus()
    {
        if (GlobalGameKeybindingsStatusText is null) return;
        var count = Settings.GlobalGameOptions.Keybindings.Count;
        var sync = GlobalGameSyncKeybindingsCheck.IsChecked == true && count > 0;
        GlobalGameSyncKeybindingsCheck.IsEnabled = count > 0;
        GlobalGameKeybindingsStatusText.Text = count == 0
            ? "尚未导入按键方案"
            : sync
                ? $"将同步 {count} 个按键"
                : $"已保存 {count} 个按键（未同步）";
    }

    private void ScheduleAutoSave()
    {
        if (!_initialized) return;
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void FlushPendingSettingsSave()
    {
        _settingsSaveTimer.Stop();
        if (_initialized) SaveSettingsFromUi();
    }

    private void SaveSettingsFromUi()
    {
        var oldDirectory = Settings.DataDirectory;
        var oldIsolation = Settings.VersionIsolation;
        var newDirectory = AppPaths.PortableGameDirectory;
        try
        {
            Directory.CreateDirectory(newDirectory);
            Settings.DataDirectory = newDirectory;
            Settings.MemoryMb = (int)MemorySlider.Value;
            Settings.DownloadRouteMode =
                Enum.TryParse<DownloadRouteMode>((DownloadRouteCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                    out var routeMode)
                    ? routeMode
                    : DownloadRouteMode.Automatic;
            Settings.JavaPath = string.IsNullOrWhiteSpace(JavaPathBox.Text) ? null : JavaPathBox.Text.Trim();
            Settings.VersionIsolation = VersionIsolationCheck.IsChecked == true;
            var globalGame = Settings.GlobalGameOptions;
            globalGame.Enabled = GlobalGameOptionsCheck.IsChecked == true;
            globalGame.Language = GetComboTag(GlobalGameLanguageCombo, "zh_cn");
            globalGame.RenderDistance = GetComboInt(GlobalGameRenderDistanceCombo, 12);
            globalGame.SimulationDistance = GetComboInt(GlobalGameSimulationDistanceCombo, 12);
            globalGame.MaxFps = GetComboInt(GlobalGameMaxFpsCombo, 120);
            globalGame.GuiScale = GetComboInt(GlobalGameGuiScaleCombo, 0);
            globalGame.Fullscreen = GlobalGameFullscreenCheck.IsChecked == true;
            globalGame.EnableVsync = GlobalGameVsyncCheck.IsChecked == true;
            globalGame.PauseOnLostFocus = GlobalGamePauseCheck.IsChecked == true;
            globalGame.SyncKeybindings =
                GlobalGameSyncKeybindingsCheck.IsChecked == true &&
                globalGame.Keybindings.Count > 0;
            Settings.JvmArguments = JvmArgumentsBox.Text.Trim();
            Settings.GameArguments = GameArgumentsBox.Text.Trim();
            Settings.HideLauncherDuringGame = HideLauncherDuringGameCheck.IsChecked == true;
            if (ThemeDarkOption.IsChecked == true) Settings.ThemeMode = LauncherThemeMode.Dark;
            else if (ThemeLightOption.IsChecked == true) Settings.ThemeMode = LauncherThemeMode.Light;
            else if (ThemeSystemOption.IsChecked == true) Settings.ThemeMode = LauncherThemeMode.System;
            if (!oldDirectory.Equals(newDirectory, StringComparison.OrdinalIgnoreCase))
            {
                InitializeServices();
                RefreshInstalledVersions();
                _ = LoadManifestAsync();
                LogService.Info($"数据目录已切换为 {newDirectory}");
            }
            else if (oldIsolation != Settings.VersionIsolation)
            {
                RefreshInstalledVersions();
            }
            PersistConfig();
            GlobalStatusText.Text = "设置已自动保存";
        }
        catch (Exception ex)
        {
            GlobalStatusText.Text = "自动保存失败：" + ex.Message;
            LogService.Error("自动保存设置失败", ex);
        }
    }

    private static void SelectComboTag(ComboBox comboBox, string? value, string fallback)
    {
        var items = comboBox.Items.OfType<ComboBoxItem>().ToList();
        comboBox.SelectedItem =
            items.FirstOrDefault(item =>
                string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            ?? items.FirstOrDefault(item =>
                string.Equals(item.Tag?.ToString(), fallback, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetComboTag(ComboBox comboBox, string fallback) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private static int GetComboInt(ComboBox comboBox, int fallback) =>
        int.TryParse((comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var value)
            ? value
            : fallback;

    private void OpenDataDirectory_Click(object sender, RoutedEventArgs e) => OpenDirectory(Settings.DataDirectory);

    private void OpenInstanceFolder_Click(object sender, RoutedEventArgs e)
    {
        var version = (InstalledVersionsSideCombo.SelectedItem as InstalledVersionInfo) ??
                      (HomeVersionCombo.SelectedItem as InstalledVersionInfo);
        if (version is null)
        {
            MessageBox.Show(this, "请先选择一个已安装版本。", "游戏版本", MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        OpenDirectory(GetVersionGameDirectory(version.Id));
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(LogService.LogFilePath))
            Process.Start(new ProcessStartInfo("explorer.exe",
                $"/select,\"{LogService.LogFilePath}\"")
            { UseShellExecute = true });
    }

    private static void OpenDirectory(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private string GetVersionGameDirectory(string versionId) =>
        Settings.VersionIsolation
            ? Path.Combine(Settings.DataDirectory, "versions", versionId)
            : Settings.DataDirectory;

    private void UpdateSearchHints()
    {
        if (VersionSearchHint is not null)
            VersionSearchHint.Visibility =
                VersionSearchBox.Text.Length == 0 && !VersionSearchBox.IsKeyboardFocused
                ? Visibility.Visible : Visibility.Collapsed;
        if (ModSearchHint is not null)
            ModSearchHint.Visibility =
                ModSearchBox.Text.Length == 0 && !ModSearchBox.IsKeyboardFocused
                ? Visibility.Visible : Visibility.Collapsed;
        if (OfflineNameHint is not null)
            OfflineNameHint.Visibility =
                OfflineNameBox.Text.Length == 0 && !OfflineNameBox.IsKeyboardFocused
                ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_FocusChanged(object sender, RoutedEventArgs e)
    {
        UpdateSearchHints();
        if (sender is TextBox { Text.Length: 0 } textBox && textBox.IsKeyboardFocused)
            PositionSearchCaretAtStart(textBox);
    }

    private void SearchArea_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement area ||
            area.Tag is not TextBox textBox ||
            e.OriginalSource is DependencyObject source &&
            FindVisualAncestor<TextBox>(source) is not null)
            return;

        PositionSearchCaretAtStart(textBox);
        e.Handled = true;
    }

    private void SearchTextBox_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not TextBox { Text.Length: 0 } textBox) return;
        PositionSearchCaretAtStart(textBox);
        e.Handled = true;
    }

    private void SearchTextBox_PreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not TextBox { Text.Length: 0 } textBox) return;
        PositionSearchCaretAtStart(textBox);
        e.Handled = true;
    }

    private static void PositionSearchCaretAtStart(TextBox textBox)
    {
        textBox.Focus();
        textBox.Select(0, 0);
        textBox.Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (textBox.Text.Length != 0 || !textBox.IsKeyboardFocused) return;
                textBox.Select(0, 0);
                textBox.CaretIndex = 0;
            }));
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.FocusedElement is not TextBox focused ||
            focused != VersionSearchBox && focused != ModSearchBox ||
            e.OriginalSource is not DependencyObject source ||
            IsDescendantOf(source, VersionSearchArea) ||
            IsDescendantOf(source, ModSearchArea))
            return;

        Keyboard.ClearFocus();
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(UpdateSearchHints));
    }

    private void OfflineNameBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateSearchHints();

    private async Task RunOperationAsync(
        string title,
        Func<CancellationToken, Task> action,
        bool trackVersionStatus = false)
    {
        if (_operationCts is not null)
        {
            MessageBox.Show(this, "当前已有下载或启动任务正在进行，请稍后再试。", "任务进行中",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var cts = new CancellationTokenSource();
        _operationCts = cts;
        _trackVersionStatus = trackVersionStatus;
        CancelOperationButton.Visibility = Visibility.Visible;
        GlobalProgressBar.IsIndeterminate = false;
        GlobalProgressBar.Value = 0;
        GlobalStatusText.Text = title + "…";
        if (trackVersionStatus)
        {
            VersionStatusCard.Visibility = Visibility.Visible;
            VersionTaskStatus.Text = title;
            VersionProgressBar.Value = 0;
            VersionProgressDetail.Text = "";
        }
        UpdateLaunchState();
        UpdateLoaderTargetFromSelection();
        var completed = false;
        var cancelled = false;
        var failed = false;
        try
        {
            await action(cts.Token);
            completed = true;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            GlobalStatusText.Text = "操作已取消";
            LogService.Warn(title + "已取消");
        }
        catch (Exception ex)
        {
            failed = true;
            ShowError(title + "失败", ex);
            GlobalStatusText.Text = title + "失败";
        }
        finally
        {
            if (ReferenceEquals(_operationCts, cts)) _operationCts = null;
            cts.Dispose();
            CancelOperationButton.Visibility = Visibility.Collapsed;
            GlobalProgressBar.IsIndeterminate = false;
            GlobalProgressBar.Value = 0;
            if (trackVersionStatus)
            {
                VersionStatusCard.Visibility = Visibility.Visible;
                VersionProgressBar.IsIndeterminate = false;
                VersionProgressBar.Value = completed ? 100 : 0;
                VersionTaskStatus.Text = failed
                    ? "失败"
                    : cancelled
                        ? "已取消"
                        : VersionCompletionLabel(title);
                VersionProgressDetail.Text = failed
                    ? "请查看错误信息后重试"
                    : cancelled ? "操作已取消" : "";
            }
            _trackVersionStatus = false;
            if (_runningGame is not { HasExited: false } && GlobalStatusText.Text.EndsWith("…"))
                GlobalStatusText.Text = "就绪";
            UpdateLaunchState();
            UpdateLoaderTargetFromSelection();
        }
    }

    private void UpdateDownloadProgress(DownloadProgress progress)
    {
        GlobalProgressBar.IsIndeterminate = progress.Total <= 0;
        if (progress.Total > 0) GlobalProgressBar.Value = progress.Percent;
        var speed = FormatSpeed(progress.BytesPerSecond);
        var remaining = FormatRemaining(progress.EstimatedRemaining);
        if (_launchInProgress)
            HomeLaunchStatusText.Text = string.IsNullOrWhiteSpace(progress.CurrentFile)
                ? $"{progress.Stage} · {speed} · 剩余 {remaining}"
                : $"{progress.Stage} · {progress.CurrentFile}";
        if (_trackVersionStatus)
        {
            VersionStatusCard.Visibility = Visibility.Visible;
            VersionProgressBar.IsIndeterminate = progress.Total <= 0;
            VersionProgressBar.Value = progress.Percent;
            VersionTaskStatus.Text = string.IsNullOrWhiteSpace(progress.Route)
                ? progress.Stage
                : $"{progress.Stage} · {progress.Route}";
            VersionProgressDetail.Text = string.IsNullOrWhiteSpace(progress.CurrentFile)
                ? $"{progress.Completed}/{progress.Total} · {speed} · 剩余 {remaining}"
                : $"{progress.Completed}/{progress.Total} · {speed} · 剩余 {remaining} · {progress.CurrentFile}";
        }
        GlobalStatusText.Text = $"{progress.Stage} · {speed} · 剩余 {remaining}";
    }

    private static string VersionCompletionLabel(string title)
    {
        if (title.Contains("检查", StringComparison.Ordinal)) return "检查完成";
        if (title.Contains("安装", StringComparison.Ordinal)) return "安装完成";
        if (title.Contains("下载", StringComparison.Ordinal)) return "下载完成";
        return "完成";
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "计算速度中";
        if (bytesPerSecond >= 1024 * 1024)
            return $"{bytesPerSecond / 1024 / 1024:0.0} MB/s";
        return $"{bytesPerSecond / 1024:0} KB/s";
    }

    private static string FormatRemaining(TimeSpan? remaining)
    {
        if (remaining is null) return "计算中";
        if (remaining.Value <= TimeSpan.Zero) return "00:00";
        return remaining.Value.TotalHours >= 1
            ? $"{(int)remaining.Value.TotalHours:00}:{remaining.Value.Minutes:00}:{remaining.Value.Seconds:00}"
            : $"{remaining.Value.Minutes:00}:{remaining.Value.Seconds:00}";
    }

    private void CancelOperation_Click(object sender, RoutedEventArgs e) => _operationCts?.Cancel();

    private async Task<bool> ConfirmInstallAsync(
        string title,
        string message,
        string target,
        string acceptLabel,
        bool destructive = false,
        IReadOnlyList<InstalledVersionInfo>? targetVersions = null,
        InstalledVersionInfo? selectedTarget = null,
        string? loaderBaseVersion = null,
        bool allowDisplayName = false,
        string targetLabel = "安装目标",
        IReadOnlyList<ModDependencyFile>? dependencies = null)
    {
        if (_installConfirmation is not null || _operationCts is not null) return false;
        _confirmedInstallVersion = null;
        _confirmedLoaderOption = null;
        _confirmedOptiFineOption = null;
        _confirmOptiFabricFile = null;
        _confirmedOptiFabricFile = null;
        _confirmedInstallOptiFabric = false;
        _confirmedDisplayName = null;
        _confirmLoaderBaseVersion = loaderBaseVersion;
        _installConfirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ConfirmTitleText.Text = title;
        ConfirmMessageText.Text = message;
        ConfirmTargetLabel.Text = targetLabel;
        ConfirmTargetText.Text = target;
        ConfirmTargetText.Visibility = targetVersions is null ? Visibility.Visible : Visibility.Collapsed;
        ConfirmVersionCombo.Visibility = targetVersions is null ? Visibility.Collapsed : Visibility.Visible;
        ConfirmVersionCombo.ItemsSource = targetVersions;
        ConfirmVersionCombo.SelectedItem = targetVersions is null
            ? null
            : selectedTarget ?? targetVersions.FirstOrDefault();
        ConfirmDependencySection.Visibility =
            dependencies is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        ConfirmDependencyList.ItemsSource = dependencies;
        ConfirmDependencyHint.Text = dependencies is { Count: > 0 }
            ? $"共 {dependencies.Count} 个必需前置。已有文件会校验后跳过，不会重复下载。"
            : "";
        ConfirmLoaderSection.Visibility = loaderBaseVersion is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        _confirmLoaderCts?.Cancel();
        ConfirmLoaderKindCombo.ItemsSource = loaderBaseVersion is null ? null : ConfirmLoaderChoices;
        ConfirmLoaderKindCombo.SelectedIndex = loaderBaseVersion is null ? -1 : 0;
        ConfirmLoaderVersionCombo.ItemsSource = null;
        ConfirmLoaderVersionCombo.SelectedItem = null;
        ConfirmLoaderVersionCombo.Visibility = Visibility.Collapsed;
        ConfirmLoaderVersionLabel.Visibility = Visibility.Collapsed;
        _confirmOptiFabricCts?.Cancel();
        ConfirmOptiFabricCheck.Visibility = Visibility.Collapsed;
        ConfirmOptiFabricCheck.IsChecked = false;
        ConfirmOptiFineVersionCombo.ItemsSource = null;
        ConfirmOptiFineVersionCombo.SelectedItem = null;
        ConfirmOptiFineVersionCombo.Visibility = Visibility.Collapsed;
        ConfirmOptiFineVersionLabel.Visibility = Visibility.Collapsed;
        ConfirmLoaderStatus.Text = loaderBaseVersion is null ? "" : "仅安装原版，不添加 Mod 加载器。";
        ConfirmVersionNameSection.Visibility =
            allowDisplayName ? Visibility.Visible : Visibility.Collapsed;
        ConfirmVersionNameBox.Text = "";
        ConfirmVersionNameHint.Text =
            "输入不同名称会创建独立版本目录；留空时使用默认目录。";
        ConfirmVersionNameHint.Foreground = (Brush)FindResource("MutedTextBrush");
        ConfirmInstallButton.IsEnabled = true;
        ConfirmInstallButton.Content = acceptLabel;
        ConfirmInstallButton.Style = (Style)FindResource(destructive ? "DangerPrimaryButton" : "PrimaryButton");
        ConfirmIconText.Text = destructive ? "\uE74D" : "\uE73E";
        ConfirmIconText.Foreground = (Brush)FindResource(destructive ? "DangerBrush" : "AccentBrush");
        ConfirmIconBorder.Background =
            (Brush)FindResource(destructive ? "DangerTintBrush" : "AccentTintBrush");
        ShowOverlayWindow(
            ConfirmOverlay,
            title,
            () => ResolveInstallConfirmation(false));
        return await _installConfirmation.Task;
    }

    private async void ConfirmLoaderKindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConfirmLoaderKindCombo.SelectedItem is not LoaderChoiceOption choice) return;
        _confirmLoaderCts?.Cancel();
        _confirmLoaderCts?.Dispose();
        _confirmLoaderCts = null;
        _confirmOptiFabricCts?.Cancel();
        _confirmOptiFabricCts?.Dispose();
        _confirmOptiFabricCts = null;
        ConfirmLoaderVersionCombo.IsEnabled = true;
        ConfirmOptiFabricCheck.IsChecked = false;
        ConfirmOptiFabricCheck.Visibility = choice.Loader == ModLoader.Fabric
            ? Visibility.Visible
            : Visibility.Collapsed;
        ConfirmOptiFineVersionCombo.ItemsSource = null;
        ConfirmOptiFineVersionCombo.SelectedItem = null;
        ConfirmOptiFineVersionCombo.Visibility = Visibility.Collapsed;
        ConfirmOptiFineVersionLabel.Visibility = Visibility.Collapsed;
        ConfirmLoaderVersionCombo.ItemsSource = null;
        ConfirmLoaderVersionCombo.SelectedItem = null;

        if (choice.Loader == ModLoader.Vanilla)
        {
            ConfirmLoaderVersionCombo.Visibility = Visibility.Collapsed;
            ConfirmLoaderVersionLabel.Visibility = Visibility.Collapsed;
            ConfirmLoaderStatus.Text = "仅安装原版，不添加 Mod 加载器。";
            ConfirmInstallButton.IsEnabled = true;
            return;
        }

        var target = _confirmLoaderBaseVersion;
        if (string.IsNullOrWhiteSpace(target)) return;
        ConfirmLoaderVersionCombo.Visibility = Visibility.Visible;
        ConfirmLoaderVersionLabel.Visibility = Visibility.Visible;
        ConfirmLoaderStatus.Text = $"正在读取 {LoaderName(choice.Loader)} 版本…";
        ConfirmInstallButton.IsEnabled = false;
        var cancellation = new CancellationTokenSource();
        _confirmLoaderCts = cancellation;
        try
        {
            var versions = await _loaderCatalog.GetVersionsAsync(
                choice.Loader, target, cancellation.Token);
            if (cancellation.IsCancellationRequested ||
                ConfirmLoaderKindCombo.SelectedItem is not LoaderChoiceOption selected ||
                selected.Loader != choice.Loader)
                return;
            ConfirmLoaderVersionCombo.ItemsSource = versions;
            ConfirmLoaderVersionCombo.SelectedIndex = versions.Count > 0 ? 0 : -1;
            if (choice.Loader == ModLoader.Fabric &&
                ConfirmOptiFabricCheck.IsChecked == true)
                ApplyOptiFabricLoaderCompatibility();
            ConfirmLoaderStatus.Text = versions.Count > 0
                ? $"共 {versions.Count} 个版本，默认选择最新版本。"
                : choice.Loader == ModLoader.OptiFine
                    ? $"OptiFine 官方尚未发布适用于 Minecraft {target} 的版本或预览版。"
                    : $"{LoaderName(choice.Loader)} 没有适用于 Minecraft {target} 的版本。";
        }
        catch (OperationCanceledException)
        {
            // A different loader choice superseded this request.
        }
        catch (Exception ex)
        {
            ConfirmLoaderStatus.Text = $"读取版本失败：{ex.Message}";
            LogService.Warn($"读取安装加载器版本失败：{ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_confirmLoaderCts, cancellation))
                UpdateConfirmInstallButtonState();
        }
    }

    private void ConfirmLoaderVersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateConfirmInstallButtonState();
    }

    private void ConfirmVersionNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ConfirmVersionNameHint is null) return;
        ValidateConfirmVersionName(updateHint: true);
        UpdateConfirmInstallButtonState();
    }

    private async void ConfirmOptiFabric_Changed(object sender, RoutedEventArgs e)
    {
        _confirmOptiFabricCts?.Cancel();
        _confirmOptiFabricCts?.Dispose();
        _confirmOptiFabricCts = null;
        _confirmOptiFabricFile = null;
        ConfirmOptiFineVersionCombo.ItemsSource = null;
        ConfirmOptiFineVersionCombo.SelectedItem = null;
        if (ConfirmOptiFabricCheck.IsChecked != true ||
            ConfirmLoaderKindCombo.SelectedItem is not LoaderChoiceOption { Loader: ModLoader.Fabric })
        {
            ConfirmLoaderVersionCombo.IsEnabled = true;
            ConfirmOptiFineVersionCombo.Visibility = Visibility.Collapsed;
            ConfirmOptiFineVersionLabel.Visibility = Visibility.Collapsed;
            UpdateConfirmInstallButtonState();
            return;
        }

        ApplyOptiFabricLoaderCompatibility();

        var target = _confirmLoaderBaseVersion;
        if (string.IsNullOrWhiteSpace(target)) return;
        ConfirmOptiFineVersionCombo.Visibility = Visibility.Visible;
        ConfirmOptiFineVersionLabel.Visibility = Visibility.Visible;
        ConfirmLoaderStatus.Text = "正在检查 OptiFine 和 OptiFabric 兼容版本…";
        UpdateConfirmInstallButtonState();
        var cancellation = new CancellationTokenSource();
        _confirmOptiFabricCts = cancellation;
        try
        {
            var optiFineTask = _loaderCatalog.GetVersionsAsync(
                ModLoader.OptiFine, target, cancellation.Token);
            var optiFabricTask = _curseForge.FindProjectFileAsync(
                "optifabric", target, ModLoader.Fabric, cancellation.Token);
            await Task.WhenAll(optiFineTask, optiFabricTask);
            if (cancellation.IsCancellationRequested ||
                ConfirmOptiFabricCheck.IsChecked != true ||
                ConfirmLoaderKindCombo.SelectedItem is not LoaderChoiceOption { Loader: ModLoader.Fabric })
                return;
            var optiFineVersions = await optiFineTask;
            _confirmOptiFabricFile = await optiFabricTask;
            ConfirmOptiFineVersionCombo.ItemsSource = optiFineVersions;
            ConfirmOptiFineVersionCombo.SelectedIndex = optiFineVersions.Count > 0 ? 0 : -1;
            if (optiFineVersions.Count == 0)
                ConfirmLoaderStatus.Text =
                    $"OptiFine 官方尚未发布适用于 Minecraft {target} 的版本或预览版。";
            else if (_confirmOptiFabricFile is null)
                ConfirmLoaderStatus.Text = $"OptiFabric 没有适用于 Minecraft {target} 的版本。";
            else
                ConfirmLoaderStatus.Text =
                    $"将安装 Fabric {OptiFabricLoaderVersion} + OptiFine " +
                    $"{optiFineVersions[0].DisplayName} + OptiFabric。";
        }
        catch (OperationCanceledException)
        {
            // The selection changed while compatibility was being checked.
        }
        catch (Exception ex)
        {
            ConfirmLoaderStatus.Text = $"检查 OptiFabric 失败：{ex.Message}";
            LogService.Warn($"检查 OptiFabric 兼容版本失败：{ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_confirmOptiFabricCts, cancellation))
                UpdateConfirmInstallButtonState();
        }
    }

    private void ConfirmOptiFineVersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConfirmOptiFabricCheck.IsChecked == true)
            UpdateConfirmInstallButtonState();
    }

    private bool ApplyOptiFabricLoaderCompatibility()
    {
        if (ConfirmOptiFabricCheck.IsChecked != true ||
            ConfirmLoaderKindCombo.SelectedItem is not LoaderChoiceOption { Loader: ModLoader.Fabric })
        {
            ConfirmLoaderVersionCombo.IsEnabled = true;
            return true;
        }

        ConfirmLoaderVersionCombo.IsEnabled = false;
        if (ConfirmLoaderVersionCombo.ItemsSource is not IEnumerable<LoaderVersionOption> versions)
            return false;
        var ceiling = Version.Parse(OptiFabricLoaderVersion);
        var compatible = versions
            .Select(option => (Option: option, Parsed: Version.TryParse(option.Version, out var parsed)
                ? parsed
                : null))
            .Where(item => item.Parsed is not null && item.Parsed <= ceiling)
            .OrderByDescending(item => item.Parsed)
            .Select(item => item.Option)
            .FirstOrDefault();
        if (compatible is null) return false;
        ConfirmLoaderVersionCombo.SelectedItem = compatible;
        return true;
    }

    private void UpdateConfirmInstallButtonState()
    {
        var nameReady = ValidateConfirmVersionName(updateHint: false);
        if (ConfirmLoaderSection.Visibility != Visibility.Visible ||
            ConfirmLoaderKindCombo.SelectedItem is not LoaderChoiceOption choice)
        {
            ConfirmInstallButton.IsEnabled = nameReady;
            return;
        }
        if (choice.Loader == ModLoader.Vanilla)
        {
            ConfirmInstallButton.IsEnabled = nameReady;
            return;
        }
        if (ConfirmLoaderVersionCombo.SelectedItem is not LoaderVersionOption)
        {
            ConfirmInstallButton.IsEnabled = false;
            return;
        }
        if (choice.Loader == ModLoader.Fabric && ConfirmOptiFabricCheck.IsChecked == true)
        {
            var loaderOption = ConfirmLoaderVersionCombo.SelectedItem as LoaderVersionOption;
            var loaderCompatible = loaderOption is not null &&
                                   Version.TryParse(loaderOption.Version, out var loaderVersion) &&
                                   loaderVersion <= Version.Parse(OptiFabricLoaderVersion);
            ConfirmInstallButton.IsEnabled =
                nameReady &&
                loaderCompatible &&
                ConfirmOptiFineVersionCombo.SelectedItem is LoaderVersionOption &&
                _confirmOptiFabricFile is not null;
            return;
        }
        ConfirmInstallButton.IsEnabled = nameReady;
    }

    private bool ValidateConfirmVersionName(bool updateHint)
    {
        if (ConfirmVersionNameSection.Visibility != Visibility.Visible)
            return true;
        if (!VersionProfileService.TryNormalizeInstanceId(
                ConfirmVersionNameBox.Text, out var instanceId, out var error))
        {
            if (updateHint)
            {
                ConfirmVersionNameHint.Text = error;
                ConfirmVersionNameHint.Foreground = (Brush)FindResource("DangerBrush");
            }
            return false;
        }
        if (instanceId.Length == 0)
        {
            if (updateHint)
            {
                ConfirmVersionNameHint.Text =
                    "留空时使用加载器默认目录；再次安装不会创建新实例。";
                ConfirmVersionNameHint.Foreground = (Brush)FindResource("MutedTextBrush");
            }
            return true;
        }
        if (_profiles.InstanceIdExists(instanceId))
        {
            if (updateHint)
            {
                ConfirmVersionNameHint.Text =
                    $"已存在名为“{instanceId}”的版本目录，请换一个名称。";
                ConfirmVersionNameHint.Foreground = (Brush)FindResource("DangerBrush");
            }
            return false;
        }
        if (updateHint)
        {
            ConfirmVersionNameHint.Text = $"将创建独立版本：versions\\{instanceId}";
            ConfirmVersionNameHint.Foreground = (Brush)FindResource("AccentBrush");
        }
        return true;
    }

    private void AcceptInstallConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateConfirmVersionName(updateHint: true))
            return;
        if (ConfirmVersionCombo.Visibility == Visibility.Visible &&
            ConfirmVersionCombo.SelectedItem is not InstalledVersionInfo)
            return;
        if (ConfirmLoaderSection.Visibility == Visibility.Visible &&
            ConfirmLoaderKindCombo.SelectedItem is LoaderChoiceOption { Loader: not ModLoader.Vanilla } &&
            ConfirmLoaderVersionCombo.SelectedItem is not LoaderVersionOption)
            return;
        if (ConfirmLoaderKindCombo.SelectedItem is LoaderChoiceOption { Loader: ModLoader.Fabric } &&
            ConfirmOptiFabricCheck.IsChecked == true &&
            (ConfirmOptiFineVersionCombo.SelectedItem is not LoaderVersionOption ||
             _confirmOptiFabricFile is null))
            return;
        _confirmedInstallVersion = ConfirmVersionCombo.Visibility == Visibility.Visible
            ? ConfirmVersionCombo.SelectedItem as InstalledVersionInfo
            : null;
        _confirmedLoaderOption = ConfirmLoaderSection.Visibility == Visibility.Visible
            ? ConfirmLoaderVersionCombo.SelectedItem as LoaderVersionOption
            : null;
        _confirmedInstallOptiFabric =
            ConfirmLoaderKindCombo.SelectedItem is LoaderChoiceOption { Loader: ModLoader.Fabric } &&
            ConfirmOptiFabricCheck.IsChecked == true;
        _confirmedOptiFineOption = _confirmedInstallOptiFabric
            ? ConfirmOptiFineVersionCombo.SelectedItem as LoaderVersionOption
            : null;
        _confirmedOptiFabricFile = _confirmedInstallOptiFabric ? _confirmOptiFabricFile : null;
        _confirmedDisplayName = ConfirmVersionNameSection.Visibility == Visibility.Visible
            ? ConfirmVersionNameBox.Text.Trim()
            : null;
        ResolveInstallConfirmation(true);
    }

    private void CancelInstallConfirm_Click(object sender, RoutedEventArgs e) =>
        ResolveInstallConfirmation(false);

    private void ResolveInstallConfirmation(bool accepted)
    {
        HideOverlayWindow(ConfirmOverlay);
        var completion = _installConfirmation;
        _installConfirmation = null;
        if (!accepted)
        {
            _confirmedInstallVersion = null;
            _confirmedLoaderOption = null;
            _confirmedOptiFineOption = null;
            _confirmedOptiFabricFile = null;
            _confirmedInstallOptiFabric = false;
            _confirmedDisplayName = null;
        }
        _confirmLoaderCts?.Cancel();
        _confirmLoaderCts?.Dispose();
        _confirmLoaderCts = null;
        _confirmOptiFabricCts?.Cancel();
        _confirmOptiFabricCts?.Dispose();
        _confirmOptiFabricCts = null;
        completion?.TrySetResult(accepted);
    }

    private static void DeleteDirectoryToRecycleBin(string dataRoot, string target)
    {
        var rootFull = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar) +
                       Path.DirectorySeparatorChar;
        var targetFull = Path.GetFullPath(target);
        if (!targetFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ||
            targetFull.Equals(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("拒绝删除数据目录之外的路径。");
        if (!Directory.Exists(targetFull)) return;
        FileSystem.DeleteDirectory(targetFull, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
    }

    private static long SafeDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        try
        {
            return Directory.EnumerateFiles(path, "*", System.IO.SearchOption.AllDirectories)
                .Sum(file => new FileInfo(file).Length);
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "大小未知";
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024d / 1024 / 1024:0.0} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / 1024d / 1024:0.0} MB";
        return $"{bytes / 1024d:0.0} KB";
    }

    private sealed class OverlayWindowState(
        Grid overlay,
        FrameworkElement content,
        Window window,
        Action? cancelAction)
    {
        public Grid Overlay { get; } = overlay;
        public FrameworkElement Content { get; } = content;
        public Window Window { get; } = window;
        public Action? CancelAction { get; } = cancelAction;
        public bool AllowClose { get; set; }
        public bool IsClosing { get; set; }
    }

    private void ShowError(string title, Exception ex)
    {
        LogService.Error(title, ex);
        MessageBox.Show(this, $"{title}：\n\n{ex.Message}", "Kumo",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void OnLogLine(string line)
    {
        _pendingLogLines.Enqueue(line);
        if (Interlocked.Exchange(ref _logFlushScheduled, 1) == 0)
            Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(FlushPendingLogLines));
    }

    private void FlushPendingLogLines()
    {
        Interlocked.Exchange(ref _logFlushScheduled, 0);
        var changed = false;
        for (var count = 0; count < 200 && _pendingLogLines.TryDequeue(out var line); count++)
        {
            _displayLogLines.Enqueue(line);
            while (_displayLogLines.Count > 300) _displayLogLines.Dequeue();
            changed = true;
        }

        if (changed)
        {
            LogBox.Text = string.Join(Environment.NewLine, _displayLogLines);
            LogBox.ScrollToEnd();
        }
        if (!_pendingLogLines.IsEmpty &&
            Interlocked.Exchange(ref _logFlushScheduled, 1) == 0)
            Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(FlushPendingLogLines));
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            WindowState = WindowState.Normal;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    internal static string ContentSubdirectory(ContentKind contentKind) => contentKind switch
    {
        ContentKind.DataPack => "datapacks",
        ContentKind.ResourcePack => "resourcepacks",
        ContentKind.Shader => "shaderpacks",
        ContentKind.Modpack => ".",
        _ => "mods"
    };

    internal static (string Filter, string DefaultExtension) ContentDownloadFormat(
        ContentKind contentKind) =>
        contentKind switch
        {
            ContentKind.Mod =>
                ("Minecraft 模组 (*.jar)|*.jar|所有文件|*.*", ".jar"),
            ContentKind.Modpack =>
                ("整合包 (*.mrpack;*.zip)|*.mrpack;*.zip|所有文件|*.*", ".mrpack"),
            ContentKind.DataPack =>
                ("数据包 (*.zip;*.jar)|*.zip;*.jar|所有文件|*.*", ".zip"),
            ContentKind.ResourcePack =>
                ("资源包 (*.zip)|*.zip|所有文件|*.*", ".zip"),
            _ =>
                ("光影包 (*.zip)|*.zip|所有文件|*.*", ".zip")
        };

    private sealed record ModCatalogTarget(
        string BaseVersion,
        ModLoader Loader,
        string DisplayLabel,
        ContentKind ContentKind);

    private sealed record ModInstallTarget(
        string BaseVersion,
        ModLoader Loader);

    private enum PageKind
    {
        Home,
        MyVersions,
        Versions,
        Mods,
        Accounts,
        Settings
    }
}
