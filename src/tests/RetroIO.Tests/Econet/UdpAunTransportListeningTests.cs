//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.Aun;
using RetroIO.Protocols.Econet.Server.Transport;

namespace RetroIO.Tests.Econet;

/// <summary>
/// The UDP transport says when its port is open, so a host can wait for it before calling itself started.
/// </summary>
[TestFixture]
public sealed class UdpAunTransportListeningTests
{
    [Test]
    public async Task Listening_IsDoneOnlyOnceThePortIsBound()
    {
        int port = FreeUdpPort();
        using UdpAunTransport transport = new UdpAunTransport(port, new AunAddressMapper(network: 1), NullLogger<UdpAunTransport>.Instance);
        Assert.That(transport.Listening.IsCompleted, Is.False, "nothing is bound before the transport runs");

        using CancellationTokenSource stop = new CancellationTokenSource();
        Task run = transport.RunAsync(stop.Token);
        await transport.Listening.WaitAsync(TimeSpan.FromSeconds(10));

        // Bound: a second socket on the same port without address reuse is refused.
        using Socket probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.ExclusiveAddressUse = true;
        Assert.Throws<SocketException>(() => probe.Bind(new IPEndPoint(IPAddress.Any, port)));

        stop.Cancel();
        try
        {
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
            // Stopping by cancellation is the normal end.
        }
    }

    private static int FreeUdpPort()
    {
        using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
