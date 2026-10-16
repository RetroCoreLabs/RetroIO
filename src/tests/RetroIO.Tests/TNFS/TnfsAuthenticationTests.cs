//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RetroIO.Protocols.Tnfs.Protocol;
using RetroIO.Protocols.Tnfs.Server;
using RetroIO.Protocols.Tnfs.Server.Handlers;
using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Tests.TNFS;

/// <summary>
/// Unit tests for TNFS authentication event-based integration
/// Tests various authentication scenarios without requiring RetroIDP
/// </summary>
[TestFixture]
[Category("TNFS")]
[Category("Authentication")]
public class TnfsAuthenticationTests
{
    private SessionManager? _sessionManager;
    private SessionCommandHandler? _handler;
    private IFileSystemBackend? _fileSystem;
    private string _testRoot = string.Empty;

    [SetUp]
    public void Setup()
    {
        // Create test directory
        _testRoot = Path.Combine(Path.GetTempPath(), $"tnfs-auth-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testRoot);

        // Create session manager
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        var sessionLogger = loggerFactory.CreateLogger<SessionManager>();
        _sessionManager = new SessionManager(sessionLogger, TimeSpan.FromMinutes(10), maxSessionsPerIP: 8);

        // Create filesystem backend
        _fileSystem = new LocalFileSystemBackend();

        // Create handler
        var handlerLogger = loggerFactory.CreateLogger<SessionCommandHandler>();
        _handler = new SessionCommandHandler(_sessionManager, handlerLogger, _testRoot, _fileSystem);
    }

    [TearDown]
    public void TearDown()
    {
        // Dispose session manager
        _sessionManager?.Dispose();

        // Cleanup test directory
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, true);
        }
    }

    [Test]
    public void Test_Mount_SuccessfulAuthentication_ReturnsSuccess()
    {
        // Arrange
        var testUserId = Guid.NewGuid();
        var testUsername = "testuser";

        _handler!.OnAuthenticationRequest += (request) =>
        {
            Assert.That(request.Username, Is.EqualTo(testUsername));
            Assert.That(request.Password, Is.EqualTo("correctpassword"));
            Assert.That(request.ClientIP, Is.Not.Null);
            Assert.That(request.MountPoint, Is.EqualTo("/"));

            return TnfsAuthenticationResponse.Success(testUserId, testUsername);
        };

        var mountRequest = CreateMountRequest(testUsername, "correctpassword");

        // Act
        var response = _handler.HandleMount(mountRequest);

        // Assert
        Assert.That(response.Header.Status, Is.EqualTo((byte)TnfsReturnCode.SUCCESS));
        Assert.That(response.Header.SessionId, Is.Not.EqualTo(0));

        // Verify session was created with correct user
        var session = _sessionManager!.FindSession(response.Header.SessionId);
        Assert.That(session, Is.Not.Null);
        Assert.That(session!.UserId, Is.EqualTo(testUserId));
        Assert.That(session.Username, Is.EqualTo(testUsername));
        Assert.That(session.IsAuthenticated, Is.True);
    }

    [Test]
    public void Test_Mount_UnknownUser_ReturnsAccessDenied()
    {
        // Arrange
        _handler!.OnAuthenticationRequest += (request) =>
        {
            Assert.That(request.Username, Is.EqualTo("unknownuser"));
            return TnfsAuthenticationResponse.Failed("User not found");
        };

        var mountRequest = CreateMountRequest("unknownuser", "anypassword");

        // Act
        var response = _handler.HandleMount(mountRequest);

        // Assert
        Assert.That(response.Header.Status, Is.EqualTo((byte)TnfsReturnCode.EACCES));
        Assert.That(response.Header.SessionId, Is.EqualTo(0));

        // Verify no session was created
        var session = _sessionManager!.FindSession(response.Header.SessionId);
        Assert.That(session, Is.Null);
    }

    [Test]
    public void Test_Mount_WrongPassword_ReturnsAccessDenied()
    {
        // Arrange
        _handler!.OnAuthenticationRequest += (request) =>
        {
            Assert.That(request.Username, Is.EqualTo("validuser"));
            Assert.That(request.Password, Is.EqualTo("wrongpassword"));
            return TnfsAuthenticationResponse.Failed("Invalid username or password");
        };

        var mountRequest = CreateMountRequest("validuser", "wrongpassword");

        // Act
        var response = _handler.HandleMount(mountRequest);

        // Assert
        Assert.That(response.Header.Status, Is.EqualTo((byte)TnfsReturnCode.EACCES));
        Assert.That(response.Header.SessionId, Is.EqualTo(0));
    }

    [Test]
    public void Test_Mount_DisabledAccount_ReturnsAccessDenied()
    {
        // Arrange
        _handler!.OnAuthenticationRequest += (request) =>
        {
            return TnfsAuthenticationResponse.Failed("Account is disabled");
        };

        var mountRequest = CreateMountRequest("disableduser", "password");

        // Act
        var response = _handler.HandleMount(mountRequest);

        // Assert
        Assert.That(response.Header.Status, Is.EqualTo((byte)TnfsReturnCode.EACCES));
    }

    [Test]
    public void Test_Mount_AnonymousWithoutCredentials_ReturnsSuccess()
    {
        // Arrange
        // Authentication handler registered but no credentials provided
        _handler!.OnAuthenticationRequest += (request) =>
        {
            Assert.Fail("Authentication handler should not be called for anonymous mount");
            return TnfsAuthenticationResponse.Failed("Should not reach here");
        };

        var mountRequest = CreateMountRequest("", "");  // Empty credentials

        // Act
        var response = _handler.HandleMount(mountRequest);

        // Assert
        Assert.That(response.Header.Status, Is.EqualTo((byte)TnfsReturnCode.SUCCESS));
        Assert.That(response.Header.SessionId, Is.Not.EqualTo(0));

        // Verify session was created as anonymous
        var session = _sessionManager!.FindSession(response.Header.SessionId);
        Assert.That(session, Is.Not.Null);
        Assert.That(session!.UserId, Is.EqualTo(Guid.Empty));
        Assert.That(session.Username, Is.EqualTo("UNKNOWN"));
        Assert.That(session.IsAuthenticated, Is.False);
    }

    [Test]
    public void Test_Mount_NoAuthenticationHandler_AllowsAnonymous()
    {
        // Arrange
        // No authentication handler registered
        var mountRequest = CreateMountRequest("someuser", "somepassword");

        // Act
        var response = _handler!.HandleMount(mountRequest);

        // Assert
        Assert.That(response.Header.Status, Is.EqualTo((byte)TnfsReturnCode.SUCCESS));
        Assert.That(response.Header.SessionId, Is.Not.EqualTo(0));

        // Verify session was created as anonymous (no handler means anonymous allowed)
        var session = _sessionManager!.FindSession(response.Header.SessionId);
        Assert.That(session, Is.Not.Null);
        Assert.That(session!.UserId, Is.EqualTo(Guid.Empty));
        Assert.That(session.IsAuthenticated, Is.False);
    }

    [Test]
    public void Test_Mount_UsernameWithoutPassword_AllowsAnonymous()
    {
        // Arrange
        _handler!.OnAuthenticationRequest += (request) =>
        {
            Assert.Fail("Authentication handler should not be called when password is missing");
            return TnfsAuthenticationResponse.Failed("Should not reach here");
        };

        var mountRequest = CreateMountRequest("username", "");  // Username but no password

        // Act
        var response = _handler.HandleMount(mountRequest);

        // Assert
        Assert.That(response.Header.Status, Is.EqualTo((byte)TnfsReturnCode.SUCCESS));

        // Verify anonymous session
        var session = _sessionManager!.FindSession(response.Header.SessionId);
        Assert.That(session, Is.Not.Null);
        Assert.That(session!.IsAuthenticated, Is.False);
    }

    [Test]
    public void Test_Mount_InvalidDirectory_ReturnsNoSuchFileOrDirectory()
    {
        // Arrange
        var testUserId = Guid.NewGuid();

        _handler!.OnAuthenticationRequest += (request) =>
        {
            return TnfsAuthenticationResponse.Success(testUserId, "testuser");
        };

        var mountRequest = CreateMountRequest("testuser", "password", "/nonexistent");

        // Act
        var response = _handler.HandleMount(mountRequest);

        // Assert
        Assert.That(response.Header.Status, Is.EqualTo((byte)TnfsReturnCode.ENOENT));
    }

    [Test]
    public void Test_Mount_MultipleUsers_IndependentSessions()
    {
        // Arrange
        var user1Id = Guid.NewGuid();
        var user2Id = Guid.NewGuid();

        _handler!.OnAuthenticationRequest += (request) =>
        {
            if (request.Username == "user1")
                return TnfsAuthenticationResponse.Success(user1Id, "user1");
            else if (request.Username == "user2")
                return TnfsAuthenticationResponse.Success(user2Id, "user2");
            else
                return TnfsAuthenticationResponse.Failed("Unknown user");
        };

        var mount1 = CreateMountRequest("user1", "pass1");
        var mount2 = CreateMountRequest("user2", "pass2");

        // Act
        var response1 = _handler.HandleMount(mount1);
        var response2 = _handler.HandleMount(mount2);

        // Assert
        Assert.That(response1.Header.Status, Is.EqualTo((byte)TnfsReturnCode.SUCCESS));
        Assert.That(response2.Header.Status, Is.EqualTo((byte)TnfsReturnCode.SUCCESS));
        Assert.That(response1.Header.SessionId, Is.Not.EqualTo(response2.Header.SessionId));

        var session1 = _sessionManager!.FindSession(response1.Header.SessionId);
        var session2 = _sessionManager.FindSession(response2.Header.SessionId);

        Assert.That(session1!.UserId, Is.EqualTo(user1Id));
        Assert.That(session2!.UserId, Is.EqualTo(user2Id));
        Assert.That(session1.Username, Is.EqualTo("user1"));
        Assert.That(session2.Username, Is.EqualTo("user2"));
    }

    // Helper method to create MOUNT request
    private TnfsMessage CreateMountRequest(string username, string password, string mountPoint = "/")
    {
        using var buffer = TnfsBuffer.Rent(512);
        var span = buffer.Span;

        int offset = 0;

        // TNFS header (4 bytes) - will be filled by ParseRequest
        span[0] = 0x00;  // Session ID LSB
        span[1] = 0x00;  // Session ID MSB
        span[2] = 0x00;  // Retry number
        span[3] = TnfsConstants.TNFS_MOUNT;  // Command
        offset = 4;

        // Protocol version (2 bytes little-endian)
        span[offset++] = (byte)TnfsConstants.PROTOVERSION_LSB;
        span[offset++] = (byte)TnfsConstants.PROTOVERSION_MSB;

        // Mount point (null-terminated)
        var mountBytes = System.Text.Encoding.ASCII.GetBytes(mountPoint);
        mountBytes.CopyTo(span.Slice(offset));
        offset += mountBytes.Length;
        span[offset++] = 0;

        // Username (null-terminated)
        if (!string.IsNullOrEmpty(username))
        {
            var userBytes = System.Text.Encoding.ASCII.GetBytes(username);
            userBytes.CopyTo(span.Slice(offset));
            offset += userBytes.Length;
        }
        span[offset++] = 0;

        // Password (null-terminated)
        if (!string.IsNullOrEmpty(password))
        {
            var passBytes = System.Text.Encoding.ASCII.GetBytes(password);
            passBytes.CopyTo(span.Slice(offset));
            offset += passBytes.Length;
        }
        span[offset++] = 0;

        // Create TnfsMessage using ParseRequest
        var message = TnfsMessage.ParseRequest(
            span.Slice(0, offset),
            IPAddress.Parse("127.0.0.1"),
            12345,
            0);

        return message;
    }
}
