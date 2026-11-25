// Lookups: an article by link and by id, a 404, company master data and the source catalog.
// Reads FINLIGHT_API_KEY, optionally FINLIGHT_BASE_URL and FINLIGHT_WSS_URL.
using Finlight;

using var client = new FinlightClient(new FinlightConfig
{
    ApiKey = Environment.GetEnvironmentVariable("FINLIGHT_API_KEY") ?? string.Empty,
    BaseUrl = Environment.GetEnvironmentVariable("FINLIGHT_BASE_URL") ?? "https://api.finlight.me",
    WssUrl = Environment.GetEnvironmentVariable("FINLIGHT_WSS_URL") ?? "wss://wss.finlight.me",
});

try
{
    // A recent link to look up.
    var recent = await client.Articles.SearchAsync(new SearchParams { Query = "ticker:NVDA", Limit = 1 });
    if (recent.Articles.Count == 0)
    {
        Console.WriteLine("no article found");
        return 0;
    }

    var byLink = await client.Articles.GetByLinkAsync(recent.Articles[0].Link, new LookupOptions { Include = [IncludeBlock.Content] });
    Console.WriteLine($"by link: {byLink.Title}");
    Console.WriteLine(byLink.ContentPresent
        ? $"content: {(byLink.Content.Value is { } text ? text.Length + " characters" : "none")}"
        : "content: not delivered (plan without fullArticleAccess)");

    var byId = await client.Articles.GetAsync(byLink.Id);
    Console.WriteLine($"by id: {byId.Id}");

    try
    {
        await client.Articles.GetAsync("AAAAAAAAAAAAAAAAAAAAAA");
    }
    catch (NotFoundException notFound)
    {
        // Articles from before the id rollout are reachable only by link.
        Console.WriteLine($"404: {notFound.Detail}");
    }

    // companies.* needs the entityAccess entitlement.
    var companies = await client.Companies.SearchAsync(new CompanySearchParams { Q = "NVDA", Mic = "XNAS" });
    foreach (var company in companies)
    {
        Console.WriteLine($"company {company.CompanyId}: {company.Name} ({company.PrimaryListing?.Ticker})");
    }

    if (companies.Count > 0)
    {
        var record = await client.Companies.GetAsync(companies[0].CompanyId);
        Console.WriteLine($"etag: {record?.GetCacheInfo()?.Etag}");
    }

    var sources = await client.Sources.ListAsync();
    Console.WriteLine($"sources: {sources.Count}");
    return 0;
}
catch (FinlightApiException error)
{
    Console.Error.WriteLine($"API error {error.Status} {error.Code} (requestId {error.RequestId}): {error.Detail}");
    return 1;
}
