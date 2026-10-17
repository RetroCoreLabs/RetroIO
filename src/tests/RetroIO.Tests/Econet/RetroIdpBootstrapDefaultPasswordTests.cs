//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RetroIO.Identity;
using RetroIO.Server.Hosting;

namespace RetroIO.Tests.Econet
{
    /// <summary>
    /// Tests that <see cref="RetroIdpBootstrap"/> warns, at every start, while the admin or SYST
    /// account still accepts the public default password.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public class RetroIdpBootstrapDefaultPasswordTests
    {
        private const string DefaultPassword = "retroadmin";
        private string _dir = null!;

        // Collects every warning the bootstrap writes.
        private sealed class WarningCollector : ILoggerProvider
        {
            public List<string> Warnings { get; } = new List<string>();

            public ILogger CreateLogger(string categoryName) => new Collector(Warnings);

            public void Dispose() { }

            private sealed class Collector : ILogger
            {
                private readonly List<string> _sink;

                public Collector(List<string> sink) { _sink = sink; }

                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                {
                    if (logLevel >= LogLevel.Warning)
                        _sink.Add(formatter(state, exception));
                }
            }
        }

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "retroio-bootstrap-pw-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private JsonRetroIDP NewIdp() => new JsonRetroIDP(
            Path.Combine(_dir, "users.json"),
            Path.Combine(_dir, "groups.json"));

        // Runs the bootstrap once, as a server start would, and returns the warnings it logged.
        private static async Task<List<string>> StartAsync(IRetroIDP idp)
        {
            WarningCollector collector = new WarningCollector();
            ServiceCollection services = new ServiceCollection();
            services.AddSingleton<IRetroIDP>(idp);
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(collector));
            await using ServiceProvider sp = services.BuildServiceProvider();
            await RetroIdpBootstrap.InitializeAsync(sp);
            return collector.Warnings;
        }

        private static int Count(List<string> warnings, string username)
        {
            int n = 0;
            for (int i = 0; i < warnings.Count; i++)
            {
                if (warnings[i].Contains("still uses the default password") && warnings[i].Contains("'" + username + "'"))
                    n++;
            }
            return n;
        }

        [Test]
        public async Task EveryStartWhilePasswordsAreDefault_WarnsForBothAccounts()
        {
            JsonRetroIDP idp = NewIdp();

            List<string> first = await StartAsync(idp);
            List<string> second = await StartAsync(idp);

            Assert.That(Count(first, WellKnownPrincipals.AdminUsername), Is.EqualTo(1), "first start, admin");
            Assert.That(Count(first, WellKnownPrincipals.SystUsername), Is.EqualTo(1), "first start, SYST");
            Assert.That(Count(second, WellKnownPrincipals.AdminUsername), Is.EqualTo(1), "second start, admin");
            Assert.That(Count(second, WellKnownPrincipals.SystUsername), Is.EqualTo(1), "second start, SYST");
        }

        [Test]
        public async Task AfterBothPasswordsAreChanged_NoDefaultPasswordWarning()
        {
            JsonRetroIDP idp = NewIdp();
            await StartAsync(idp);
            await idp.SetPasswordAsync(WellKnownPrincipals.AdminUserId, "a-new-admin-password");
            await idp.SetPasswordAsync(WellKnownPrincipals.SystUserId, "a-new-syst-password");
            await idp.SaveAsync();

            List<string> warnings = await StartAsync(idp);

            Assert.That(Count(warnings, WellKnownPrincipals.AdminUsername), Is.EqualTo(0));
            Assert.That(Count(warnings, WellKnownPrincipals.SystUsername), Is.EqualTo(0));
        }

        [Test]
        public async Task WhenOnlyAdminIsChanged_OnlySystIsNamed()
        {
            JsonRetroIDP idp = NewIdp();
            await StartAsync(idp);
            await idp.SetPasswordAsync(WellKnownPrincipals.AdminUserId, "a-new-admin-password");
            await idp.SaveAsync();

            List<string> warnings = await StartAsync(idp);

            Assert.That(Count(warnings, WellKnownPrincipals.AdminUsername), Is.EqualTo(0));
            Assert.That(Count(warnings, WellKnownPrincipals.SystUsername), Is.EqualTo(1));
            Assert.That(await idp.VerifyPasswordAsync(WellKnownPrincipals.SystUserId, DefaultPassword), Is.True);
        }
    }
}
