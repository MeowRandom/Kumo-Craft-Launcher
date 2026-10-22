using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kumo.Services;

public sealed class HttpService
{
    private readonly HttpClient _client;

    public HttpService(string userAgent = "KumoLauncher/1.0 (Windows Minecraft launcher)")
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            MaxConnectionsPerServer = 48
        };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        _client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip");
    }

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public async Task<T> GetJsonAsync<T>(string url, CancellationToken cancellationToken = default)
    {
        using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url),
            cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
               ?? throw new InvalidDataException("服务器返回了空数据。");
    }

    public async Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default)
    {
        using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url),
            cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<T> GetJsonWithHeadersAsync<T>(
        string url,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            foreach (var pair in headers) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            return request;
        }, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
               ?? throw new InvalidDataException("服务器返回了空数据。");
    }

    public async Task<T> PostJsonAsync<T>(string url, object body, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(body);
        using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
               ?? throw new InvalidDataException("服务器返回了空数据。");
    }

    public async Task<T> PostFormAsync<T>(
        string url,
        IEnumerable<KeyValuePair<string, string>> fields,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields)
        }, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
               ?? throw new InvalidDataException("服务器返回了空数据。");
    }

    public async Task<RawHttpResponse> PostFormRawAsync(
        string url,
        IEnumerable<KeyValuePair<string, string>> fields,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        if (headers is not null)
            foreach (var pair in headers) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        using var response = await _client.SendAsync(request, cancellationToken);
        return new RawHttpResponse((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    public async Task<RawHttpResponse> PostJsonRawAsync(
        string url,
        object body,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        if (headers is not null)
            foreach (var pair in headers) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        using var response = await _client.SendAsync(request, cancellationToken);
        return new RawHttpResponse((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    public async Task<RawHttpResponse> SendRawAsync(
        HttpMethod method,
        string url,
        HttpContent? content = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        if (headers is not null)
            foreach (var pair in headers) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        using var response = await _client.SendAsync(request, cancellationToken);
        return new RawHttpResponse((int)response.StatusCode,
            await response.Content.ReadAsStringAsync(cancellationToken));
    }

    public async Task<double> ProbeDownloadSpeedAsync(
        string url,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(7));
        var stopwatch = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, Math.Max(1024, maxBytes) - 1);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var buffer = new byte[32 * 1024];
        long received = 0;
        while (received < maxBytes)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, maxBytes - received)),
                timeout.Token);
            if (read <= 0) break;
            received += read;
        }
        stopwatch.Stop();
        if (received == 0) return 0;
        return received / Math.Max(0.15, stopwatch.Elapsed.TotalSeconds);
    }

    public async Task<TimeSpan> ProbeLatencyAsync(string url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(7));
        var stopwatch = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    public async Task<bool> DownloadFileAsync(
        string url,
        string destination,
        string? expectedSha1,
        long? expectedSize,
        Action<long, long?>? onProgress,
        CancellationToken cancellationToken,
        bool verifyExistingHash = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination) && expectedSize is > 0 &&
            new FileInfo(destination).Length == expectedSize.Value)
        {
            if (!verifyExistingHash || string.IsNullOrWhiteSpace(expectedSha1)) return false;
            await using var existingStream = File.OpenRead(destination);
            var existingHash = Convert.ToHexString(await SHA1.HashDataAsync(existingStream, cancellationToken));
            if (existingHash.Equals(expectedSha1, StringComparison.OrdinalIgnoreCase)) return false;
            TryDelete(destination);
        }

        var tempPath = destination + ".part";
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await DownloadAttemptAsync(url, tempPath, expectedSize, onProgress, cancellationToken);
                if (!string.IsNullOrWhiteSpace(expectedSha1))
                {
                    await using var stream = File.OpenRead(tempPath);
                    var actual = Convert.ToHexString(await SHA1.HashDataAsync(stream, cancellationToken));
                    if (!actual.Equals(expectedSha1, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"文件校验失败：{Path.GetFileName(destination)}");
                }
                File.Move(tempPath, destination, true);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (InvalidDataException ex)
            {
                lastError = ex;
                TryDelete(tempPath);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or HttpRequestException)
            {
                lastError = ex;
            }

            if (attempt < 2)
            {
                LogService.Warn($"正在重试 {Path.GetFileName(destination)}：{lastError?.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(450), cancellationToken);
            }
        }

        throw new IOException($"下载失败：{Path.GetFileName(destination)}", lastError);
    }

    private async Task DownloadAttemptAsync(
        string url,
        string tempPath,
        long? expectedSize,
        Action<long, long?>? onProgress,
        CancellationToken cancellationToken)
    {
        var offset = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;
        if (expectedSize is > 0 && offset > expectedSize.Value)
        {
            TryDelete(tempPath);
            offset = 0;
        }
        if (expectedSize is > 0 && offset == expectedSize.Value)
        {
            onProgress?.Invoke(offset, offset);
            return;
        }

        using var response = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            return request;
        }, cancellationToken, HttpCompletionOption.ResponseHeadersRead);

        var append = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append)
        {
            offset = 0;
            TryDelete(tempPath);
        }

        long? total = response.Content.Headers.ContentRange?.Length ??
                      (response.Content.Headers.ContentLength is { } contentLength
                          ? offset + contentLength
                          : expectedSize);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(tempPath, append ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.Read, 128 * 1024, true);
        var buffer = new byte[128 * 1024];
        var received = offset;
        var lastReport = DateTime.UtcNow;
        onProgress?.Invoke(received, total);
        int read;
        while ((read = await ReadWithIdleTimeoutAsync(input, buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
            if (onProgress is not null && (DateTime.UtcNow - lastReport).TotalMilliseconds >= 100)
            {
                onProgress(received, total);
                lastReport = DateTime.UtcNow;
            }
        }
        await output.FlushAsync(cancellationToken);
        onProgress?.Invoke(received, total);
    }

    private static async Task<int> ReadWithIdleTimeoutAsync(
        Stream input,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            return await input.ReadAsync(buffer, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("下载连接超过 45 秒没有收到数据。");
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var request = requestFactory();
            try
            {
                var response = await _client.SendAsync(request, completionOption, cancellationToken);
                if (response.IsSuccessStatusCode) return response;

                var body = completionOption == HttpCompletionOption.ResponseHeadersRead
                    ? string.Empty
                    : await response.Content.ReadAsStringAsync(cancellationToken);
                var statusCode = (int)response.StatusCode;
                var message = TryReadApiError(body) ?? $"HTTP {statusCode} {response.ReasonPhrase}";
                response.Dispose();

                if (statusCode is >= 500 or 429 && attempt < 3)
                {
                    lastError = new HttpRequestException(message);
                    await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt * attempt), cancellationToken);
                    continue;
                }
                throw new NonRetryableHttpException(message, response.StatusCode);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = new TimeoutException("网络请求超时。");
            }
            catch (NonRetryableHttpException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
            }

            if (attempt < 3)
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), cancellationToken);
        }

        throw new HttpRequestException($"无法连接到下载服务器：{lastError?.Message}", lastError);
    }

    private static string? TryReadApiError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error_description", out var description)) return description.GetString();
            if (root.TryGetProperty("description", out var description2)) return description2.GetString();
            if (root.TryGetProperty("error", out var error)) return error.GetString() ?? body[..Math.Min(body.Length, 240)];
        }
        catch
        {
            return body[..Math.Min(body.Length, 240)];
        }
        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A stale .part file is harmless and will be overwritten next time.
        }
    }

    private sealed class NonRetryableHttpException(string message, HttpStatusCode statusCode)
        : HttpRequestException(message, null, statusCode);
}

public sealed record RawHttpResponse(int StatusCode, string Body);
