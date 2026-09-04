//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Threading;
using System.Threading.Tasks;

namespace RetroIO.Identity
{
    /// <summary>
    /// Authentication provider that wraps IRetroIDP
    /// Provides simple authentication interface for servers
    /// </summary>
    public sealed class RetroIDPAuthenticationProvider
    {
        private readonly IRetroIDP _idp;

        public RetroIDPAuthenticationProvider(IRetroIDP idp)
        {
            _idp = idp ?? throw new ArgumentNullException(nameof(idp));
        }

        /// <summary>
        /// Authenticate user with username and password
        /// Returns authenticated user or null if authentication failed
        /// </summary>
        public async ValueTask<AuthenticationResult> AuthenticateAsync(
            string username, string password, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                return AuthenticationResult.Failed("Username and password are required");
            }

            ProtocolAuthResult result = await RetroUserProtocolAuth.AuthenticateAsync(
                _idp, username, password, RetroFsProtocol.Tnfs, ct);

            return result switch
            {
                ProtocolAuthResult.Success => AuthenticationResult.Success(
                    (await _idp.GetUserByUsernameAsync(username, ct))!),
                ProtocolAuthResult.Inactive => AuthenticationResult.Failed("Account is disabled"),
                ProtocolAuthResult.ProtocolNotAllowed => AuthenticationResult.Failed("TNFS access is not enabled for this account"),
                _ => AuthenticationResult.Failed("Invalid username or password"),
            };
        }

        /// <summary>
        /// Authenticate via external identity provider (OAuth, LDAP, etc.)
        /// Auto-provisions new user if not found
        /// </summary>
        public async ValueTask<AuthenticationResult> AuthenticateViaExternalIdentityAsync(
            string provider, string externalUserId, string externalUsername,
            bool autoProvision = true, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(externalUserId))
            {
                return AuthenticationResult.Failed("Provider and external user ID are required");
            }

            RetroUser? user = await _idp.AuthenticateViaExternalIdentityAsync(
                provider, externalUserId, externalUsername, autoProvision, ct);

            if (user == null)
            {
                return AuthenticationResult.Failed($"Authentication via {provider} failed");
            }

            if (!user.IsActive)
            {
                return AuthenticationResult.Failed("Account is disabled");
            }

            return AuthenticationResult.Success(user);
        }

        /// <summary>
        /// Get user by GUID (for token validation)
        /// </summary>
        public async ValueTask<RetroUser?> GetUserAsync(Guid userId, CancellationToken ct = default)
        {
            return await _idp.GetUserAsync(userId, ct);
        }

        /// <summary>
        /// Check if user is admin
        /// </summary>
        public async ValueTask<bool> IsAdminAsync(Guid userId, CancellationToken ct = default)
        {
            RetroUser? user = await _idp.GetUserAsync(userId, ct);
            return user?.IsAdmin ?? false;
        }
    }

    /// <summary>
    /// Authentication result
    /// </summary>
    public sealed class AuthenticationResult
    {
        public bool IsSuccess { get; set; }
        public RetroUser? User { get; set; }
        public string? ErrorMessage { get; set; }

        public static AuthenticationResult Success(RetroUser user)
        {
            return new AuthenticationResult
            {
                IsSuccess = true,
                User = user
            };
        }

        public static AuthenticationResult Failed(string errorMessage)
        {
            return new AuthenticationResult
            {
                IsSuccess = false,
                ErrorMessage = errorMessage
            };
        }
    }
}
