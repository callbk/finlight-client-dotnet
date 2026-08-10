using System.Reflection;

namespace Finlight;

/// <summary>
/// Reports this library's version as sent to the server in the User-Agent and
/// x-client-version headers, e.g. "dotnet/Finlight.Client@1.0.0".
/// </summary>
internal static class ClientVersion
{
    public static string Value { get; } = Compute();

    private static string Compute()
    {
        var assembly = typeof(ClientVersion).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "devel";
        var metadataStart = version.IndexOf('+');
        if (metadataStart >= 0)
        {
            version = version[..metadataStart];
        }

        return $"dotnet/Finlight.Client@{version}";
    }
}
