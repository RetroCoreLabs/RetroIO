//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using NUnit.Framework;

namespace RetroIO.Management.Api.Tests;

/// <summary>
/// The Swagger page is served when it is switched on, and not when it is off.
/// </summary>
/// <remarks>
/// Until 2026-11-23 the API description was built but nothing served it, while RetroServer's
/// start-up banner printed the page's address.
/// </remarks>
[TestFixture]
public sealed class SwaggerServingTests
{
    [TestCase(true, HttpStatusCode.OK)]
    [TestCase(false, HttpStatusCode.Unauthorized)]
    public async Task TheApiDescription_IsServed_OnlyWhenSwaggerIsOn(bool enableSwagger, HttpStatusCode expected)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRetroManagementApi(builder.Logging, enableSwagger: enableSwagger);
        await using WebApplication app = builder.Build();
        app.UseRetroManagementApi(enableSwagger);
        app.MapRetroManagementApi();
        await app.StartAsync();

        using HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.That(response.StatusCode, Is.EqualTo(expected), "got " + (int)response.StatusCode);
        if (enableSwagger)
        {
            string text = await response.Content.ReadAsStringAsync();
            Assert.That(text, Does.Contain("RetroFS Management API"));
            Assert.That(text, Does.Contain("/api/v1/telemetry/sources").IgnoreCase, text.Substring(System.Math.Max(0, text.IndexOf("\"paths\"", System.StringComparison.Ordinal)), 400));
        }
    }
}
