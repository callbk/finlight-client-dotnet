using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;

namespace Finlight.Tests.Support;

/// <summary>An <see cref="HttpMock"/> plus a client on a fake clock.</summary>
internal sealed class Rest : IAsyncDisposable
{
    private Rest(HttpMock mock, FinlightClient client, FakeTimeProvider clock, FinlightConfig config)
    {
        Mock = mock;
        Client = client;
        Clock = clock;
        Config = config;
    }

    public HttpMock Mock { get; }

    public FinlightClient Client { get; }

    public FakeTimeProvider Clock { get; }

    public FinlightConfig Config { get; }

    public static async Task<Rest> StartAsync(Func<FinlightConfig, FinlightConfig>? configure = null, string apiKey = Kit.ApiKey)
    {
        var mock = await HttpMock.StartAsync();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.Zero));
        var config = new FinlightConfig
        {
            ApiKey = apiKey,
            BaseUrl = mock.Url + "/",
            WssUrl = "ws://127.0.0.1:9",
            TimeProvider = clock,
            Random = () => 0.5,
        };
        config = configure?.Invoke(config) ?? config;
        return new Rest(mock, new FinlightClient(config), clock, config);
    }

    /// <summary>Runs a call while moving the fake clock, so retry and burst waits pass without real waiting.</summary>
    public async Task<T> RunAsync<T>(Func<Task<T>> call, TimeSpan? step = null)
    {
        var task = call();
        var delta = step ?? TimeSpan.FromMilliseconds(250);
        var guard = 0;
        while (!task.IsCompleted)
        {
            await Task.Delay(1);
            if (!task.IsCompleted)
            {
                Clock.Advance(delta);
            }

            if (++guard > 20_000)
            {
                throw new TimeoutException("the call did not finish");
            }
        }

        return await task;
    }

    public Task RunAsync(Func<Task> call, TimeSpan? step = null)
        => RunAsync<bool>(async () => { await call(); return true; }, step);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Mock.DisposeAsync();
    }

    /// <summary>Maps kit <c>sdk.args</c> to a parameter type; an unknown key fails, as the type system does at a call site.</summary>
    public static T Args<T>(JsonNode? args)
        => (args ?? new JsonObject()).Deserialize<T>(new JsonSerializerOptions
        {
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        })!;
}
