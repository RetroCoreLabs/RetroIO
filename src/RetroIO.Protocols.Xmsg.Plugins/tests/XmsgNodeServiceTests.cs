//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Grpc.Net.Client;

using RetroIO.Protocols.Xmsg.Chat;
using RetroIO.Protocols.Xmsg.Hub;
using RetroIO.Protocols.Xmsg.Plugins.Client;
using RetroIO.Protocols.Xmsg.Plugins.Server;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Plugins.Tests
{
    /// <summary>
    /// The node service as RetroServer runs it: joined to a real hub over TCP, with the program port
    /// open on a real socket.
    /// </summary>
    public sealed class XmsgNodeServiceTests
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

        /// <summary>
        /// The chat server program and a chat client program both connect to the running node over
        /// its gRPC port, and the client joins and is answered.
        /// </summary>
        /// <returns>
        /// The test.
        /// </returns>
        [Fact]
        public async Task ChatServerAndClient_ConnectToTheRunningNode_AndTalk()
        {
            HubServer hub = new HubServer(IPAddress.Loopback, 0);
            hub.Start();
            string state = Path.Combine(Path.GetTempPath(), "xmsg-node-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(state);
            int port = FreePort();
            XmsgNodeService service = new XmsgNodeService(
                new XmsgNodeServiceOptions
                {
                    SystemNumber = 19998,
                    PeerSystemNumber = 102,
                    Ethernet = "tcp:127.0.0.1:" + hub.Port,
                    PluginPort = port,
                    StateDirectory = state,
                },
                null);

            try
            {
                await service.StartAsync(CancellationToken.None);
                using GrpcChannel channel = GrpcChannel.ForAddress("http://127.0.0.1:" + port);

                await using PluginConnection serverSide = await ConnectWhenOpenAsync(channel, "chat-server");
                Assert.Equal(19998, serverSide.SystemNumber);
                ChatServer chat = new ChatServer(serverSide.Kernel, serverSide.Names);
                Assert.Equal(XroutError.XRSOK, chat.Open("*CHAT", 4, "WELCOME"));

                await using PluginConnection clientSide = await PluginConnection.ConnectAsync(channel, "chat-client", "1");
                ChatClient ronny = new ChatClient(clientSide.Kernel, clientSide.Names, "RONNY");
                Assert.Equal(XroutError.XRSOK, ronny.Join("*CHAT", string.Empty));

                List<ChatMessage> heard = new List<ChatMessage>();
                Stopwatch waited = Stopwatch.StartNew();
                while (!ronny.IsJoined && waited.Elapsed < Patience)
                {
                    chat.Poll();
                    heard.AddRange(ronny.Poll());
                    await Task.Delay(5);
                }

                Assert.True(ronny.IsJoined);
                Assert.Contains(heard, m => m.Kind == ChatMessageKind.Welcome);
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
                service.Dispose();
                hub.Stop();
                Directory.Delete(state, true);
            }
        }

        /// <summary>
        /// A client program that goes away without leaving is removed from the room the next time
        /// the server writes to it, on the running node over real sockets.
        /// </summary>
        /// <returns>
        /// The test.
        /// </returns>
        /// <remarks>
        /// Found live on 2026-11-10: with RetroServer running, CAROL's retro-chat was killed and
        /// stayed in the room through every later broadcast. The in-memory test of the same case
        /// passes. Each program here has its own channel, so its own TCP connection, as separate
        /// processes do.
        /// </remarks>
        [Fact]
        public async Task ClientThatDisconnects_IsRemovedFromTheRoom_OnTheRunningNode()
        {
            HubServer hub = new HubServer(IPAddress.Loopback, 0);
            hub.Start();
            string state = Path.Combine(Path.GetTempPath(), "xmsg-node-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(state);
            int port = FreePort();
            XmsgNodeService service = new XmsgNodeService(
                new XmsgNodeServiceOptions
                {
                    SystemNumber = 19998,
                    PeerSystemNumber = 102,
                    Ethernet = "tcp:127.0.0.1:" + hub.Port,
                    PluginPort = port,
                    StateDirectory = state,
                },
                null);

            try
            {
                await service.StartAsync(CancellationToken.None);
                string address = "http://127.0.0.1:" + port;
                using GrpcChannel serverChannel = GrpcChannel.ForAddress(address);
                using GrpcChannel carolChannel = GrpcChannel.ForAddress(address);
                using GrpcChannel daveChannel = GrpcChannel.ForAddress(address);

                await using PluginConnection serverSide = await ConnectWhenOpenAsync(serverChannel, "chat-server");
                ChatServer chat = new ChatServer(serverSide.Kernel, serverSide.Names);
                Assert.Equal(XroutError.XRSOK, chat.Open("*CHAT", 4, "WELCOME"));

                PluginConnection carolSide = await PluginConnection.ConnectAsync(carolChannel, "carol", "1");
                ChatClient carol = new ChatClient(carolSide.Kernel, carolSide.Names, "CAROL");
                await JoinAsync(chat, carol);

                await using PluginConnection daveSide = await PluginConnection.ConnectAsync(daveChannel, "dave", "1");
                ChatClient dave = new ChatClient(daveSide.Kernel, daveSide.Names, "DAVE");
                await JoinAsync(chat, dave);
                Assert.Equal(2, chat.MemberCount);

                // CAROL's program goes away without a Leave.
                await carolSide.DisposeAsync();
                await Task.Delay(200);

                Assert.True(dave.Say("anyone there?"));
                Stopwatch waited = Stopwatch.StartNew();
                while (chat.MemberCount != 1 && waited.Elapsed < Patience)
                {
                    chat.Poll();
                    dave.Poll();
                    await Task.Delay(5);
                }

                Assert.Equal(1, chat.MemberCount);
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
                service.Dispose();
                hub.Stop();
                Directory.Delete(state, true);
            }
        }

        /// <summary>
        /// A client program that is KILLED - its socket reset, no stream end, no Leave - is removed
        /// from the room the next time the server writes to it.
        /// </summary>
        /// <returns>
        /// The test.
        /// </returns>
        /// <remarks>
        /// The live case of 2026-11-10: a killed retro-chat stayed in the room. A program that is
        /// killed never ends its gRPC stream; its socket is reset by the operating system. CAROL
        /// reaches the node through a relay here, and the relay resets both sockets (linger 0),
        /// which is what the operating system does for a killed process.
        /// </remarks>
        [Fact]
        public async Task ClientThatIsKilled_IsRemovedFromTheRoom_OnTheRunningNode()
        {
            HubServer hub = new HubServer(IPAddress.Loopback, 0);
            hub.Start();
            string state = Path.Combine(Path.GetTempPath(), "xmsg-node-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(state);
            int port = FreePort();
            XmsgNodeService service = new XmsgNodeService(
                new XmsgNodeServiceOptions
                {
                    SystemNumber = 19998,
                    PeerSystemNumber = 102,
                    Ethernet = "tcp:127.0.0.1:" + hub.Port,
                    PluginPort = port,
                    StateDirectory = state,
                },
                null);
            ResettingRelay? relay = null;

            try
            {
                await service.StartAsync(CancellationToken.None);
                string address = "http://127.0.0.1:" + port;
                using GrpcChannel serverChannel = GrpcChannel.ForAddress(address);
                using GrpcChannel daveChannel = GrpcChannel.ForAddress(address);

                await using PluginConnection serverSide = await ConnectWhenOpenAsync(serverChannel, "chat-server");
                ChatServer chat = new ChatServer(serverSide.Kernel, serverSide.Names);
                Assert.Equal(XroutError.XRSOK, chat.Open("*CHAT", 4, "WELCOME"));

                relay = new ResettingRelay(port);
                using GrpcChannel carolChannel = GrpcChannel.ForAddress("http://127.0.0.1:" + relay.Port);
                PluginConnection carolSide = await PluginConnection.ConnectAsync(carolChannel, "carol", "1");
                ChatClient carol = new ChatClient(carolSide.Kernel, carolSide.Names, "CAROL");
                await JoinAsync(chat, carol);

                await using PluginConnection daveSide = await PluginConnection.ConnectAsync(daveChannel, "dave", "1");
                ChatClient dave = new ChatClient(daveSide.Kernel, daveSide.Names, "DAVE");
                await JoinAsync(chat, dave);
                Assert.Equal(2, chat.MemberCount);

                // CAROL is killed: her socket is reset, her stream never ends.
                relay.Reset();
                await Task.Delay(500);

                Assert.True(dave.Say("anyone there?"));
                Stopwatch waited = Stopwatch.StartNew();
                while (chat.MemberCount != 1 && waited.Elapsed < Patience)
                {
                    chat.Poll();
                    dave.Poll();
                    await Task.Delay(5);
                }

                Assert.Equal(1, chat.MemberCount);
            }
            finally
            {
                relay?.Dispose();
                await service.StopAsync(CancellationToken.None);
                service.Dispose();
                hub.Stop();
                Directory.Delete(state, true);
            }
        }

        /// <summary>
        /// Joins a client and waits until the server has let it in.
        /// </summary>
        /// <param name="chat">
        /// The server.
        /// </param>
        /// <param name="client">
        /// The client.
        /// </param>
        /// <returns>
        /// The wait.
        /// </returns>
        private static async Task JoinAsync(ChatServer chat, ChatClient client)
        {
            Assert.Equal(XroutError.XRSOK, client.Join("*CHAT", string.Empty));
            Stopwatch waited = Stopwatch.StartNew();
            while (!client.IsJoined && waited.Elapsed < Patience)
            {
                chat.Poll();
                client.Poll();
                await Task.Delay(5);
            }

            Assert.True(client.IsJoined);
        }

        /// <summary>
        /// Connects, retrying while the port is still opening.
        /// </summary>
        /// <param name="channel">
        /// The channel.
        /// </param>
        /// <param name="name">
        /// The program's name.
        /// </param>
        /// <returns>
        /// The connection.
        /// </returns>
        private static async Task<PluginConnection> ConnectWhenOpenAsync(GrpcChannel channel, string name)
        {
            Stopwatch waited = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    return await PluginConnection.ConnectAsync(channel, name, "1");
                }
                catch (Grpc.Core.RpcException) when (waited.Elapsed < Patience)
                {
                    await Task.Delay(50);
                }
            }
        }

        /// <summary>
        /// Asks the system for a port nobody is using.
        /// </summary>
        /// <returns>
        /// The port.
        /// </returns>
        private static int FreePort()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        /// <summary>
        /// A one-connection TCP relay to the node that can reset both of its sockets, as the
        /// operating system does for a killed process.
        /// </summary>
        private sealed class ResettingRelay : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly int _target;
            private TcpClient? _inside;
            private TcpClient? _outside;

            public ResettingRelay(int target)
            {
                _target = target;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _ = AcceptAsync();
            }

            public int Port { get; }

            public void Reset()
            {
                Cut(_outside);
                _outside = null;
                Cut(_inside);
                _inside = null;
            }

            public void Dispose()
            {
                Reset();
                _listener.Stop();
            }

            private static void Cut(TcpClient? client)
            {
                if (client == null)
                {
                    return;
                }

                try
                {
                    // Linger on with a zero timeout: Close sends a reset, not a FIN.
                    client.LingerState = new LingerOption(true, 0);
                    client.Close();
                }
                catch (ObjectDisposedException)
                {
                    // Already cut.
                }
            }

            private async Task AcceptAsync()
            {
                try
                {
                    _outside = await _listener.AcceptTcpClientAsync();
                    _inside = new TcpClient();
                    await _inside.ConnectAsync(IPAddress.Loopback, _target);
                    _ = CopyAsync(_outside.GetStream(), _inside.GetStream());
                    _ = CopyAsync(_inside.GetStream(), _outside.GetStream());
                }
                catch (SocketException)
                {
                    // The relay was stopped.
                }
                catch (ObjectDisposedException)
                {
                    // The relay was stopped.
                }
            }

            private static async Task CopyAsync(Stream from, Stream to)
            {
                try
                {
                    await from.CopyToAsync(to);
                }
                catch (IOException)
                {
                    // Reset: the copy ends.
                }
                catch (ObjectDisposedException)
                {
                    // Reset: the copy ends.
                }
            }
        }
    }
}
