//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RetroIO.Identity;
using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer;
using RetroIO.Protocols.Econet.FileServer.Bulk;
using RetroIO.Protocols.Econet.FileServer.Configuration;
using RetroIO.Protocols.Econet.FileServer.Discs;
using RetroIO.Protocols.Econet.FileServer.Handles;
using RetroIO.Protocols.Econet.FileServer.Paths;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.FileServer.Users;
using RetroIO.Protocols.Econet.Protocol;
using RetroIO.Protocols.Econet.Protocol.Aun;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;
using RetroIO.Server.Core.FileSystem;
using RetroIO.Tests.TNFS;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Shared mock file server for Econet OSCLI and catalogue unit tests.
/// </summary>
internal sealed class EconetFsTestHarness
{
    // The in-memory store; also the seed/inspection target even when a recording decorator is wired.
    public MockFileSystemBackend FileSystem { get; }

    // The backend actually wired into the server. Same object as FileSystem by default, or the
    // RecordingFileSystemBackend when the recording constructor is used.
    public IFileSystemBackend Backend { get; }

    public FileServerCommandHandler Handler { get; }
    public IRetroIDP IdentityProvider { get; }
    public EconetActivityLog ActivityLog { get; }
    public EconetTestMonitor Monitor { get; }

    // Default: a plain in-memory mock is both the store and the wired backend.
    public EconetFsTestHarness()
        : this(new MockFileSystemBackend(), null)
    {
    }

    // Recording variant: the decorator is wired into the server so its calls are recorded, while
    // FileSystem still exposes the inner mock for direct seeding/inspection (both share one store).
    public EconetFsTestHarness(RecordingFileSystemBackend recording)
        : this(recording.Inner, recording)
    {
    }

    // Wire-profile variant: injects a WireProfileResolver so tests can exercise the Classic1983
    // divergences against the default client (net 1, station 1).
    public EconetFsTestHarness(WireProfileResolver wireProfiles)
        : this(new MockFileSystemBackend(), null, wireProfiles)
    {
    }

    private EconetFsTestHarness(MockFileSystemBackend mock, IFileSystemBackend? wired, WireProfileResolver? wireProfiles = null)
    {
        FileSystem = mock;
        IFileSystemBackend backend = wired ?? mock;
        Backend = backend;

        // Seed the inner mock directly (not through any recording decorator) so seed traffic is not
        // recorded — tests should see only the calls their opcode makes.
        EconetSampleCatalogFixture.Seed(mock);

        EconetUserDirectory users = EconetTestUsers.CreateDirectory(out IRetroIDP idp);
        IdentityProvider = idp;

        DiscCatalog discs = new DiscCatalog(backend, "/econet-root", "RetroFS");
        FileServerSessionManager sessions = new FileServerSessionManager(backend, users, discs, "/econet-root");
        FileServerHandleManager handles = new FileServerHandleManager();
        PathResolver paths = new PathResolver(backend, discs, "/econet-root");
        BulkTransferManager bulk = new BulkTransferManager();

        ActivityLog = new EconetActivityLog();
        Monitor = new EconetTestMonitor(ActivityLog);

        FileServerContext context = new FileServerContext(
            backend,
            users,
            discs,
            sessions,
            handles,
            paths,
            bulk,
            new FileServerConfiguration(),
            new FileServerClock(),
            "/econet-root",
            "RetroFS",
            FileServerCommandHandler.DefaultVersionString,
            ActivityLog,
            wireProfiles: wireProfiles);

        Handler = new FileServerCommandHandler(context, NullLogger<FileServerCommandHandler>.Instance);
    }

    // Login helpers. SYST is an admin; USER is a normal (non-admin) user — the valid-vs-wrong-privilege
    // pair for gating tests. Both accept an empty password (see EconetTestUsers).
    public EconetMessage LoginAsSyst() => HandleOsCli("I AM SYST \r");
    public EconetMessage LoginAsUser() => HandleOsCli("I AM USER \r");

    // Sends a logged-in-shaped fsop request. Works whether or not a session exists — the server
    // decides. Clearer name than HandleLoggedIn at the protocol-test call site.
    public EconetMessage Send(byte opcode, byte[] parameters) => HandleLoggedIn(opcode, parameters);
    public EconetMessage Send(byte opcode) => HandleLoggedIn(opcode, []);

    public EconetMessage HandleOsCli(string command)
    {
        EconetMessage request = CreateRequest(FileServerOpcodes.OsCli, Encoding.ASCII.GetBytes(command));
        return Handler.Handle(request)!;
    }

    public EconetMessage HandleLoggedIn(byte opcode, byte[] parameters, byte urd = AcornFileHandles.WireUrd, byte cwd = AcornFileHandles.WireCwd, byte lib = AcornFileHandles.WireLib)
    {
        EconetMessage request = CreateLoggedInRequest(opcode, parameters, urd, cwd, lib);
        return Handler.Handle(request)!;
    }

    public static EconetMessage CreateRequest(byte opcode, byte[] parameters, byte replyPort = 0x80)
    {
        byte[] payload = new byte[5 + parameters.Length];
        payload[0] = replyPort;
        payload[1] = opcode;
        for (int i = 0; i < parameters.Length; i++)
            payload[5 + i] = parameters[i];

        return CreateMessage(payload);
    }

    public static EconetMessage CreateLoggedInRequest(
        byte opcode,
        byte[] parameters,
        byte urd = AcornFileHandles.WireUrd,
        byte cwd = AcornFileHandles.WireCwd,
        byte lib = AcornFileHandles.WireLib,
        byte replyPort = 0x80)
    {
        byte[] payload = new byte[5 + parameters.Length];
        payload[0] = replyPort;
        payload[1] = opcode;
        payload[2] = urd;
        payload[3] = cwd;
        payload[4] = lib;
        for (int i = 0; i < parameters.Length; i++)
            payload[5 + i] = parameters[i];

        return CreateMessage(payload);
    }

    public static EconetMessage CreateMessage(byte[] payload)
    {
        return new EconetMessage
        {
            PacketType = AunPacketType.Unicast,
            EconetPort = EconetPorts.FileServer,
            Control = 0x80,
            Sequence = 1,
            Payload = payload,
            ClientAddress = IPAddress.Loopback,
            ClientPort = 50000,
            ClientNet = 1,
            ClientStation = 1,
        };
    }
}
