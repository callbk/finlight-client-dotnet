namespace Finlight.Tests.Support;

/// <summary>A fact that only runs when FINLIGHT_API_KEY is set.</summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FINLIGHT_API_KEY")))
        {
            Skip = "Set FINLIGHT_API_KEY to run integration tests.";
        }
    }
}
