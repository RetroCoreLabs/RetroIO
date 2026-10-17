//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RetroIO.Management.Api.Authentication;
using RetroIO.Management.Contracts;
using RetroIO.Identity;

namespace RetroIO.Management.Api.Controllers;

/// <summary>
/// Turns a RetroIDP user name and password into a bearer token.
/// </summary>
/// <remarks>
/// The only anonymous endpoint on the management API. Everything else requires the token this
/// hands out.
/// </remarks>
[ApiController]
[Route("api/v1/auth")]
public class AuthController : ControllerBase
{
    private readonly IRetroIDP _idp;
    private readonly ManagementTokenService _tokens;
    private readonly ILogger<AuthController> _logger;

    /// <summary>Creates the controller.</summary>
    /// <param name="idp">Identity provider that verifies the password.</param>
    /// <param name="tokens">Token issuer.</param>
    /// <param name="logger">Log sink.</param>
    public AuthController(IRetroIDP idp, ManagementTokenService tokens, ILogger<AuthController> logger)
    {
        _idp = idp;
        _tokens = tokens;
        _logger = logger;
    }

    /// <summary>Exchanges credentials for a bearer token.</summary>
    /// <param name="request">User name and password.</param>
    /// <returns>The token, or 401 when the credentials are not accepted.</returns>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<LoginResponse>>> Login([FromBody] LoginRequest request)
    {
        if (request == null ||
            string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(ApiResponse<LoginResponse>.Fail("Username and password are required"));
        }

        RetroUser? user = await _idp.AuthenticateAsync(request.Username, request.Password);

        if (user == null || !user.IsActive)
        {
            // Deliberately the same message and the same status for "no such user", "wrong
            // password" and "disabled account": telling them apart hands an attacker a way to
            // enumerate valid user names.
            _logger.LogWarning("Management login refused for {Username}", request.Username);
            return Unauthorized(ApiResponse<LoginResponse>.Fail("Invalid username or password"));
        }

        (string token, DateTime expiresAt) = _tokens.Issue(user);

        _logger.LogInformation("Management login for {Username} (admin: {IsAdmin})",
            user.Username, user.IsAdmin);

        return Ok(ApiResponse<LoginResponse>.Ok(new LoginResponse
        {
            Token = token,
            ExpiresAtUtc = expiresAt,
            Username = user.Username,
            DisplayName = user.DisplayName,

            // A non-admin gets a valid token that every management endpoint will refuse. Saying so
            // here lets the client show "your account cannot manage this server" instead of
            // letting the user hit a wall of 403s.
            IsAdmin = user.IsAdmin
        }));
    }
}
