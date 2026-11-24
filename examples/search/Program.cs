// One page of articles about NVIDIA with companies and events.
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
    var page = await client.Articles.SearchAsync(new SearchParams
    {
        Query = "ticker:NVDA",
        Languages = ["en"],
        Include = [IncludeBlock.Companies, IncludeBlock.Events],
        Limit = 10,
    });

    foreach (var article in page.Articles)
    {
        var time = article.PublishedAtTime?.ToString("u") ?? "(no time)";
        // companies is absent without entityAccess; [] means tagged, no company.
        var companies = article.CompaniesPresent
            ? string.Join(", ", article.Companies.Value!.Select(c => c.Name))
            : "(companies not delivered)";
        Console.WriteLine($"{time}  {article.Source.Domain}  {article.Title}  [{companies}]");
    }

    Console.WriteLine($"hasMore: {page.Pagination.HasMore}");
    return 0;
}
catch (FinlightApiException error)
{
    Console.Error.WriteLine($"API error {error.Status} {error.Code} (requestId {error.RequestId}): {error.Detail}");
    return 1;
}
