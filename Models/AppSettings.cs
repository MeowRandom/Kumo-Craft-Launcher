namespace Kumo.Models;

public enum DownloadRouteMode
{
    Automatic,
    Official,
    DomesticMirror
}

public enum DownloadRoute
{
    Official,
    BmclApi
}

public enum LauncherThemeMode
{
    Dark,
    Light,
    System
}

public enum DownloadFileKind
{
    Client,
    Library,
    Asset,
    LoggingConfig,
    Other
}

public sealed class GlobalMinecraftOptions
{
    public bool Enabled { get; set; }
    public string Language { get; set; } = "zh_cn";
    public int RenderDistance { get; set; } = 12;
    public int SimulationDistance { get; set; } = 12;
    public int MaxFps { get; set; } = 120;
    public int GuiScale { get; set; }
    public bool Fullscreen { get; set; }
    public bool EnableVsync { get; set; } = true;
    public bool PauseOnLostFocus { get; set; } = true;
    public bool SyncKeybindings { get; set; }
    public Dictionary<string, string> Keybindings { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AppSettings
{
    public const string DefaultJvmArguments =
        "-XX:+UseG1GC -XX:+ParallelRefProcEnabled -Dfile.encoding=UTF-8 " +
        "-XX:-OmitStackTraceInFastThrow -Djdk.lang.Process.allowAmbiguousCommands=True " +
        "-Dfml.ignoreInvalidMinecraftCertificates=True -Dfml.ignorePatchDiscrepancies=True";

    public string DataDirectory { get; set; } = string.Empty;
    public string? JavaPath { get; set; }
    public string? JvmArguments { get; set; } = DefaultJvmArguments;
    public string? GameArguments { get; set; } = "";
    public int MemoryMb { get; set; } = 4096;
    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 760;
    public bool HideLauncherDuringGame { get; set; }
    public bool VersionIsolation { get; set; } = true;
    public GlobalMinecraftOptions GlobalGameOptions { get; set; } = new();
    public LauncherThemeMode ThemeMode { get; set; } = LauncherThemeMode.Dark;
    public DownloadRouteMode DownloadRouteMode { get; set; } = DownloadRouteMode.Automatic;
    public string? LastModDownloadDirectory { get; set; }
    public string? HomeBackgroundImagePath { get; set; }
    public string? LastVersionId { get; set; }
    public string? ActiveAccountId { get; set; }
    public List<AccountInfo> Accounts { get; set; } = [];
}
