using Finlight.Http;

namespace Finlight;

/// <summary>Lists the news sources available through the API.</summary>
public sealed class SourceService
{
    private readonly ApiClient _api;

    internal SourceService(ApiClient api) => _api = api;

    /// <summary>Returns all sources with their availability flags.</summary>
    /// <exception cref="FinlightApiException">The server returned a non-2xx response (after retries).</exception>
    public async Task<IReadOnlyList<Source>> GetSourcesAsync(CancellationToken cancellationToken = default)
        => await _api
            .SendAsync<List<Source>>(HttpMethod.Get, "/v2/sources", null, null, cancellationToken)
            .ConfigureAwait(false);
}
