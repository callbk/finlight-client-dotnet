// A webhook endpoint with ASP.NET Core. Reads FINLIGHT_WEBHOOK_SECRET (and PORT, default 8080).
// It reads the raw body, answers within 5 s and does the slow work afterwards.
using System.Collections.Concurrent;
using System.Threading.Channels;
using Finlight;

var secret = Environment.GetEnvironmentVariable("FINLIGHT_WEBHOOK_SECRET") ?? string.Empty;
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
var app = builder.Build();

var seen = new ConcurrentDictionary<string, bool>();
var work = Channel.CreateUnbounded<WebhookEvent>();

app.MapPost("/finlight-webhook", async (HttpRequest request) =>
{
    // The raw bytes, exactly as received: never parse and re-serialize before verifying.
    using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer);

    WebhookPayload payload;
    try
    {
        payload = Webhooks.ConstructEvent(buffer.ToArray(), request.Headers, secret);
    }
    catch (WebhookVerificationException error)
    {
        // 400, never 410: a 410 disables the webhook at once.
        Console.WriteLine($"rejected: {error.Reason}");
        return Results.StatusCode(400);
    }

    switch (payload)
    {
        case WrongPayloadVersion old:
            // A delivery in the old format, still queued after the switch: answer 2xx and keep it.
            Console.WriteLine($"old payload version, kept {old.Raw.Length} bytes ({old.Link})");
            return Results.Ok();
        case WebhookEvent e when !seen.TryAdd(e.EventId, true):
            Console.WriteLine($"duplicate {e.EventId}");
            return Results.Ok();
        case WebhookEvent e:
            await work.Writer.WriteAsync(e);
            return Results.Ok();
        default:
            return Results.Ok();
    }
});

// Slow work runs after the answer.
_ = Task.Run(async () =>
{
    await foreach (var e in work.Reader.ReadAllAsync())
    {
        Console.WriteLine($"{e.Event} {e.EventId}{(e.IsTest ? " (test)" : string.Empty)}: {e.Data.Title}");
    }
});

Console.WriteLine($"listening on http://127.0.0.1:{port}/finlight-webhook");
app.Run();
