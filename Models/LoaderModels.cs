namespace Kumo.Models;

public sealed record LoaderChoiceOption(ModLoader Loader, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record RemoteModFile(string Name, long Size, string Url);

public sealed record LoaderVersionOption(
    ModLoader Loader,
    string Version,
    string DisplayName,
    string? InstallerUrl = null,
    string? InstallerFallbackUrl = null,
    string? InstallerSha1 = null,
    bool IsPreview = false)
{
    public bool UsesInstaller => InstallerUrl is not null;

    public override string ToString() => DisplayName;
}
