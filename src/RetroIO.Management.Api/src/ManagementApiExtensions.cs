//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards
//

using System;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroIO.Management.Api.Authentication;
using RetroIO.Management.Api.Hubs;
using RetroIO.Management.Api.Services;
using RetroIO.Management.Contracts;
using RetroIO.Server.Core.FileSystem;
using RetroIO.Server.Hosting;
using Microsoft.OpenApi.Models;

namespace RetroIO.Management.Api;

/// <summary>
/// Adds the management API to a host.
/// </summary>
/// <remarks>
/// These two calls replace what used to be this project's own <c>Program.cs</c>. Putting them in
/// the library is what lets the API be served from the SAME process that runs the protocol
/// servers, which is the only way the TNFS, Econet and Server endpoints can report real numbers:
/// they need the live <c>SessionManager</c> and statistics objects, and those cannot cross a
/// process boundary.
///
/// Deliberately NOT registered here: identity, security and the protocol servers. Those belong to
/// the host's own composition (see <c>RetroIO.Server.Hosting</c>), so that a host which already
/// registered them does not get a second, conflicting set.
/// </remarks>
public static class ManagementApiExtensions
{
    /// <summary>
    /// Registers the management API's controllers, SignalR hub, live push service and the
    /// in-memory log buffer the Logs endpoints read from.
    /// </summary>
    /// <param name="services">Service collection to add to.</param>
    /// <param name="loggingBuilder">
    /// The host's logging builder. The in-memory log provider is attached to it so the Logs
    /// endpoints return the log of the process that is actually serving.
    /// </param>
    /// <param name="maxLogEntries">How many log entries the in-memory buffer keeps.</param>
    /// <param name="enableSwagger">Whether to register the Swagger/OpenAPI generator.</param>
    /// <param name="tokenLifetime">
    /// How long an issued bearer token stays valid. Null means eight hours, long enough for a
    /// working day at the console without leaving a stolen token useful indefinitely.
    /// </param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddRetroManagementApi(
        this IServiceCollection services,
        ILoggingBuilder loggingBuilder,
        int maxLogEntries = 1000,
        bool enableSwagger = true,
        TimeSpan? tokenLifetime = null)
    {
        // One buffer shared by the logging pipeline and the Logs controller. Registered as a
        // concrete singleton because LogsController takes the concrete type.
        InMemoryLogProvider logProvider = new InMemoryLogProvider(maxEntries: maxLogEntries);
        services.AddSingleton(logProvider);
        loggingBuilder.AddProvider(logProvider);

        // AddApplicationPart is the mechanism that matters here: controllers live in THIS
        // assembly, not in the host's, so without it the host discovers none of them.
        services.AddControllers()
            .AddApplicationPart(typeof(ManagementApiExtensions).Assembly);

        services.AddSignalR();

        // ---------------- authentication ----------------
        // Every endpoint below is refused without a bearer token from /api/v1/auth/login. Until
        // this existed the API had no authentication of any kind -- no [Authorize], no
        // AddAuthentication -- and all 38 endpoints (create users, change passwords, edit
        // firewall rules, start and stop the server) answered anyone who could reach the port.

        ManagementTokenService tokenService = new ManagementTokenService(
            RetroServerHostingExtensions.EnsureRetroFsDataPath(),
            tokenLifetime ?? TimeSpan.FromHours(8));

        services.AddSingleton(tokenService);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Validated with the very parameters the issuer signs with, rather than a second
                // set assembled here that could drift out of agreement with them.
                options.TokenValidationParameters = tokenService.ValidationParameters;

                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        // A WebSocket handshake cannot carry an Authorization header, so SignalR
                        // passes the token in the query string. Accept it there for the hub path
                        // ONLY -- honouring it everywhere would put tokens into request logs,
                        // browser history and Referer headers.
                        string? token = context.Request.Query[ManagementAuth.AccessTokenQueryParameter];

                        if (!string.IsNullOrEmpty(token) &&
                            context.HttpContext.Request.Path.StartsWithSegments(ServerHubMethods.HubPath))
                        {
                            context.Token = token;
                        }

                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorization(options =>
        {
            // Management is an administrator function. A valid token for a non-admin account
            // authenticates the caller but does not authorise them here.
            options.AddPolicy(ManagementAuth.PolicyName, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireClaim(ManagementAuth.AdminClaim, "true");
            });

            // Anything that forgets an explicit attribute still lands on the strict policy, so a
            // new controller cannot be accidentally anonymous.
            options.FallbackPolicy = options.GetPolicy(ManagementAuth.PolicyName);
        });

        if (enableSwagger)
        {
            services.AddEndpointsApiExplorer();
            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new()
                {
                    Title = "RetroFS Management API",
                    Version = "v1",
                    Description = "REST API for managing RetroFS servers"
                });

                // Without this the Swagger UI cannot send a token, so every "Try it out" on a
                // real endpoint would come back 401 and look like a broken API.
                c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
                {
                    Name = "Authorization",
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    In = ParameterLocation.Header,
                    Description = "Token from POST /api/v1/auth/login"
                });

                c.AddSecurityRequirement(new OpenApiSecurityRequirement
                {
                    {
                        new OpenApiSecurityScheme
                        {
                            Reference = new OpenApiReference
                            {
                                Type = ReferenceType.SecurityScheme,
                                Id = "Bearer"
                            }
                        },
                        Array.Empty<string>()
                    }
                });
            });
        }

        services.AddSingleton<IServerManager, ServerManager>();

        // Only added if the host has not already supplied one. A host that runs the protocol
        // servers registers its own backend, and that one must win.
        services.TryAddFileSystemBackend();

        services.AddHostedService<LiveDataService>();

        return services;
    }

    /// <summary>
    /// Adds the authentication and authorization middleware the management API needs.
    /// </summary>
    /// <param name="app">The host's application builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    /// A host that registers the API but forgets this call would authenticate nobody and let
    /// every request through, so it lives here beside the registration rather than being left as
    /// two lines each host has to remember. Order matters: authentication must run before
    /// authorization, and both before the endpoints are dispatched.
    /// </remarks>
    public static IApplicationBuilder UseRetroManagementApi(this IApplicationBuilder app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }

    /// <summary>
    /// Maps the management API's controller routes and its SignalR hub.
    /// </summary>
    /// <param name="endpoints">Endpoint route builder from the host.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapRetroManagementApi(this IEndpointRouteBuilder endpoints)
    {
        // The fallback policy already covers the controllers; RequireAuthorization on the hub is
        // explicit because a hub is mapped, not attributed, and is easy to overlook.
        endpoints.MapControllers();
        endpoints.MapHub<ServerHub>(ServerHubMethods.HubPath)
                 .RequireAuthorization(ManagementAuth.PolicyName);
        return endpoints;
    }

    /// <summary>
    /// Registers <see cref="LocalFileSystemBackend"/> unless the host already registered an
    /// <see cref="IFileSystemBackend"/>.
    /// </summary>
    /// <param name="services">Service collection to add to.</param>
    private static void TryAddFileSystemBackend(this IServiceCollection services)
    {
        for (int i = 0; i < services.Count; i++)
        {
            if (services[i].ServiceType == typeof(IFileSystemBackend))
            {
                return;
            }
        }

        services.AddSingleton<IFileSystemBackend, LocalFileSystemBackend>();
    }
}
