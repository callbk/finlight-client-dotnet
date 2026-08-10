using System.Net.Http.Headers;
using System.Text.Json;
using Finlight.Json;
using Microsoft.Extensions.Logging;

namespace Finlight.Http;

/// <summary>
/// Performs authenticated REST requests with retry and backoff: retryable
/// statuses (429, 500, 502, 503, 504) are retried up to
/// <see cref="FinlightClientOptions.RetryCount"/> total attempts with
/// exponential backoff (500ms · 2^(attempt−1)), matching the sibling clients.
/// </summary>
internal sealed class ApiClient
{
    private static readonly HashSet<int> RetryableStatus = [429, 500, 502, 503, 504];
    private static readonly TimeSpan BaseRetryDelay = TimeSpan.FromMilliseconds(500);

    private readonly FinlightClientOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger _log;

    public ApiClient(FinlightClientOptions options, HttpClient http, ILogger log)
    {
        _options = options;
        _http = http;
        _log = log;
    }

    public async Task<TResponse> SendAsync<TResponse>(
        HttpMethod method,
        string path,
        string? query,
        object? body,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(_options.BaseUrl + path + (query is null ? "" : "?" + query));
        var payload = body is null
            ? null
            : JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), FinlightJson.Options);

        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.TryAddWithoutValidation("X-API-KEY", _options.ApiKey);
            request.Headers.TryAddWithoutValidation("User-Agent", ClientVersion.Value);
            if (payload is not null)
            {
                request.Content = new ByteArrayContent(payload)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
                };
            }

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(_options.Timeout);

            HttpResponseMessage response;
            try
            {
                response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, attemptCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"finlight: request timed out after {_options.Timeout.TotalSeconds:0.###}s.", exception);
            }

            using (response)
            {
                var statusCode = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsByteArrayAsync(attemptCts.Token).ConfigureAwait(false);
                    return JsonSerializer.Deserialize<TResponse>(content, FinlightJson.Options)
                        ?? throw new FinlightApiException(statusCode, response.ReasonPhrase, "null");
                }

                var errorBody = await response.Content.ReadAsStringAsync(attemptCts.Token).ConfigureAwait(false);
                if (RetryableStatus.Contains(statusCode) && attempt < _options.RetryCount)
                {
                    var delay = BaseRetryDelay * (1 << (attempt - 1));
                    _log.LogWarning(
                        "finlight: retrying request (status {Status}, attempt {Attempt}, delay {Delay})",
                        statusCode, attempt, delay);
                    await Task.Delay(delay, _options.TimeProvider, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new FinlightApiException(statusCode, response.ReasonPhrase, errorBody);
            }
        }
    }
}
