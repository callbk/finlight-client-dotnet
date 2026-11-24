using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Finlight.Internal;
using Microsoft.Extensions.Logging;

namespace Finlight;

/// <summary>Article search and lookups (<c>client.Articles</c>).</summary>
public sealed class ArticlesService
{
    private readonly HttpTransport _http;

    internal ArticlesService(HttpTransport http) => _http = http;

    /// <summary>
    /// <c>POST /v3/articles/search</c>: one page. Only the keys you set are sent; an empty
    /// <see cref="SearchParams"/> sends <c>{}</c>. Every call costs quota, also a failed one.
    /// </summary>
    /// <param name="parameters">The search; checked before the request.</param>
    /// <param name="cancellationToken">Cancels the call, retry and burst waits included.</param>
    /// <returns>The page.</returns>
    public async Task<ArticlePage> SearchAsync(SearchParams parameters, CancellationToken cancellationToken = default)
    {
        var body = Requests.SearchBody(parameters);
        return await SearchBodyAsync(body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Walks the cursor over several pages. The body is frozen now (later changes to your lists do
    /// not matter); later pages change only <c>cursor</c>. Stops at the end or after
    /// <see cref="SearchAllOptions.MaxPages"/> (default <see cref="FinlightConfig.MaxPages"/>), every page costs
    /// quota. Iterate the result once, by article or with <see cref="SearchAllResult.Pages"/>.
    /// </summary>
    /// <param name="parameters">The search; checked now, before any request.</param>
    /// <param name="options">Page limit.</param>
    /// <param name="cancellationToken">Cancels the iteration.</param>
    /// <returns>A single-use result.</returns>
    public SearchAllResult SearchAllAsync(
        SearchParams parameters,
        SearchAllOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var body = Requests.SearchBody(parameters);
        var maxPages = options?.MaxPages ?? _http.Config.MaxPages;
        if (maxPages < 1)
        {
            throw new FinlightValidationException("maxPages must be at least 1", "maxPages");
        }

        return new SearchAllResult(this, body, maxPages, _http.Log, cancellationToken);
    }

    /// <summary><c>GET /v3/articles/{id}</c>. A 404 is a <see cref="NotFoundException"/>; articles from before the id rollout are reachable only by link.</summary>
    /// <param name="id">The article id.</param>
    /// <param name="options">Extra blocks.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The article.</returns>
    public async Task<Article> GetAsync(string id, LookupOptions? options = null, CancellationToken cancellationToken = default)
    {
        Requests.RequireText(id, "id");
        var query = new List<KeyValuePair<string, string>>();
        if (Requests.IncludeQuery(options?.Include) is { } include)
        {
            query.Add(new("include", include));
        }

        var path = "/v3/articles/" + Uri.EscapeDataString(id) + Requests.Query(query);
        return await LookupAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>GET /v3/articles/by-link</c>: reaches every article, old ones included. A 404 is a <see cref="NotFoundException"/>.</summary>
    /// <param name="link">The article URL, exactly as stored.</param>
    /// <param name="options">Extra blocks.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The article.</returns>
    public async Task<Article> GetByLinkAsync(string link, LookupOptions? options = null, CancellationToken cancellationToken = default)
    {
        Requests.RequireText(link, "link");
        var query = new List<KeyValuePair<string, string>> { new("link", link) };
        if (Requests.IncludeQuery(options?.Include) is { } include)
        {
            query.Add(new("include", include));
        }

        return await LookupAsync("/v3/articles/by-link" + Requests.Query(query), cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ArticlePage> SearchBodyAsync(JsonObject body, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, FinlightJson.Options);
        var result = await _http
            .SendAsync(HttpMethod.Post, "/v3/articles/search", bytes, null, false, cancellationToken)
            .ConfigureAwait(false);
        return Decode.Page(result.Body, _http.Log);
    }

    private async Task<Article> LookupAsync(string path, CancellationToken cancellationToken)
    {
        var result = await _http.SendAsync(HttpMethod.Get, path, null, null, false, cancellationToken).ConfigureAwait(false);
        using var doc = Decode.Parse(result.Body);
        return Decode.Article(Decode.Field(doc.RootElement, "article", JsonValueKind.Object), "$.article");
    }
}

/// <summary>
/// The result of <c>SearchAllAsync</c>: iterate it once by article (<c>await foreach</c>) or by page
/// (<see cref="Pages"/>). A second iteration throws <see cref="FinlightValidationException"/>.
/// </summary>
public sealed class SearchAllResult : IAsyncEnumerable<Article>
{
    private readonly ArticlesService _articles;
    private readonly JsonObject _frozen;
    private readonly int _maxPages;
    private readonly ILogger _log;
    private readonly CancellationToken _callToken;
    private int _used;

    internal SearchAllResult(ArticlesService articles, JsonObject frozen, int maxPages, ILogger log, CancellationToken callToken)
    {
        _articles = articles;
        _frozen = frozen;
        _maxPages = maxPages;
        _log = log;
        _callToken = callToken;
    }

    /// <summary>The last <c>nextCursor</c> seen; after a stop at the page limit, pass it as <see cref="SearchParams.Cursor"/> to go on.</summary>
    public string? NextCursor { get; private set; }

    /// <summary>True when the pager stopped at the page limit while more pages exist.</summary>
    public bool Truncated { get; private set; }

    /// <summary>Pages fetched so far.</summary>
    public int PagesFetched { get; private set; }

    /// <summary>Iterates whole pages instead of articles.</summary>
    /// <returns>The pages.</returns>
    public IAsyncEnumerable<ArticlePage> Pages()
    {
        Claim();
        return PagesCoreAsync(default);
    }

    /// <inheritdoc />
    public IAsyncEnumerator<Article> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        Claim();
        return ArticlesCoreAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
    }

    private void Claim()
    {
        if (Interlocked.Exchange(ref _used, 1) != 0)
        {
            throw new FinlightValidationException(
                "a SearchAllResult can be iterated only once; call SearchAllAsync again for a new search");
        }
    }

    private async IAsyncEnumerable<Article> ArticlesCoreAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var page in PagesCoreAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var article in page.Articles)
            {
                yield return article;
            }
        }
    }

    /// <summary>One page; a cancellation arrives with the caller's token, not the internal linked one (R-RETRY-1).</summary>
    private async Task<ArticlePage> FetchAsync(JsonObject body, CancellationToken linked, CancellationToken enumeratorToken)
    {
        try
        {
            return await _articles.SearchBodyAsync(body, linked).ConfigureAwait(false);
        }
        catch (OperationCanceledException canceled) when (CallerToken(enumeratorToken) is { } caller)
        {
            throw new OperationCanceledException(canceled.Message, canceled, caller);
        }
    }

    private CancellationToken? CallerToken(CancellationToken enumeratorToken)
        => _callToken.IsCancellationRequested ? _callToken
            : enumeratorToken.IsCancellationRequested ? enumeratorToken
            : null;

    private async IAsyncEnumerable<ArticlePage> PagesCoreAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_callToken, cancellationToken);
        string? cursor = null;
        for (var number = 1; number <= _maxPages; number++)
        {
            var body = (JsonObject)_frozen.DeepClone();
            if (cursor is not null)
            {
                body["cursor"] = cursor;
            }

            var page = await FetchAsync(body, linked.Token, cancellationToken).ConfigureAwait(false);
            PagesFetched = number;
            var next = page.Pagination.HasMore ? page.Pagination.NextCursor : null;
            NextCursor = next;
            yield return page;
            if (next is null)
            {
                yield break;
            }

            if (number == _maxPages)
            {
                Truncated = true;
                _log.LogWarning(
                    "searchAll stopped at maxPages ({MaxPages}); more pages exist, continue with NextCursor", _maxPages);
                yield break;
            }

            cursor = next;
        }
    }
}

/// <summary>
/// Company master data (<c>client.Companies</c>). Needs the <c>entityAccess</c> entitlement (403 otherwise).
/// Every request costs 1 credit, like an article request (<c>ids=</c> with several companies too); a 304
/// revalidation and errors cost 1 credit too.
/// </summary>
public sealed class CompaniesService
{
    private readonly HttpTransport _http;

    internal CompaniesService(HttpTransport http) => _http = http;

    /// <summary><c>GET /v3/companies?q=…</c>: resolves a ticker, name, alias, ISIN or vendor notation. At most 10 results, no pages. Costs 1 credit.</summary>
    /// <param name="parameters"><c>q</c> and optional <c>mic</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The companies.</returns>
    public async Task<CompanyList> SearchAsync(CompanySearchParams parameters, CancellationToken cancellationToken = default)
    {
        Requests.NotNull(parameters, "parameters");
        var query = new List<KeyValuePair<string, string>> { new("q", Requests.RequireText(parameters.Q, "q", 200)) };
        if (parameters.Mic is not null)
        {
            query.Add(new("mic", Requests.RequireText(parameters.Mic, "mic", 32)));
        }

        return await ListAsync(query, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>GET /v3/companies?ids=…</c>: 1 to 10 ids. Unknown ids are left out; the order is not guaranteed. Costs 1 credit per request, not per id.</summary>
    /// <param name="ids">The company ids.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The companies.</returns>
    public async Task<CompanyList> GetManyAsync(IEnumerable<long> ids, CancellationToken cancellationToken = default)
    {
        var list = Requests.CompanyIds(ids);
        var joined = string.Join(',', list.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return await ListAsync([new("ids", joined)], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>GET /v3/companies/{companyId}</c>. The SDK keeps no cache: it sends <c>If-None-Match</c> only with your
    /// <see cref="CompanyGetOptions.IfNoneMatch"/>, and a 304 then returns null ("not modified").
    /// <see cref="CompanyRecord.GetCacheInfo"/> exposes the ETag and Cache-Control, so you can keep the record
    /// yourself. A 404 is a <see cref="NotFoundException"/>. Every call costs 1 credit, a 304 included: to save
    /// credits, keep the record and do not call again within its <c>max-age</c>.
    /// </summary>
    /// <param name="companyId">The company id.</param>
    /// <param name="options">Your own ETag.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The record, or null when your ETag is still current.</returns>
    public async Task<CompanyRecord?> GetAsync(long companyId, CompanyGetOptions? options = null, CancellationToken cancellationToken = default)
    {
        var id = Requests.CompanyId(companyId);
        var etag = options?.IfNoneMatch;
        if (etag is { Length: 0 })
        {
            throw new FinlightValidationException("ifNoneMatch must not be empty", "ifNoneMatch");
        }

        var headers = etag is null ? null : new Dictionary<string, string> { ["If-None-Match"] = etag };
        var path = "/v3/companies/" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var result = await _http
            .SendAsync(HttpMethod.Get, path, null, headers, allowNotModified: etag is not null, cancellationToken)
            .ConfigureAwait(false);
        if (result.Status == 304)
        {
            return null;
        }

        using var doc = Decode.Parse(result.Body);
        var record = Decode.CompanyRecord(Decode.Field(doc.RootElement, "company", JsonValueKind.Object), "$.company");
        record.Cache = new CacheInfo(RawHeader(result, "ETag"), RawHeader(result, "Cache-Control"));
        return record;
    }

    private static string? RawHeader(HttpResult result, string name)
        => result.Headers.NonValidated.TryGetValues(name, out var values) ? string.Join(", ", values) : null;

    private async Task<CompanyList> ListAsync(List<KeyValuePair<string, string>> query, CancellationToken cancellationToken)
    {
        var result = await _http
            .SendAsync(HttpMethod.Get, "/v3/companies" + Requests.Query(query), null, null, false, cancellationToken)
            .ConfigureAwait(false);
        using var doc = Decode.Parse(result.Body);
        var array = Decode.Field(doc.RootElement, "companies", JsonValueKind.Array);
        var (items, skipped) = Decode.Entries(array, "$.companies", Decode.CompanyRecord, _http.Log);
        return new CompanyList(items, skipped);
    }
}

/// <summary>The source catalog (<c>client.Sources</c>).</summary>
public sealed class SourcesService
{
    private readonly HttpTransport _http;

    internal SourcesService(HttpTransport http) => _http = http;

    /// <summary><c>GET /v3/sources</c>: the full catalog, sorted by domain. Costs 1 credit, errors included.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The sources.</returns>
    public async Task<SourceList> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = await _http.SendAsync(HttpMethod.Get, "/v3/sources", null, null, false, cancellationToken).ConfigureAwait(false);
        using var doc = Decode.Parse(result.Body);
        var array = Decode.Field(doc.RootElement, "sources", JsonValueKind.Array);
        var (items, skipped) = Decode.Entries(array, "$.sources", Decode.Source, _http.Log);
        return new SourceList(items, skipped);
    }
}
