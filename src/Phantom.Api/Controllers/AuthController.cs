using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Phantom.Api.Data;
using Phantom.Api.Services;
using Phantom.Shared;

namespace Phantom.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(PhantomDbContext db, TokenService tokens, PasswordService passwords, IOptions<PhantomOptions> options) : ControllerBase
{
    /// <summary>True until the first account (the server owner / admin) is created.</summary>
    [HttpGet("setup-required")]
    public async Task<bool> SetupRequired(CancellationToken ct) => !await db.Users.AnyAsync(ct);

    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || req.Password.Length < 6)
            return BadRequest("Username is required and password must be at least 6 characters.");

        var first = !await db.Users.AnyAsync(ct);
        var isAdminCaller = User.Identity?.IsAuthenticated == true && User.IsInRole("Admin");
        if (!first && !options.Value.AllowRegistration && !isAdminCaller)
            return Forbid();
        if (await db.Users.AnyAsync(u => u.Username == req.Username, ct))
            return Conflict("Username already taken.");

        var user = new User { Username = req.Username.Trim(), IsAdmin = first, AvatarColor = "#E5A00D" };
        user.PasswordHash = passwords.Hash(user, req.Password);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return tokens.Issue(user);
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest req, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == req.Username && u.ParentUserId == null, ct);
        if (user is null || !passwords.Verify(user, user.PasswordHash, req.Password)) return Unauthorized();
        return tokens.Issue(user);
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<UserDto>> Me(CancellationToken ct)
    {
        var user = await db.Users.FindAsync([User.UserId()], ct);
        return user is null ? Unauthorized() : TokenService.ToDto(user);
    }
}

/// <summary>Plex Home: an account owner plus managed profiles, each with their own watch history.</summary>
[ApiController]
[Authorize]
[Route("api/profiles")]
public class ProfilesController(PhantomDbContext db, TokenService tokens, PasswordService passwords) : ControllerBase
{
    private async Task<User?> OwnerAsync(CancellationToken ct)
    {
        var me = await db.Users.FindAsync([User.UserId()], ct);
        if (me?.ParentUserId is { } parent) me = await db.Users.FindAsync([parent], ct);
        return me;
    }

    [HttpGet]
    public async Task<ActionResult<List<UserDto>>> List(CancellationToken ct)
    {
        var owner = await OwnerAsync(ct);
        if (owner is null) return Unauthorized();
        var users = await db.Users.Where(u => u.Id == owner.Id || u.ParentUserId == owner.Id).OrderBy(u => u.Id).ToListAsync(ct);
        return users.Select(TokenService.ToDto).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<UserDto>> Create(CreateProfileRequest req, CancellationToken ct)
    {
        var me = await db.Users.FindAsync([User.UserId()], ct);
        if (me is null || me.ParentUserId is not null) return Forbid();
        if (await db.Users.AnyAsync(u => u.Username == req.Username, ct)) return Conflict("Name already taken.");

        var profile = new User
        {
            Username = req.Username.Trim(),
            ParentUserId = me.Id,
            AvatarColor = req.AvatarColor ?? "#3FA9F5",
            MaxContentRating = string.IsNullOrWhiteSpace(req.MaxContentRating) ? null : req.MaxContentRating,
        };
        if (!string.IsNullOrWhiteSpace(req.Pin)) profile.PinHash = passwords.Hash(profile, req.Pin);
        db.Users.Add(profile);
        await db.SaveChangesAsync(ct);
        return TokenService.ToDto(profile);
    }

    [HttpPost("switch")]
    public async Task<ActionResult<AuthResponse>> Switch(SwitchProfileRequest req, CancellationToken ct)
    {
        var owner = await OwnerAsync(ct);
        var target = await db.Users.FindAsync([req.ProfileId], ct);
        if (owner is null || target is null || (target.Id != owner.Id && target.ParentUserId != owner.Id)) return NotFound();

        // Switching to a PIN-protected profile (or back to the owner from a managed one) needs the PIN/password.
        if (target.PinHash is not null && !passwords.Verify(target, target.PinHash, req.Pin ?? "")) return Unauthorized();
        if (target.Id == owner.Id && User.UserId() != owner.Id && !passwords.Verify(owner, owner.PasswordHash, req.Pin ?? ""))
            return Unauthorized();
        return tokens.Issue(target);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var profile = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.ParentUserId == User.UserId(), ct);
        if (profile is null) return NotFound();
        db.Users.Remove(profile);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/users")]
public class UsersController(PhantomDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<List<UserDto>> List(CancellationToken ct) =>
        (await db.Users.OrderBy(u => u.Username).ToListAsync(ct)).Select(TokenService.ToDto).ToList();

    [HttpPut("{id:int}/admin")]
    public async Task<IActionResult> SetAdmin(int id, [FromQuery] bool value, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([id], ct);
        if (user is null || user.ParentUserId is not null) return NotFound();
        if (user.Id == User.UserId() && !value) return BadRequest("You cannot remove your own admin role.");
        user.IsAdmin = value;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        if (id == User.UserId()) return BadRequest("You cannot delete yourself.");
        var user = await db.Users.FindAsync([id], ct);
        if (user is null) return NotFound();
        db.Users.RemoveRange(db.Users.Where(u => u.ParentUserId == id));
        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
