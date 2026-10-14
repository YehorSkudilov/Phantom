using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Phantom.Api.Data;
using Phantom.Shared;

namespace Phantom.Api.Services;

public static class PhantomClaims
{
    public const string UserId = "uid";
    public const string MaxRating = "max_rating";
}

public class TokenService
{
    private readonly PhantomOptions _options;

    public TokenService(IOptions<PhantomOptions> options)
    {
        _options = options.Value;
        SigningKey = new SymmetricSecurityKey(LoadOrCreateKey(_options));
    }

    public SymmetricSecurityKey SigningKey { get; }

    public AuthResponse Issue(User user)
    {
        var expires = DateTime.UtcNow.AddDays(_options.TokenLifetimeDays);
        var claims = new List<Claim>
        {
            new(PhantomClaims.UserId, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
        };
        if (user.IsAdmin) claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        if (!string.IsNullOrEmpty(user.MaxContentRating)) claims.Add(new Claim(PhantomClaims.MaxRating, user.MaxContentRating));

        var token = new JwtSecurityToken(
            issuer: "phantom",
            audience: "phantom",
            claims: claims,
            expires: expires,
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));
        return new AuthResponse(new JwtSecurityTokenHandler().WriteToken(token), expires, ToDto(user));
    }

    public static UserDto ToDto(User u) =>
        new(u.Id, u.Username, u.IsAdmin, u.ParentUserId is not null, u.PinHash is not null, u.AvatarColor, u.MaxContentRating,
            u.ParentUserId ?? u.Id, u.Avatar);

    private static byte[] LoadOrCreateKey(PhantomOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.JwtSigningKey)) return Encoding.UTF8.GetBytes(options.JwtSigningKey.PadRight(32, '#'));

        Directory.CreateDirectory(options.DataDirectory);
        var path = Path.Combine(options.DataDirectory, "jwt.key");
        if (File.Exists(path)) return Convert.FromBase64String(File.ReadAllText(path));
        var key = RandomNumberGenerator.GetBytes(64);
        File.WriteAllText(path, Convert.ToBase64String(key));
        return key;
    }
}

public class PasswordService
{
    private readonly PasswordHasher<User> _hasher = new();

    public string Hash(User user, string secret) => _hasher.HashPassword(user, secret);

    public bool Verify(User user, string? hash, string secret) =>
        hash is not null && _hasher.VerifyHashedPassword(user, hash, secret) != PasswordVerificationResult.Failed;
}

public static class ClaimsPrincipalExtensions
{
    public static int UserId(this ClaimsPrincipal user) =>
        int.Parse(user.FindFirstValue(PhantomClaims.UserId) ?? throw new UnauthorizedAccessException());

    public static string? MaxContentRating(this ClaimsPrincipal user) => user.FindFirstValue(PhantomClaims.MaxRating);
}
