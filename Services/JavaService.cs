using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using Kumo.Models;

namespace Kumo.Services;

public sealed record JavaRuntime(string Executable, int MajorVersion, bool IsManaged);

public sealed class JavaService
{
    private const string RuntimeMetadataUrl =
        "https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";
    private readonly HttpService _http;
    private readonly string _dataDirectory;

    public JavaService(HttpService http, string dataDirectory)
    {
        _http = http;
        _dataDirectory = dataDirectory;
    }

    public async Task<JavaRuntime> EnsureJavaAsync(
        JsonObject profile,
        string? configuredPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken,
        bool requireExactMajor = false)
    {
        var javaVersion = profile.Obj("javaVersion");
        var required = javaVersion?.Str("component") ??
                       (profile.Str("inheritsFrom")?.Length > 0 ? "java-runtime-gamma" : "jre-legacy");
        var requiredMajor = javaVersion?["majorVersion"]?.GetValue<int>() ??
                            (required == "jre-legacy" ? 8 : 17);

        var configured = await TryJavaAsync(configuredPath, cancellationToken);
        if (configured is not null && configured.MajorVersion == requiredMajor)
            return configured;

        var managedPath = Path.Combine(_dataDirectory, "runtime", required, "bin", "java.exe");
        var managed = await TryJavaAsync(managedPath, cancellationToken);
        if (managed is not null && managed.MajorVersion == requiredMajor)
            return managed;

        var installed = await FindInstalledJavaAsync(cancellationToken);
        var exact = installed.FirstOrDefault(x => x.MajorVersion == requiredMajor);
        if (exact is not null) return exact;

        try
        {
            return await InstallManagedJavaAsync(required, requiredMajor, progress, cancellationToken);
        }
        catch (Exception ex) when (configured is not null || installed.Count > 0)
        {
            LogService.Warn($"Java {requiredMajor} 自动下载失败，尝试使用现有 Java：{ex.Message}");
            var fallback = requireExactMajor
                ? null
                : configured ?? installed
                    .Where(x => x.MajorVersion >= requiredMajor)
                    .OrderBy(x => x.MajorVersion)
                    .FirstOrDefault();
            if (fallback is not null)
            {
                LogService.Warn($"此版本推荐 Java {requiredMajor}，当前使用 Java {fallback.MajorVersion}。");
                return fallback;
            }
            if (requireExactMajor)
                throw new InvalidOperationException(
                    $"加载器安装器需要精确的 Java {requiredMajor}，但自动准备失败。", ex);
            throw;
        }
    }

    private async Task<JavaRuntime> InstallManagedJavaAsync(
        string component,
        int requiredMajor,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new DownloadProgress("正在准备 Java 运行时", 0, 1, component));
        var root = JsonNode.Parse(await _http.GetStringAsync(RuntimeMetadataUrl, cancellationToken)) as JsonObject
                   ?? throw new InvalidDataException("Mojang Java 清单格式无效。");
        var platform = root.Obj("windows-x64");
        if (platform?.Arr(component) is not JsonArray installations || installations.Count == 0 ||
            installations[0] is not JsonObject entry || entry.Obj("manifest")?.Str("url") is not { } manifestUrl)
            throw new InvalidOperationException($"找不到适用于 Windows 的 Java 运行时 {component}。");

        var manifest = JsonNode.Parse(await _http.GetStringAsync(manifestUrl, cancellationToken)) as JsonObject
                       ?? throw new InvalidDataException("Java 运行时清单格式无效。");
        if (manifest.Obj("files") is not JsonObject files)
            throw new InvalidDataException("Java 运行时文件清单为空。");

        var destinationRoot = Path.Combine(_dataDirectory, "runtime", component);
        var rootFull = Path.GetFullPath(destinationRoot) + Path.DirectorySeparatorChar;
        var tasks = new List<JavaDownloadTask>();
        foreach (var pair in files)
        {
            if (pair.Value is not JsonObject item) continue;
            var relative = pair.Key.Replace('/', Path.DirectorySeparatorChar);
            var destination = Path.GetFullPath(Path.Combine(destinationRoot, relative));
            if (!destination.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Java 运行时清单包含不安全路径。");
            if (item.Str("type") == "directory")
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            if (item.Obj("downloads")?.Obj("raw") is not JsonObject raw ||
                raw.Str("url") is not { } url || raw.Str("sha1") is not { } sha1)
                continue;
            tasks.Add(new JavaDownloadTask(url, destination, sha1, raw["size"]?.GetValue<long>()));
        }

        var pending = tasks
            .Where(task => !(task.Size is > 0 && File.Exists(task.Path) &&
                             new FileInfo(task.Path).Length == task.Size.Value))
            .ToList();
        var skipped = tasks.Count - pending.Count;
        var allSizesKnown = pending.All(task => task.Size is > 0);
        var pendingBytes = allSizesKnown ? pending.Sum(task => task.Size!.Value) : 0;
        var meter = new DownloadBatchMeter(tasks.Count, skipped, pendingBytes);
        foreach (var task in pending)
        {
            if (File.Exists(task.Path + ".part"))
                meter.SetResumeOffset(task.Path, new FileInfo(task.Path + ".part").Length);
        }
        var errors = new ConcurrentQueue<Exception>();
        using var gate = new SemaphoreSlim(20);
        long lastReportTimestamp = 0;
        void Report(string file, bool force = false)
        {
            var now = Stopwatch.GetTimestamp();
            if (!force)
            {
                var previous = Interlocked.Read(ref lastReportTimestamp);
                if (Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromMilliseconds(110)) return;
                if (Interlocked.CompareExchange(ref lastReportTimestamp, now, previous) != previous) return;
            }
            progress?.Report(meter.Snapshot("正在下载 Java 运行时", file, "Mojang 官方"));
        }
        Report("准备连接", true);
        var workers = pending.Select(async task =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                await _http.DownloadFileAsync(task.Url, task.Path, task.Sha1, task.Size,
                    (received, total) =>
                    {
                        meter.Report(task.Path, received);
                        Report(Path.GetFileName(task.Path));
                    }, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
            }
            finally
            {
                gate.Release();
                meter.Complete(task.Path, task.Size ?? 0);
                Report(Path.GetFileName(task.Path));
            }
        });
        await Task.WhenAll(workers);
        if (!errors.IsEmpty)
            throw new AggregateException($"Java 运行时下载失败（{errors.Count} 个文件）。", errors);

        var javaPath = Path.Combine(destinationRoot, "bin", "java.exe");
        var runtime = await TryJavaAsync(javaPath, cancellationToken);
        if (runtime is null || runtime.MajorVersion != requiredMajor)
            throw new InvalidOperationException($"Java 运行时安装不完整，期望 Java {requiredMajor}。");
        await File.WriteAllTextAsync(Path.Combine(destinationRoot, ".kumo-ready"), DateTime.UtcNow.ToString("O"),
            cancellationToken);
        progress?.Report(new DownloadProgress("Java 运行时准备完成", tasks.Count, tasks.Count,
            $"Java {requiredMajor}", pendingBytes, pendingBytes > 0 ? pendingBytes : null,
            0, TimeSpan.Zero, "Mojang 官方"));
        LogService.Info($"已准备 Java {requiredMajor}：{javaPath}");
        return runtime;
    }

    private static async Task<JavaRuntime?> TryJavaAsync(string? path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (Directory.Exists(path))
        {
            var inDirectory = Path.Combine(path, "bin", "java.exe");
            if (File.Exists(inDirectory)) path = inDirectory;
            else
            {
                inDirectory = Path.Combine(path, "java.exe");
                if (File.Exists(inDirectory)) path = inDirectory;
            }
        }
        else if (Path.GetFileName(path).Equals("javaw.exe", StringComparison.OrdinalIgnoreCase))
        {
            var java = Path.Combine(Path.GetDirectoryName(path) ?? "", "java.exe");
            if (File.Exists(java)) path = java;
        }
        if (!File.Exists(path)) return null;

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(path, "-version")
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var text = await process.StandardError.ReadToEndAsync(cancellationToken);
            text += await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var major = ParseMajorVersion(text);
            return major > 0 ? new JavaRuntime(path, major, false) : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<List<JavaRuntime>> FindInstalledJavaAsync(CancellationToken cancellationToken)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return;
            var java = Path.Combine(directory, "bin", "java.exe");
            if (File.Exists(java)) candidates.Add(java);
        }

        Add(Environment.GetEnvironmentVariable("JAVA_HOME"));
        foreach (var root in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BellSoft")
                 })
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var child in Directory.EnumerateDirectories(root)) Add(child);
            }
            catch
            {
                // A protected installation folder is not fatal.
            }
        }

        try
        {
            using var where = Process.Start(new ProcessStartInfo("where.exe", "java.exe")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (where is not null)
            {
                var output = await where.StandardOutput.ReadToEndAsync(cancellationToken);
                await where.WaitForExitAsync(cancellationToken);
                foreach (var line in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
                    if (File.Exists(line.Trim())) candidates.Add(line.Trim());
            }
        }
        catch
        {
            // where.exe is unavailable on a few minimal Windows installations.
        }

        var result = new List<JavaRuntime>();
        foreach (var candidate in candidates)
        {
            var runtime = await TryJavaAsync(candidate, cancellationToken);
            if (runtime is not null) result.Add(runtime);
        }
        return result.GroupBy(x => (x.Executable, x.MajorVersion)).Select(x => x.First()).ToList();
    }

    public static int ParseMajorVersion(string versionOutput)
    {
        var marker = versionOutput.IndexOf("version \"", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return 0;
        var start = marker + "version \"".Length;
        var end = versionOutput.IndexOf('"', start);
        if (end <= start) return 0;
        var version = versionOutput[start..end];
        if (version.StartsWith("1.", StringComparison.Ordinal))
            version = version[2..];
        var dot = version.IndexOf('.');
        if (dot >= 0) version = version[..dot];
        return int.TryParse(version, out var major) ? major : 0;
    }

    private sealed record JavaDownloadTask(string Url, string Path, string Sha1, long? Size);
}
