using Microsoft.Extensions.Options;

namespace Phantom.Api.Swagger;

public class SwaggerAuthOptions
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public class ValidateSwaggerAuthOptions : IValidateOptions<SwaggerAuthOptions>
{
    public ValidateOptionsResult Validate(string? name, SwaggerAuthOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Username))
            errors.Add("SwaggerAuth:Username must be configured and non-empty.");

        if (string.IsNullOrWhiteSpace(options.Password))
            errors.Add("SwaggerAuth:Password must be configured and non-empty.");

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}
