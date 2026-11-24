namespace Finlight.LiveTests;

/// <summary>Skips unless all three variables are set; fails at once for a production host (R-TEST-2).</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Live.Missing() is { } missing)
        {
            Skip = $"live tests need {missing}";
        }
    }
}

internal static class Live
{
    private static readonly string[] Variables = ["FINLIGHT_API_KEY", "FINLIGHT_BASE_URL", "FINLIGHT_WSS_URL"];

    public static string? Missing()
        => Variables.FirstOrDefault(v => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v)));

    public static FinlightClient Client()
    {
        var baseUrl = Environment.GetEnvironmentVariable("FINLIGHT_BASE_URL")!;
        var wssUrl = Environment.GetEnvironmentVariable("FINLIGHT_WSS_URL")!;
        foreach (var url in new[] { baseUrl, wssUrl })
        {
            var host = new Uri(url).Host;
            if (!host.Contains(".dev.", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"live tests run against dev only; refusing host {host}");
            }
        }

        return new FinlightClient(new FinlightConfig
        {
            ApiKey = Environment.GetEnvironmentVariable("FINLIGHT_API_KEY")!,
            BaseUrl = baseUrl,
            WssUrl = wssUrl,
            MaxRetries = 1,
        });
    }
}

/// <summary>A small smoke run: a handful of REST calls and one short stream.</summary>
public sealed class LiveTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [LiveFact]
    public async Task Rest()
    {
        using var client = Live.Client();
        var sources = await client.Sources.ListAsync();
        Assert.NotEmpty(sources);
        Assert.Empty(sources.Skipped);

        var page = await client.Articles.SearchAsync(new SearchParams
        {
            Query = "ticker:NVDA",
            Languages = ["en"],
            Include = [IncludeBlock.Companies, IncludeBlock.Events],
            Limit = 3,
        });
        Assert.Empty(page.Skipped);
        Assert.InRange(page.Articles.Count, 1, 3);
        var first = page.Articles[0];
        Assert.NotNull(first.PublishedAtTime);

        var byLink = await client.Articles.GetByLinkAsync(first.Link, new LookupOptions { Include = [IncludeBlock.Companies] });
        Assert.Equal(first.Id, byLink.Id);
        var byId = await client.Articles.GetAsync(first.Id);
        Assert.Equal(first.Link, byId.Link);
        var notFound = await Assert.ThrowsAsync<NotFoundException>(() => client.Articles.GetAsync("AAAAAAAAAAAAAAAAAAAAAA"));
        Assert.StartsWith("req_", notFound.RequestId, StringComparison.Ordinal);

        var all = client.Articles.SearchAllAsync(new SearchParams { Query = "ticker:NVDA", Limit = 2 }, new SearchAllOptions { MaxPages = 2 });
        var count = 0;
        await foreach (var _ in all)
        {
            count++;
        }

        Assert.InRange(count, 1, 4);
        Assert.InRange(all.PagesFetched, 1, 2);
        output.WriteLine($"sources {sources.Count}; search {page.Articles.Count} (companies present: {first.CompaniesPresent}, events present: {first.EventsPresent}); searchAll {count} articles in {all.PagesFetched} pages");

        try
        {
            var companies = await client.Companies.SearchAsync(new CompanySearchParams { Q = "NVDA" });
            if (companies.Count > 0)
            {
                var record = await client.Companies.GetAsync(companies[0].CompanyId);
                var etag = record!.GetCacheInfo()!.Etag;
                Assert.NotNull(etag);
                var again = await client.Companies.GetAsync(companies[0].CompanyId, new CompanyGetOptions { IfNoneMatch = etag });
                Assert.Null(again);
                output.WriteLine($"companies {companies.Count}; etag revalidated: {again is null}");
            }
        }
        catch (ForbiddenException)
        {
            output.WriteLine("companies: 403 (no entityAccess)");
            // The key's plan has no entityAccess.
        }
    }

    [LiveFact]
    public async Task Stream()
    {
        using var client = Live.Client();
        AdmittedInfo? admitted = null;
        var errors = new List<FinlightException>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var stream = client.StreamAsync(
            new StreamParams { Query = "ticker:NVDA", Include = [IncludeBlock.Companies] },
            new StreamOptions { OnAdmitted = a => admitted = a, OnError = errors.Add });
        StreamEvent? first = null;
        try
        {
            await foreach (var item in stream.WithCancellation(cts.Token))
            {
                if (item.Kind != StreamEventKind.Other)
                {
                    first = item;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // No initial push within 30 s; admitted is enough.
        }

        output.WriteLine($"stream: admitted {admitted is not null}, first item {first?.Kind.Value ?? "none"} {first?.Data?.Id}");
        Assert.NotNull(admitted);
        Assert.NotNull(admitted!.LeaseId);
        Assert.Empty(errors);
        if (first is not null)
        {
            Assert.Equal(StreamEventKind.Created, first.Kind);
            Assert.NotNull(first.Data);
        }

        Assert.True(stream.Closed);
    }
}
