using System.IO;
using Kumo.Models;

namespace Kumo.Services;

public sealed record DownloadRouteCandidate(
    string OfficialUrl,
    string MirrorUrl,
    string Label);

public sealed record DownloadRouteChoice(
    DownloadRoute Route,
    string DisplayName,
    double OfficialBytesPerSecond,
    double MirrorBytesPerSecond);

public sealed class DownloadRouteService
{
    public const string MirrorRoot = "https://bmclapi2.bangbang93.com";
    private readonly HttpService _http;

    public DownloadRouteService(HttpService http)
    {
        _http = http;
    }

    public async Task<DownloadRouteChoice> SelectAsync(
        DownloadRouteMode mode,
        IReadOnlyList<DownloadRouteCandidate> candidates,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (mode == DownloadRouteMode.Official)
            return new DownloadRouteChoice(DownloadRoute.Official, "Mojang 官方", 0, 0);
        if (mode == DownloadRouteMode.DomesticMirror)
            return new DownloadRouteChoice(DownloadRoute.BmclApi, "BMCLAPI 国内镜像", 0, 0);
        if (candidates.Count == 0)
            return new DownloadRouteChoice(DownloadRoute.Official, "Mojang 官方", 0, 0);

        progress?.Report(new DownloadProgress("正在测试下载线路", 0, 1, "Mojang 官方 / BMCLAPI"));
        var probes = candidates.Take(2).Select(async candidate =>
        {
            var officialTask = TryProbeAsync(candidate.OfficialUrl, cancellationToken);
            var mirrorTask = TryProbeAsync(candidate.MirrorUrl, cancellationToken);
            await Task.WhenAll(officialTask, mirrorTask);
            return (Official: await officialTask, Mirror: await mirrorTask);
        }).ToList();

        await Task.WhenAll(probes);
        var officialSpeed = probes.Sum(x => x.Result.Official);
        var mirrorSpeed = probes.Sum(x => x.Result.Mirror);
        var useMirror = mirrorSpeed > 0 && mirrorSpeed >= officialSpeed * 1.12;
        var choice = useMirror
            ? new DownloadRouteChoice(DownloadRoute.BmclApi, "BMCLAPI 国内镜像", officialSpeed, mirrorSpeed)
            : new DownloadRouteChoice(DownloadRoute.Official, "Mojang 官方", officialSpeed, mirrorSpeed);
        LogService.Info(
            $"下载线路测速：官方 {FormatSpeed(officialSpeed)}，镜像 {FormatSpeed(mirrorSpeed)}，选择 {choice.DisplayName}");
        return choice;
    }

    public static string GetMirrorUrl(
        string versionId,
        string officialUrl,
        string relativePath,
        DownloadFileKind kind)
    {
        return kind switch
        {
            DownloadFileKind.Client =>
                $"{MirrorRoot}/version/{Uri.EscapeDataString(versionId)}/client",
            DownloadFileKind.Asset => BuildAssetMirrorUrl(officialUrl),
            DownloadFileKind.Library when IsMojangLibrary(officialUrl) =>
                $"{MirrorRoot}/maven/{relativePath.Replace('\\', '/').TrimStart('/')}",
            _ => officialUrl
        };
    }

    public static bool HasDistinctMirror(string officialUrl, string mirrorUrl) =>
        !officialUrl.Equals(mirrorUrl, StringComparison.OrdinalIgnoreCase);

    private async Task<double> TryProbeAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.ProbeDownloadSpeedAsync(url, 128 * 1024, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Warn($"线路测速失败 {new Uri(url).Host}：{ex.Message}");
            return 0;
        }
    }

    private static string BuildAssetMirrorUrl(string officialUrl)
    {
        var hash = Path.GetFileName(new Uri(officialUrl).LocalPath);
        return hash.Length >= 2
            ? $"{MirrorRoot}/assets/{hash[..2]}/{hash}"
            : officialUrl;
    }

    private static bool IsMojangLibrary(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Host.Equals("libraries.minecraft.net", StringComparison.OrdinalIgnoreCase);

    private static string FormatSpeed(double bytesPerSecond) =>
        bytesPerSecond <= 0 ? "不可用" : $"{bytesPerSecond / 1024 / 1024:0.0} MB/s";
}
