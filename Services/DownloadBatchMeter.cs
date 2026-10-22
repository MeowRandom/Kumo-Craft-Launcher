using System.Collections.Concurrent;
using System.Diagnostics;
using Kumo.Models;

namespace Kumo.Services;

public sealed class DownloadBatchMeter
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, long> _activeBytes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _resumeOffsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly int _totalFiles;
    private readonly long _totalBytes;
    private int _completedFiles;
    private long _lastSpeedBytes;
    private long _lastElapsedMilliseconds;
    private double _smoothedSpeed;

    public DownloadBatchMeter(int totalFiles, int skippedFiles, long pendingBytes)
    {
        _totalFiles = totalFiles;
        _completedFiles = skippedFiles;
        _totalBytes = pendingBytes;
    }

    public void Report(string file, long received)
    {
        _activeBytes[file] = Math.Max(0, received);
    }

    public void SetResumeOffset(string file, long offset)
    {
        if (offset > 0) _resumeOffsets[file] = offset;
    }

    public void Complete(string file, long received)
    {
        _activeBytes.AddOrUpdate(file, Math.Max(0, received),
            (_, existing) => Math.Max(existing, Math.Max(0, received)));
        Interlocked.Increment(ref _completedFiles);
    }

    public DownloadProgress Snapshot(string stage, string currentFile, string? route)
    {
        long received;
        double speed;
        TimeSpan? remaining;
        lock (_gate)
        {
            received = _activeBytes.Values.Sum();
            var speedBytes = _activeBytes.Sum(pair =>
                Math.Max(0, pair.Value - (_resumeOffsets.TryGetValue(pair.Key, out var offset) ? offset : 0)));
            var elapsed = Math.Max(1, _clock.ElapsedMilliseconds);
            var deltaBytes = speedBytes - _lastSpeedBytes;
            var deltaTime = Math.Max(0.001, (elapsed - _lastElapsedMilliseconds) / 1000d);
            if (deltaBytes > 0)
            {
                var instantSpeed = deltaBytes / deltaTime;
                _smoothedSpeed = _smoothedSpeed <= 0
                    ? instantSpeed
                    : _smoothedSpeed * 0.72 + instantSpeed * 0.28;
                _lastSpeedBytes = speedBytes;
                _lastElapsedMilliseconds = elapsed;
            }
            else if (elapsed > _lastElapsedMilliseconds + 1200)
            {
                _smoothedSpeed *= 0.5;
                _lastElapsedMilliseconds = elapsed;
            }
            speed = _smoothedSpeed;
            remaining = _totalBytes > 0 && speed > 0
                ? TimeSpan.FromSeconds(Math.Max(0, _totalBytes - received) / speed)
                : null;
        }

        return new DownloadProgress(
            stage,
            Volatile.Read(ref _completedFiles),
            _totalFiles,
            currentFile,
            received,
            _totalBytes > 0 ? _totalBytes : null,
            speed,
            remaining,
            route);
    }
}
