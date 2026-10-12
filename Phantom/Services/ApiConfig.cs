using System.Reflection;

namespace Phantom.Services;

/// <summary>The Phantom server this build talks to.</summary>
public static class ApiConfig
{
    // Baked in at build time from the ApiBaseUrl MSBuild property / API_BASE_URL environment variable /
    // Phantom/.env, via [AssemblyMetadata] - see Phantom.csproj, which fails the build when none is set.
    public static string ServerUrl { get; } =
        typeof(ApiConfig).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "ApiBaseUrl")?.Value is { Length: > 0 } url
            ? url.TrimEnd('/')
            : throw new InvalidOperationException("This build has no API_BASE_URL.");
}
