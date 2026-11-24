using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Finlight.Internal;

/// <summary>Version and identification headers (R-CONFIG-1).</summary>
internal static class ClientInfo
{
    /// <summary>The SDK version without a leading <c>v</c> and without build metadata.</summary>
    public static string Version { get; } = ReadVersion();

    public static string UserAgent { get; } = $"finlight-client-dotnet/{Version}";

    private static string ReadVersion()
    {
        var assembly = typeof(ClientInfo).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            version = version[..plus];
        }

        return version.TrimStart('v');
    }
}

/// <summary>The answer of one REST call.</summary>
internal sealed record HttpResult(int Status, HttpResponseHeaders Headers, byte[] Body, string RequestId);

/// <summary>REST transport: headers, size limit, timeouts and the retry loop (R-RETRY-1).</summary>
internal sealed class HttpTransport : IDisposable
{
    private readonly FinlightConfig _config;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ILogger _log;
    private readonly string _baseUrl;

    public HttpTransport(FinlightConfig config, ILogger log)
    {
        _config = config;
        _log = log;
        _baseUrl = config.BaseUrl.TrimEnd('/');
        if (config.HttpClient is { } injected)
        {
            _http = injected;
            _ownsHttp = false;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AllowAutoRedirect = true,
            };
            _http = new HttpClient(handler, disposeHandler: true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            _ownsHttp = true;
        }
    }

    public FinlightConfig Config => _config;

    public ILogger Log => _log;

    public async Task<HttpResult> SendAsync(
        HttpMethod method,
        string pathAndQuery,
        byte[]? body,
        IReadOnlyDictionary<string, string>? headers,
        bool allowNotModified,
        CancellationToken cancellationToken)
    {
        var time = _config.TimeProvider;
        var requestId = "req_" + Guid.NewGuid().ToString("D");
        var started = time.GetTimestamp();
        var label = $"{method} {pathAndQuery.Split('?')[0]}";
        var retries = 0;
        var burstRetried = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attemptTimeout = _ownsHttp ? _config.Timeout : System.Threading.Timeout.InfiniteTimeSpan;
            if (_config.TotalTimeout is { } total)
            {
                var left = total - time.GetElapsedTime(started);
                if (left <= TimeSpan.Zero)
                {
                    throw new FinlightTimeoutException($"{label}: TotalTimeout ({total}) reached");
                }

                if (attemptTimeout == System.Threading.Timeout.InfiniteTimeSpan || left < attemptTimeout)
                {
                    attemptTimeout = left;
                }
            }

            FinlightException failure;
            try
            {
                var (status, responseHeaders, contentType, bytes) =
                    await AttemptAsync(method, pathAndQuery, body, headers, requestId, attemptTimeout, cancellationToken)
                        .ConfigureAwait(false);
                if (status < 300 || (status == 304 && allowNotModified))
                {
                    _log.LogDebug("{Label} -> {Status} ({RequestId})", label, status, requestId);
                    return new HttpResult(status, responseHeaders, bytes, requestId);
                }

                if (status < 400)
                {
                    throw new FinlightDecodeException($"{label}: unexpected HTTP {status}");
                }

                var retryAfter = ErrorParser.ParseRetryAfter(
                    responseHeaders.TryGetValues("Retry-After", out var values) ? values.FirstOrDefault() : null,
                    time.GetUtcNow());
                var apiError = ErrorParser.Parse(status, contentType, Encoding.UTF8.GetString(bytes), requestId, retryAfter);
                _log.LogDebug("{Label} -> {Status} {Code} ({RequestId})", label, status, apiError.Code, requestId);
                var retryClass = ErrorParser.Classify(apiError);
                if (retryClass == RetryClass.Burst && _config.RetryOnBurst == RetryOnBurst.Once && !burstRetried)
                {
                    burstRetried = true;
                    var wait = retryAfter ?? _config.BurstWait;
                    _log.LogWarning(
                        "{Label}: burst rate limit hit, retrying once in {Wait} ({RequestId})", label, wait, requestId);
                    await WaitAsync(wait, started, apiError, label, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (retryClass != RetryClass.Backoff || retries >= _config.MaxRetries)
                {
                    throw apiError;
                }

                failure = apiError;
            }
            catch (AttemptTimeoutException timeout)
            {
                failure = new FinlightTimeoutException($"{label}: no response within {attemptTimeout}", timeout.InnerException);
                if (retries >= _config.MaxRetries)
                {
                    throw failure;
                }
            }
            catch (HttpRequestException network)
            {
                failure = new FinlightNetworkException($"{label}: request failed ({network.Message})", network);
                if (retries >= _config.MaxRetries)
                {
                    throw failure;
                }
            }
            catch (IOException network)
            {
                failure = new FinlightNetworkException($"{label}: request failed ({network.Message})", network);
                if (retries >= _config.MaxRetries)
                {
                    throw failure;
                }
            }

            var delay = Backoff(retries);
            retries++;
            _log.LogWarning(
                "{Label}: {Error}; retry {Retry}/{Max} in {Delay} ({RequestId})",
                label, failure.Message, retries, _config.MaxRetries, delay, requestId);
            await WaitAsync(delay, started, failure, label, cancellationToken).ConfigureAwait(false);
        }
    }

    private TimeSpan Backoff(int retry)
    {
        var baseMs = _config.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(retry, 16));
        var capped = Math.Min(_config.RetryMaxDelay.TotalMilliseconds, baseMs);
        return TimeSpan.FromMilliseconds(Math.Round(capped * (0.5 + (_config.Random() * 0.5))));
    }

    private async Task WaitAsync(TimeSpan delay, long started, FinlightException last, string label, CancellationToken cancellationToken)
    {
        var time = _config.TimeProvider;
        if (_config.TotalTimeout is { } total && time.GetElapsedTime(started) + delay > total)
        {
            throw new FinlightTimeoutException($"{label}: TotalTimeout ({total}) reached", last);
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(int Status, HttpResponseHeaders Headers, string? ContentType, byte[] Body)> AttemptAsync(
        HttpMethod method,
        string pathAndQuery,
        byte[]? body,
        IReadOnlyDictionary<string, string>? headers,
        string requestId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, _baseUrl + pathAndQuery);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, application/problem+json");
        request.Headers.TryAddWithoutValidation("User-Agent", ClientInfo.UserAgent);
        request.Headers.TryAddWithoutValidation("x-client-version", ClientInfo.Version);
        request.Headers.TryAddWithoutValidation("X-API-KEY", _config.ApiKey);
        request.Headers.TryAddWithoutValidation("X-Request-Id", requestId);
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != System.Threading.Timeout.InfiniteTimeSpan)
        {
            attemptCts.CancelAfter(timeout);
        }

        try
        {
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token)
                .ConfigureAwait(false);
            var bytes = await ReadLimitedAsync(response.Content, attemptCts.Token).ConfigureAwait(false);
            return ((int)response.StatusCode, response.Headers, response.Content.Headers.ContentType?.ToString(), bytes);
        }
        catch (OperationCanceledException canceled) when (cancellationToken.IsCancellationRequested)
        {
            // The caller's cancellation, with the caller's token.
            throw new OperationCanceledException(canceled.Message, canceled, cancellationToken);
        }
        catch (OperationCanceledException canceled)
        {
            // Our attempt timer, or the timeout of an injected HttpClient: not the caller's cancellation.
            throw new AttemptTimeoutException(canceled);
        }
    }

    private async Task<byte[]> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var limit = _config.MaxResponseBytes;
        if (content.Headers.ContentLength is { } declared && declared > limit)
        {
            throw new FinlightDecodeException($"the response body is larger than MaxResponseBytes ({limit})");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > limit)
            {
                throw new FinlightDecodeException($"the response body is larger than MaxResponseBytes ({limit})");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private sealed class AttemptTimeoutException(Exception inner) : Exception("attempt timeout", inner);
}
