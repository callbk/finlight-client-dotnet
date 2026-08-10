using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Finlight.Tests;

/// <summary>
/// Mirrors the Go client's webhook_test.go scenarios so all clients verify
/// webhooks identically.
/// </summary>
public class WebhookTests
{
    private const string Secret = "test_secret_key";

    private const string ValidPayload = """
        {
            "link": "https://example.com/article",
            "title": "Test Article",
            "publishDate": "2024-01-01T00:00:00Z",
            "source": "example.com",
            "language": "en",
            "sentiment": "positive",
            "confidence": "0.95",
            "summary": "This is a test article",
            "companies": [
                {"companyId": 1, "confidence": "0.90", "name": "Apple Inc.", "ticker": "AAPL", "exchange": "NASDAQ"}
            ]
        }
        """;

    private static string Sign(string payload, string secret, string? timestamp = null)
    {
        var message = string.IsNullOrEmpty(timestamp) ? payload : timestamp + "." + payload;
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(message));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Now(TimeSpan? offset = null)
        => DateTimeOffset.UtcNow.Add(offset ?? TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture);

    [Fact]
    public void ValidSignatureAndTimestamp_ReturnsArticle()
    {
        var timestamp = Now();
        var signature = "sha256=" + Sign(ValidPayload, Secret, timestamp);

        var article = FinlightWebhooks.ConstructEvent(ValidPayload, signature, Secret, timestamp);

        Assert.Equal("Test Article", article.Title);
        Assert.Equal("https://example.com/article", article.Link);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), article.PublishDate);
        Assert.Equal(0.95, article.Confidence);
        var company = Assert.Single(article.Companies!);
        Assert.Equal(0.90, company.Confidence);
    }

    [Fact]
    public void ValidSignatureWithoutTimestamp_ReturnsArticle()
    {
        var signature = "sha256=" + Sign(ValidPayload, Secret);

        var article = FinlightWebhooks.ConstructEvent(ValidPayload, signature, Secret);

        Assert.Equal("Test Article", article.Title);
        Assert.Equal(0.95, article.Confidence);
    }

    [Fact]
    public void SignatureWithoutPrefix_IsAccepted()
    {
        var timestamp = Now();
        var signature = Sign(ValidPayload, Secret, timestamp);

        var article = FinlightWebhooks.ConstructEvent(ValidPayload, signature, Secret, timestamp);

        Assert.Equal("Test Article", article.Title);
    }

    [Fact]
    public void ByteAndStringOverloads_Agree()
    {
        var signature = "sha256=" + Sign(ValidPayload, Secret);

        var fromBytes = FinlightWebhooks.ConstructEvent(Encoding.UTF8.GetBytes(ValidPayload), signature, Secret);
        var fromString = FinlightWebhooks.ConstructEvent(ValidPayload, signature, Secret);

        Assert.Equal(fromBytes.Link, fromString.Link);
    }

    [Fact]
    public void InvalidSignature_Throws()
    {
        var exception = Assert.Throws<FinlightWebhookVerificationException>(
            () => FinlightWebhooks.ConstructEvent(ValidPayload, "sha256=invalid_signature", Secret, Now()));

        Assert.Equal("Invalid webhook signature", exception.Message);
    }

    [Fact]
    public void MismatchedSecret_Throws()
    {
        var timestamp = Now();
        var signature = "sha256=" + Sign(ValidPayload, "wrong_secret", timestamp);

        var exception = Assert.Throws<FinlightWebhookVerificationException>(
            () => FinlightWebhooks.ConstructEvent(ValidPayload, signature, Secret, timestamp));

        Assert.Equal("Invalid webhook signature", exception.Message);
    }

    [Fact]
    public void UppercasedSignature_Throws()
    {
        var signature = "sha256=" + Sign(ValidPayload, Secret).ToUpperInvariant();

        var exception = Assert.Throws<FinlightWebhookVerificationException>(
            () => FinlightWebhooks.ConstructEvent(ValidPayload, signature, Secret));

        Assert.Equal("Invalid webhook signature", exception.Message);
    }

    [Theory]
    [InlineData(-6)]
    [InlineData(6)]
    public void TimestampOutsideTolerance_Throws(int offsetMinutes)
    {
        var timestamp = Now(TimeSpan.FromMinutes(offsetMinutes));
        var signature = "sha256=" + Sign(ValidPayload, Secret, timestamp);

        var exception = Assert.Throws<FinlightWebhookVerificationException>(
            () => FinlightWebhooks.ConstructEvent(ValidPayload, signature, Secret, timestamp));

        Assert.Equal("Webhook timestamp outside allowed tolerance", exception.Message);
    }

    [Fact]
    public void TimestampWithinTolerance_IsAccepted()
    {
        var timestamp = Now(TimeSpan.FromMinutes(-4));
        var signature = "sha256=" + Sign(ValidPayload, Secret, timestamp);

        var article = FinlightWebhooks.ConstructEvent(ValidPayload, signature, Secret, timestamp);

        Assert.Equal("Test Article", article.Title);
    }

    [Fact]
    public void InvalidTimestampFormat_Throws()
    {
        const string Timestamp = "not-a-timestamp";
        var signature = "sha256=" + Sign(ValidPayload, Secret, Timestamp);

        var exception = Assert.Throws<FinlightWebhookVerificationException>(
            () => FinlightWebhooks.ConstructEvent(ValidPayload, signature, Secret, Timestamp));

        Assert.Equal("Invalid timestamp format", exception.Message);
    }

    [Fact]
    public void InvalidJsonWithValidSignature_Throws()
    {
        const string RawBody = "invalid json";
        var signature = "sha256=" + Sign(RawBody, Secret);

        var exception = Assert.Throws<FinlightWebhookVerificationException>(
            () => FinlightWebhooks.ConstructEvent(RawBody, signature, Secret));

        Assert.Equal("Invalid JSON payload", exception.Message);
    }

    [Fact]
    public void ComplexPayload_ParsesAllCompanies()
    {
        const string Payload = """
            {
                "link": "https://example.com/complex-article",
                "title": "Complex Article with Multiple Companies",
                "publishDate": "2024-01-01T12:30:00Z",
                "source": "financial-news.com",
                "language": "en",
                "sentiment": "neutral",
                "confidence": "0.85",
                "summary": "A comprehensive analysis of market trends",
                "images": ["https://example.com/image1.jpg", "https://example.com/image2.jpg"],
                "content": "Full article content here...",
                "companies": [
                    {"companyId": 1, "confidence": "0.95", "country": "US", "exchange": "NASDAQ",
                     "industry": "Technology", "sector": "Software", "name": "Apple Inc.", "ticker": "AAPL",
                     "isin": "US0378331005", "openfigi": "BBG000B9XRY4"},
                    {"companyId": 2, "confidence": "0.88", "name": "Microsoft Corporation", "ticker": "MSFT"}
                ]
            }
            """;
        var signature = "sha256=" + Sign(Payload, Secret);

        var article = FinlightWebhooks.ConstructEvent(Payload, signature, Secret);

        Assert.Equal(0.85, article.Confidence);
        Assert.NotNull(article.Companies);
        Assert.Equal(2, article.Companies.Count);
        Assert.Equal(0.95, article.Companies[0].Confidence);
        Assert.Equal(0.88, article.Companies[1].Confidence);
        Assert.Equal("US0378331005", article.Companies[0].Isin);
    }
}
