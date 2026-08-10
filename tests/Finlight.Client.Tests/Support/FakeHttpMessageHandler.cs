using System.Net;
using System.Text;

namespace Finlight.Tests.Support;

/// <summary>Scripted HTTP handler: returns queued responses and records every request.</summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();

    public List<RecordedRequest> Requests { get; } = [];

    public sealed record RecordedRequest(
        HttpMethod Method,
        Uri? Uri,
        string? Body,
        string? ApiKey,
        string? UserAgent,
        string? ContentType);

    public void Enqueue(HttpStatusCode statusCode, string body = "{}")
        => _responses.Enqueue(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri,
            body,
            request.Headers.TryGetValues("X-API-KEY", out var keys) ? string.Join(",", keys) : null,
            request.Headers.TryGetValues("User-Agent", out var agents) ? string.Join(",", agents) : null,
            request.Content?.Headers.ContentType?.MediaType));

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("FakeHttpMessageHandler: no response queued.");
        }

        return _responses.Dequeue();
    }
}
