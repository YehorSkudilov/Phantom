using System.Reflection;

namespace Phantom.Client.Services;

/// <summary>Where the app looks for a Phantom server before the user has picked one.</summary>
public static class ApiConfig
{
    // Baked in at build time from the ApiBaseUrl MSBuild property / API_BASE_URL environment variable /
    // src/Phantom.Client/.env, via [AssemblyMetadata] - see Phantom.Client.csproj. Empty when the build had none.
    public static string? DefaultServerUrl { get; } =
        typeof(ApiConfig).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "ApiBaseUrl")?.Value is { Length: > 0 } url
            ? url.TrimEnd('/')
            : null;
}
