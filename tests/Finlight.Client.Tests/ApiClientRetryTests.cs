using System.Net;
using Finlight.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Finlight.Tests;

public class ApiClientRetryTests
{
    private const string EmptyPage = """{"status":"ok","page":1,"pageSize":20,"articles":[]}""";

    private static (FinlightClient Client, FakeHttpMessageHandler Handler, FakeTimeProvider Time) CreateClient()
    {
        var handler = new FakeHttpMessageHandler();
        var time = new FakeTimeProvider();
        var options = new FinlightClientOptions { ApiKey = "test-key", TimeProvider = time };
        var client = new FinlightClient(options, new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });
        return (client, handler, time);
    }

    /// <summary>Advances fake time until the task completes, letting continuations run in between.</summary>
    private static async Task<T> CompleteAsync<T>(Task<T> task, FakeTimeProvider time)
    {
        for (var i = 0; i < 200 && !task.IsCompleted; i++)
        {
            await Task.Delay(10);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        return await task;
    }

    [Fact]
    public async Task TransientServerErrors_AreRetried()
    {
        var (client, handler, time) = CreateClient();
        handler.Enqueue(HttpStatusCode.InternalServerError, "boom");
        handler.Enqueue(HttpStatusCode.BadGateway, "boom");
        handler.Enqueue(HttpStatusCode.OK, EmptyPage);

        var response = await CompleteAsync(client.Articles.FetchArticlesAsync(new GetArticlesParams()), time);

        Assert.Equal("ok", response.Status);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task RateLimited_ThrowsAfterExactlyRetryCountAttempts()
    {
        var (client, handler, time) = CreateClient();
        handler.Enqueue(HttpStatusCode.TooManyRequests, "slow down");
        handler.Enqueue(HttpStatusCode.TooManyRequests, "slow down");
        handler.Enqueue(HttpStatusCode.TooManyRequests, "slow down");

        var task = client.Articles.FetchArticlesAsync(new GetArticlesParams());
        var exception = await Assert.ThrowsAsync<FinlightApiException>(() => CompleteAsync(task, time));

        Assert.Equal(429, exception.StatusCode);
        Assert.Equal("slow down", exception.Body);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task ClientError_IsNotRetried()
    {
        var (client, handler, time) = CreateClient();
        handler.Enqueue(HttpStatusCode.BadRequest, """{"message":"bad query"}""");

        var task = client.Articles.FetchArticlesAsync(new GetArticlesParams());
        var exception = await Assert.ThrowsAsync<FinlightApiException>(() => CompleteAsync(task, time));

        Assert.Equal(400, exception.StatusCode);
        Assert.Contains("bad query", exception.Body);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Requests_CarryAuthAndVersionHeaders()
    {
        var (client, handler, time) = CreateClient();
        handler.Enqueue(HttpStatusCode.OK, EmptyPage);

        await CompleteAsync(client.Articles.FetchArticlesAsync(new GetArticlesParams { PageSize = 5 }), time);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.finlight.me/v2/articles", request.Uri?.ToString());
        Assert.Equal("test-key", request.ApiKey);
        Assert.StartsWith("dotnet/Finlight.Client@", request.UserAgent);
        Assert.Equal("application/json", request.ContentType);
        Assert.Contains("\"pageSize\":5", request.Body);
    }
}
