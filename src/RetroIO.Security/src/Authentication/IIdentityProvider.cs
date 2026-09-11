//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RetroIO.Security.Authentication;

/// <summary>
/// Identity Provider (IDP) type enumeration
/// </summary>
public enum IdentityProviderType : byte
{
    /// <summary>Internal user registry (GUID-based)</summary>
    Internal = 0,

    /// <summary>OAuth 2.0 provider</summary>
    OAuth2 = 1,

    /// <summary>OpenID Connect provider</summary>
    OpenIdConnect = 2,

    /// <summary>SAML 2.0 provider</summary>
    SAML2 = 3,

    /// <summary>LDAP/Active Directory</summary>
    LDAP = 4,

    /// <summary>Custom external provider</summary>
    Custom = 5
}

/// <summary>
/// External identity mapping for OAuth/IDP integration
/// Maps external IDP identity to internal GUID-based user
/// </summary>
public sealed class ExternalIdentityMapping
{
    /// <summary>Internal user GUID (primary identity)</summary>
    public Guid InternalUserId { get; set; }

    /// <summary>Identity provider type</summary>
    public IdentityProviderType ProviderType { get; set; }

    /// <summary>Identity provider name/ID</summary>
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>External user ID from IDP (e.g., OAuth sub claim)</summary>
    public string ExternalUserId { get; set; } = string.Empty;

    /// <summary>External username from IDP</summary>
    public string? ExternalUsername { get; set; }

    /// <summary>External email from IDP</summary>
    public string? ExternalEmail { get; set; }

    /// <summary>IDP-specific claims</summary>
    public Dictionary<string, string> ExternalClaims { get; set; } = new();

    /// <summary>Mapping creation timestamp</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Last time external identity was verified</summary>
    public DateTime LastVerifiedAt { get; set; }

    /// <summary>Whether mapping is active</summary>
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Internal user registration (GUID-based primary identity)
/// </summary>
public sealed class InternalUser
{
    /// <summary>Primary user GUID (internal identity)</summary>
    public Guid UserId { get; set; }

    /// <summary>Username (may be from primary IDP or internal)</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Display name</summary>
    public string? DisplayName { get; set; }

    /// <summary>Email address</summary>
    public string? Email { get; set; }

    /// <summary>User roles</summary>
    public HashSet<string> Roles { get; set; } = new();

    /// <summary>User claims</summary>
    public Dictionary<string, string> Claims { get; set; } = new();

    /// <summary>External identity mappings (OAuth, SAML, LDAP, etc.)</summary>
    public List<ExternalIdentityMapping> ExternalIdentities { get; set; } = new();

    /// <summary>User creation timestamp</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Last login timestamp</summary>
    public DateTime? LastLoginAt { get; set; }

    /// <summary>Whether user account is active</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Whether user account is locked</summary>
    public bool IsLocked { get; set; } = false;
}

/// <summary>
/// OAuth token information for IDP integration
/// </summary>
public sealed class OAuthTokenInfo
{
    /// <summary>Access token from IDP</summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>Refresh token from IDP</summary>
    public string? RefreshToken { get; set; }

    /// <summary>ID token from IDP (OpenID Connect)</summary>
    public string? IdToken { get; set; }

    /// <summary>Token type (usually "Bearer")</summary>
    public string TokenType { get; set; } = "Bearer";

    /// <summary>Access token expiration</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>Token scope</summary>
    public string? Scope { get; set; }

    /// <summary>IDP that issued the token</summary>
    public string ProviderName { get; set; } = string.Empty;
}

/// <summary>
/// Identity provider interface for OAuth/IDP integration
/// Maps external identities to internal GUID-based users
/// </summary>
public interface IIdentityProvider
{
    /// <summary>
    /// Authenticate user via external IDP (OAuth, SAML, etc.)
    /// Maps external identity to internal user GUID
    /// </summary>
    /// <param name="providerName">IDP provider name</param>
    /// <param name="oauthToken">OAuth token information</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Authentication result with mapped internal principal</returns>
    ValueTask<AuthenticationResult> AuthenticateViaIdpAsync(
        string providerName,
        OAuthTokenInfo oauthToken,
        CancellationToken ct = default);

    /// <summary>
    /// Get or create internal user from external identity
    /// Auto-provisions internal user if mapping doesn't exist
    /// </summary>
    /// <param name="providerName">IDP provider name</param>
    /// <param name="externalUserId">External user ID from IDP</param>
    /// <param name="externalClaims">Claims from IDP</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Internal user with mapped identity</returns>
    ValueTask<InternalUser> GetOrCreateInternalUserAsync(
        string providerName,
        string externalUserId,
        Dictionary<string, string> externalClaims,
        CancellationToken ct = default);

    /// <summary>
    /// Get internal user by GUID
    /// </summary>
    /// <param name="userId">Internal user GUID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Internal user or null if not found</returns>
    ValueTask<InternalUser?> GetInternalUserAsync(
        Guid userId,
        CancellationToken ct = default);

    /// <summary>
    /// Get internal user by username
    /// </summary>
    /// <param name="username">Username</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Internal user or null if not found</returns>
    ValueTask<InternalUser?> GetInternalUserByUsernameAsync(
        string username,
        CancellationToken ct = default);

    /// <summary>
    /// Map external identity to existing internal user
    /// </summary>
    /// <param name="internalUserId">Internal user GUID</param>
    /// <param name="mapping">External identity mapping</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if mapping was added</returns>
    ValueTask<bool> AddExternalIdentityMappingAsync(
        Guid internalUserId,
        ExternalIdentityMapping mapping,
        CancellationToken ct = default);

    /// <summary>
    /// Remove external identity mapping
    /// </summary>
    /// <param name="internalUserId">Internal user GUID</param>
    /// <param name="providerName">IDP provider name</param>
    /// <param name="externalUserId">External user ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if mapping was removed</returns>
    ValueTask<bool> RemoveExternalIdentityMappingAsync(
        Guid internalUserId,
        string providerName,
        string externalUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Create SecurityPrincipal from internal user
    /// </summary>
    /// <param name="user">Internal user</param>
    /// <param name="sessionToken">Session token (if any)</param>
    /// <param name="expiresAt">Token expiration (if any)</param>
    /// <returns>SecurityPrincipal with user's roles and claims</returns>
    SecurityPrincipal CreatePrincipalFromUser(
        InternalUser user,
        string? sessionToken = null,
        DateTime? expiresAt = null);

    /// <summary>
    /// Refresh OAuth token from IDP
    /// </summary>
    /// <param name="providerName">IDP provider name</param>
    /// <param name="refreshToken">Refresh token</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>New OAuth token information</returns>
    ValueTask<OAuthTokenInfo?> RefreshOAuthTokenAsync(
        string providerName,
        string refreshToken,
        CancellationToken ct = default);
}
