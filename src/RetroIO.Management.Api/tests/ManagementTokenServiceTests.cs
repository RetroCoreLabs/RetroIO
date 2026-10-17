//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using RetroIO.Identity;
using RetroIO.Management.Api.Authentication;
using RetroIO.Management.Contracts;

namespace RetroIO.Management.Api.Tests;

/// <summary>
/// Tests the bearer tokens the management API issues and the key they are signed with.
/// </summary>
/// <remarks>
/// These matter more than most: until this token service existed the management API had no
/// authentication at all, and its 38 endpoints create users, change passwords and edit firewall
/// rules. A token that validates when it should not is the whole security model failing open.
/// </remarks>
[TestFixture]
public class ManagementTokenServiceTests
{
    private string _dataPath = null!;

    [SetUp]
    public void SetUp()
    {
        // A directory per test, never the real one: the service writes a signing key, and reusing
        // the developer's data directory would both leak the real key into tests and let one test
        // see another's.
        _dataPath = Path.Combine(Path.GetTempPath(), "retrofs-token-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataPath);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dataPath)) Directory.Delete(_dataPath, true);
    }

    /// <summary>
    /// Builds a user to issue tokens for.
    /// </summary>
    /// <param name="isAdmin">
    /// Whether the user is an administrator.
    /// </param>
    /// <returns>
    /// The user.
    /// </returns>
    private static RetroUser MakeUser(bool isAdmin) => new RetroUser
    {
        UserId = Guid.NewGuid(),
        Username = isAdmin ? "admin" : "plainuser",
        DisplayName = "Test User",
        IsAdmin = isAdmin,
        IsActive = true
    };

    /// <summary>
    /// Reads a token back with the service's own validation parameters.
    /// </summary>
    /// <param name="service">
    /// Service that issued it.
    /// </param>
    /// <param name="token">
    /// The token.
    /// </param>
    /// <returns>
    /// The validated principal.
    /// </returns>
    private static ClaimsPrincipal Validate(ManagementTokenService service, string token) =>
        new JwtSecurityTokenHandler().ValidateToken(token, service.ValidationParameters, out _);

    [Test]
    public void IssuedToken_ValidatesAndCarriesTheUsersClaims()
    {
        ManagementTokenService service = new ManagementTokenService(_dataPath, TimeSpan.FromHours(1));
        RetroUser user = MakeUser(isAdmin: true);

        (string token, DateTime expiresAt) = service.Issue(user);

        ClaimsPrincipal principal = Validate(service, token);

        Assert.Multiple(() =>
        {
            Assert.That(principal.FindFirst(ManagementAuth.UserIdClaim)?.Value,
                Is.EqualTo(user.UserId.ToString()));
            Assert.That(principal.FindFirst(ClaimTypes.Name)?.Value, Is.EqualTo("admin"));
            Assert.That(principal.FindFirst(ManagementAuth.AdminClaim)?.Value, Is.EqualTo("true"));
            Assert.That(expiresAt, Is.GreaterThan(DateTime.UtcNow));
        });
    }

    [Test]
    public void NonAdminToken_CarriesAdminClaimFalse()
    {
        // The authorization policy requires this claim to be exactly "true", so a non-admin must
        // not simply be missing the claim -- it must be present and false, which is what the
        // policy's RequireClaim check refuses.
        ManagementTokenService service = new ManagementTokenService(_dataPath, TimeSpan.FromHours(1));

        (string token, _) = service.Issue(MakeUser(isAdmin: false));

        ClaimsPrincipal principal = Validate(service, token);

        Assert.That(principal.FindFirst(ManagementAuth.AdminClaim)?.Value, Is.EqualTo("false"));
    }

    [Test]
    public void TamperedToken_IsRejected()
    {
        ManagementTokenService service = new ManagementTokenService(_dataPath, TimeSpan.FromHours(1));
        (string token, _) = service.Issue(MakeUser(isAdmin: true));

        // Flip the last character of the signature.
        string tampered = token.Substring(0, token.Length - 1) +
                          (token[^1] == 'A' ? 'B' : 'A');

        // Both SecurityTokenInvalidSignatureException and SecurityTokenSignatureKeyNotFoundException
        // derive from this. Which one the handler picks is its own business; what this test is
        // about is that a tampered token is refused.
        Assert.Throws(Is.InstanceOf<SecurityTokenException>(), () => Validate(service, tampered));
    }

    [Test]
    public void TokenFromADifferentInstallation_IsRejected()
    {
        // Two data directories mean two independently generated keys. A token minted by one
        // server must not open another -- if it did, every RetroFS installation would share one
        // trust boundary.
        string otherPath = Path.Combine(Path.GetTempPath(), "retrofs-token-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(otherPath);

        try
        {
            ManagementTokenService ours = new ManagementTokenService(_dataPath, TimeSpan.FromHours(1));
            ManagementTokenService theirs = new ManagementTokenService(otherPath, TimeSpan.FromHours(1));

            (string foreignToken, _) = theirs.Issue(MakeUser(isAdmin: true));

            Assert.Throws(Is.InstanceOf<SecurityTokenException>(), () => Validate(ours, foreignToken));
        }
        finally
        {
            if (Directory.Exists(otherPath)) Directory.Delete(otherPath, true);
        }
    }

    [Test]
    public void ExpiredToken_IsRejected()
    {
        // Signed by hand with the service's own key so the token is genuinely expired the moment
        // it exists. Asking the service for a negative lifetime does not work -- JwtSecurityToken
        // refuses to build a token whose expiry precedes its notBefore -- and a one-second
        // lifetime plus a sleep would be both slower and racy. ClockSkew is zero on the
        // validation parameters, so there is no grace period to absorb the difference.
        ManagementTokenService service = new ManagementTokenService(_dataPath, TimeSpan.FromHours(1));

        byte[] key = File.ReadAllBytes(Path.Combine(_dataPath, "management-signing-key"));
        SigningCredentials credentials = new SigningCredentials(
            new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256);

        JwtSecurityToken expired = new JwtSecurityToken(
            issuer: "RetroFS.Management",
            audience: "RetroFS.Management",
            claims: new[] { new Claim(ManagementAuth.AdminClaim, "true") },
            notBefore: DateTime.UtcNow.AddHours(-2),
            expires: DateTime.UtcNow.AddHours(-1),
            signingCredentials: credentials);

        string token = new JwtSecurityTokenHandler().WriteToken(expired);

        Assert.Throws<SecurityTokenExpiredException>(() => Validate(service, token));
    }

    [Test]
    public void SigningKey_IsPersistedAndReusedAcrossRestarts()
    {
        // A key regenerated on every start would invalidate every outstanding token whenever the
        // service restarted. Constructing a second service over the same directory stands in for
        // that restart: a token from the first must still validate under the second.
        ManagementTokenService first = new ManagementTokenService(_dataPath, TimeSpan.FromHours(1));
        (string token, _) = first.Issue(MakeUser(isAdmin: true));

        ManagementTokenService afterRestart = new ManagementTokenService(_dataPath, TimeSpan.FromHours(1));

        Assert.DoesNotThrow(() => Validate(afterRestart, token));
    }

    [Test]
    public void SigningKey_IsRandomPerInstallation()
    {
        string otherPath = Path.Combine(Path.GetTempPath(), "retrofs-token-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(otherPath);

        try
        {
            _ = new ManagementTokenService(_dataPath, TimeSpan.FromHours(1));
            _ = new ManagementTokenService(otherPath, TimeSpan.FromHours(1));

            byte[] a = File.ReadAllBytes(Path.Combine(_dataPath, "management-signing-key"));
            byte[] b = File.ReadAllBytes(Path.Combine(otherPath, "management-signing-key"));

            Assert.Multiple(() =>
            {
                Assert.That(a, Has.Length.EqualTo(32), "key must be 256 bits for HMAC-SHA256");
                Assert.That(b, Is.Not.EqualTo(a), "two installations must not share a signing key");
            });
        }
        finally
        {
            if (Directory.Exists(otherPath)) Directory.Delete(otherPath, true);
        }
    }

    [Test]
    public void TruncatedKeyFile_IsReplacedRatherThanUsed()
    {
        // A short key file means a truncated write or tampering. Signing with a weak key would be
        // worse than invalidating outstanding tokens, so the service must replace it.
        string keyPath = Path.Combine(_dataPath, "management-signing-key");
        File.WriteAllBytes(keyPath, new byte[] { 1, 2, 3 });

        ManagementTokenService service = new ManagementTokenService(_dataPath, TimeSpan.FromHours(1));

        byte[] replaced = File.ReadAllBytes(keyPath);

        Assert.That(replaced, Has.Length.EqualTo(32));
        Assert.DoesNotThrow(() =>
        {
            (string token, _) = service.Issue(MakeUser(isAdmin: true));
            Validate(service, token);
        });
    }
}
