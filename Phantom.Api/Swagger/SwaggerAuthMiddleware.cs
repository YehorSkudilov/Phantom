using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Phantom.Api.Swagger;

// Puts HTTP Basic auth in front of the Swagger UI and document.
public class SwaggerAuthMiddleware(RequestDelegate next, IOptions<SwaggerAuthOptions> options)
{
    const string Realm = "Swagger";
    readonly SwaggerAuthOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/swagger"))
        {
            await next(context);
            return;
        }

        var auth = context.Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            Challenge(context);
            return;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth["Basic ".Length..].Trim()));
        }
        catch (FormatException)
        {
            Challenge(context);
            return;
        }

        var parts = decoded.Split(':', 2);
        if (parts.Length != 2 || !FixedTimeEquals(parts[0], _options.Username) | !FixedTimeEquals(parts[1], _options.Password))
        {
            Challenge(context);
            return;
        }

        await next(context);
    }

    static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    static void Challenge(HttpContext context)
    {
        context.Response.Headers.WWWAuthenticate = $"Basic realm=\"{Realm}\"";
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    }
}
