//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Net;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RetroIO.Protocols.Tnfs.Server;

namespace RetroIO.Tests.TNFS
{
    /// <summary>
    /// Regression tests for SessionManager.GetAllSessions() and TnfsSession.CreatedAt — added so
    /// TnfsController.GetSessions could stop returning a hardcoded-empty list (see
    /// Stub-Gap-Audit.md).
    /// </summary>
    [TestFixture]
    [Category("TNFS")]
    public class SessionManagerGetAllSessionsTests
    {
        private SessionManager _sessionManager = null!;

        [SetUp]
        public void SetUp()
        {
            var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
            _sessionManager = new SessionManager(
                loggerFactory.CreateLogger<SessionManager>(), TimeSpan.FromMinutes(10), maxSessionsPerIP: 8);
        }

        [TearDown]
        public void TearDown()
        {
            _sessionManager.Dispose();
        }

        [Test]
        public void GetAllSessions_NoSessions_ReturnsEmpty()
        {
            Assert.That(_sessionManager.GetAllSessions(), Is.Empty);
        }

        [Test]
        public void GetAllSessions_ReturnsAllCreatedSessions()
        {
            _sessionManager.CreateSession(IPAddress.Parse("10.0.0.1"), "/root1");
            _sessionManager.CreateSession(IPAddress.Parse("10.0.0.2"), "/root2");

            var sessions = _sessionManager.GetAllSessions();

            Assert.That(sessions.Count, Is.EqualTo(2));
        }

        [Test]
        public void GetAllSessions_ReflectsRemoval()
        {
            var session = _sessionManager.CreateSession(IPAddress.Parse("10.0.0.1"), "/root1");
            Assert.That(_sessionManager.GetAllSessions().Count, Is.EqualTo(1));

            _sessionManager.RemoveSession(session.SessionId);

            Assert.That(_sessionManager.GetAllSessions(), Is.Empty);
        }

        [Test]
        public void CreateSession_SetsCreatedAt_DistinctFromLastContact()
        {
            DateTime before = DateTime.UtcNow;
            var session = _sessionManager.CreateSession(IPAddress.Parse("10.0.0.1"), "/root1");
            DateTime after = DateTime.UtcNow;

            Assert.That(session.CreatedAt, Is.InRange(before, after));

            // Simulate time passing and a later request touching LastContact — CreatedAt must
            // stay fixed at session creation, distinct from the mutable LastContact field.
            session.LastContact = session.CreatedAt.AddMinutes(5);
            Assert.That(session.CreatedAt, Is.Not.EqualTo(session.LastContact));
        }
    }
}
