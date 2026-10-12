using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Phantom.Api.Data;
using Phantom.Api.Services;
using Phantom.Shared;

namespace Phantom.Api.Controllers;

/// <summary>
/// Signing in a TV with a code instead of typing a password on a remote: the TV starts a pairing and shows its code,
/// someone signed in on another device approves the code, and the TV, polling, receives that account's sign-in.
/// </summary>
[ApiController]
[Route("api/auth/pair")]
public class PairingController(PhantomDbContext db, TokenService tokens) : ControllerBase
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    // No 0/O or 1/I, so the code reads unambiguously off a TV.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private sealed class Pairing
    {
        public required string Code { get; init; }
        public required string DeviceName { get; init; }
        public DateTime ExpiresAt { get; init; }
        public AuthResponse? Approved { get; set; }
    }

    // Keyed by the pairing id, which only the TV knows; codes are what people type.
    private static readonly ConcurrentDictionary<string, Pairing> Pairings = new();

    /// <summary>Called by the TV: a new code to show, and the id it polls with.</summary>
    [AllowAnonymous]
    [HttpPost("start")]
    public PairingStartResponse Start(PairingStartRequest req)
    {
        Prune();
        string code;
        do code = string.Concat(Enumerable.Range(0, 6).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]));
        while (Pairings.Values.Any(p => p.Code == code));

        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var pairing = new Pairing
        {
            Code = code,
            DeviceName = string.IsNullOrWhiteSpace(req.DeviceName) ? "TV" : req.DeviceName.Trim(),
            ExpiresAt = DateTime.UtcNow + Lifetime,
        };
        Pairings[id] = pairing;
        return new PairingStartResponse(id, code, pairing.ExpiresAt);
    }

    /// <summary>Polled by the TV: 202 while waiting, the sign-in once approved, 404 once expired.</summary>
    [AllowAnonymous]
    [HttpGet("{id}")]
    public ActionResult<AuthResponse> Poll(string id)
    {
        if (!Pairings.TryGetValue(id, out var pairing) || pairing.ExpiresAt < DateTime.UtcNow)
        {
            Pairings.TryRemove(id, out _);
            return NotFound();
        }
        if (pairing.Approved is null) return Accepted();
        Pairings.TryRemove(id, out _); // a sign-in is handed out once
        return pairing.Approved;
    }

    /// <summary>Called from a signed-in device: signs the TV showing this code in to the same account.</summary>
    [Authorize]
    [HttpPost("approve")]
    public async Task<ActionResult<PairingApprovedResponse>> Approve(PairingApproveRequest req, CancellationToken ct)
    {
        var code = (req.Code ?? "").Trim().ToUpperInvariant().Replace("-", "").Replace(" ", "");
        var match = Pairings.FirstOrDefault(p => p.Value.Code == code && p.Value.ExpiresAt > DateTime.UtcNow && p.Value.Approved is null);
        if (match.Value is null) return NotFound("That code isn't valid or has expired. Check the TV for a new one.");

        // The TV signs in to the account (not the profile the approver is on); it then asks who's watching.
        var me = await db.Users.FindAsync([User.UserId()], ct);
        if (me?.ParentUserId is { } parent) me = await db.Users.FindAsync([parent], ct);
        if (me is null) return Unauthorized();

        match.Value.Approved = tokens.Issue(me);
        return new PairingApprovedResponse(match.Value.DeviceName);
    }

    private static void Prune()
    {
        foreach (var (id, p) in Pairings)
            if (p.ExpiresAt < DateTime.UtcNow) Pairings.TryRemove(id, out _);
    }
}
