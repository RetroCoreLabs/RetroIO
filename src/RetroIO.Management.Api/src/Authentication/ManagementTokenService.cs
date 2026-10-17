//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using RetroIO.Management.Contracts;
using RetroIO.Identity;

namespace RetroIO.Management.Api.Authentication;

/// <summary>
/// Issues the bearer tokens the management API accepts, and owns the key they are signed with.
/// </summary>
/// <remarks>
/// The signing key is generated once, 32 random bytes from a cryptographic RNG, and kept in
/// <c>management-signing-key</c> in the RetroFS data directory. Persisting it matters: a key
/// regenerated on every start would invalidate every outstanding token whenever the service
/// restarted, and a key hardcoded in the source would be the same on every installation on earth.
///
/// Anyone who can read that file can mint tokens, so it sits in the data directory alongside the
/// identity store, which already holds the password hashes and needs the same protection.
/// </remarks>
public sealed class ManagementTokenService
{
    /// <summary>File in the data directory holding the signing key.</summary>
    private const string KeyFileName = "management-signing-key";

    /// <summary>Key length in bytes. 256 bits, matching HMAC-SHA256.</summary>
    private const int KeySizeBytes = 32;

    /// <summary>Issuer and audience. Local to one installation, so one name serves as both.</summary>
    private const string Issuer = "RetroFS.Management";

    private readonly SigningCredentials _credentials;
    private readonly TimeSpan _lifetime;

    /// <summary>Token validation parameters the JWT bearer handler must be configured with.</summary>
    /// <remarks>
    /// Exposed so the server validates with exactly the key and issuer it signs with; deriving
    /// them separately in two places is how a validator ends up quietly accepting the wrong thing.
    /// </remarks>
    public TokenValidationParameters ValidationParameters { get; }

    /// <summary>
    /// Loads the signing key from the data directory, creating it on first run.
    /// </summary>
    /// <param name="dataPath">RetroFS data directory.</param>
    /// <param name="lifetime">How long an issued token stays valid.</param>
    public ManagementTokenService(string dataPath, TimeSpan lifetime)
    {
        if (string.IsNullOrWhiteSpace(dataPath))
            throw new ArgumentException("Data path is required.", nameof(dataPath));

        _lifetime = lifetime;

        byte[] key = LoadOrCreateKey(dataPath);
        SymmetricSecurityKey securityKey = new SymmetricSecurityKey(key);

        _credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        ValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = Issuer,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = securityKey,
            ValidateLifetime = true,

            // The default is five minutes of leeway, which quietly extends every token's life.
            // Server and client are the same machine or the same LAN here, so no slack is needed.
            ClockSkew = TimeSpan.Zero
        };
    }

    /// <summary>Issues a token for a user who has already proved their password.</summary>
    /// <param name="user">Authenticated user.</param>
    /// <returns>The signed token and the moment it expires.</returns>
    public (string Token, DateTime ExpiresAtUtc) Issue(RetroUser user)
    {
        if (user == null) throw new ArgumentNullException(nameof(user));

        DateTime expiresAt = DateTime.UtcNow.Add(_lifetime);

        Claim[] claims =
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.UserId.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ManagementAuth.UserIdClaim, user.UserId.ToString()),
            new Claim(ManagementAuth.AdminClaim, user.IsAdmin ? "true" : "false")
        };

        JwtSecurityToken token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Issuer,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAt,
            signingCredentials: _credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    /// <summary>Reads the signing key, generating and storing one on first run.</summary>
    /// <param name="dataPath">RetroFS data directory.</param>
    /// <returns>The 32-byte key.</returns>
    private static byte[] LoadOrCreateKey(string dataPath)
    {
        Directory.CreateDirectory(dataPath);
        string keyPath = Path.Combine(dataPath, KeyFileName);

        if (File.Exists(keyPath))
        {
            byte[] existing = File.ReadAllBytes(keyPath);

            // A short or empty file means a truncated write or a tampered install. Replacing it
            // is safer than signing with a weak key; the cost is that outstanding tokens stop
            // being accepted, which is the correct outcome for a damaged key anyway.
            if (existing.Length >= KeySizeBytes) return existing;
        }

        byte[] key = RandomNumberGenerator.GetBytes(KeySizeBytes);
        File.WriteAllBytes(keyPath, key);
        return key;
    }
}
