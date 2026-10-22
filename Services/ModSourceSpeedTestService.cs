using Kumo.Models;

namespace Kumo.Services;

public sealed class ModSourceSpeedTestService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan FailedCacheDuration = TimeSpan.FromSeconds(20);
    private readonly HttpService _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ModSourceSelection? _cachedSelection;
    private DateTimeOffset _cachedAt;

    public ModSourceSpeedTestService(HttpService http)
    {
        _http = http;
    }

    public async Task<ModSourceSelection> SelectFastestAsync(CancellationToken cancellationToken = default)
    {
        var cacheLifetime = _cachedSelection?.UsedFallback == false ? CacheDuration : FailedCacheDuration;
        if (_cachedSelection is not null &&
            DateTimeOffset.UtcNow - _cachedAt < cacheLifetime)
            return _cachedSelection;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            cacheLifetime = _cachedSelection?.UsedFallback == false ? CacheDuration : FailedCacheDuration;
            if (_cachedSelection is not null &&
                DateTimeOffset.UtcNow - _cachedAt < cacheLifetime)
                return _cachedSelection;

            var probes = await Task.WhenAll(
                ProbeAsync(ModSource.Modrinth, "https://api.modrinth.com/v2/search?limit=1", cancellationToken),
                ProbeAsync(ModSource.CurseForge,
                    "https://api.cfwidget.com/minecraft/mc-mods/appleskin", cancellationToken));
            _cachedSelection = ChooseFastest(probes);
            _cachedAt = DateTimeOffset.UtcNow;
            return _cachedSelection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static ModSourceSelection ChooseFastest(IEnumerable<ModSourceProbe> probes)
    {
        var available = probes
            .Where(probe => probe.IsAvailable)
            .OrderBy(probe => probe.Latency)
            .ToList();
        if (available.Count > 0)
            return new ModSourceSelection(available[0].Source, available[0].Latency, false);
        return new ModSourceSelection(ModSource.Modrinth, null, true);
    }

    private async Task<ModSourceProbe> ProbeAsync(
        ModSource source,
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            var latency = await _http.ProbeLatencyAsync(url, cancellationToken);
            return new ModSourceProbe(source, latency, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException ||
                                   !cancellationToken.IsCancellationRequested)
        {
            return new ModSourceProbe(source, null, ex.Message);
        }
    }
}
