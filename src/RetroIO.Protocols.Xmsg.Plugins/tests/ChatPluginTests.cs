//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Grpc.Net.Client;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Api;
using RetroIO.Protocols.Xmsg.Api.Node;
using RetroIO.Protocols.Xmsg.Chat;
using RetroIO.Protocols.Xmsg.Node.Services;
using RetroIO.Protocols.Xmsg.Plugins.Client;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Plugins.Tests
{
    /// <summary>
    /// The C# chat server runs as a plug-in program over a real gRPC stream, and answers a join that
    /// arrives at the node off the wire.
    /// </summary>
    /// <remarks>
    /// The path: a SINTRAN-style letter to <c>*CHAT</c> enters the node, the bridge forwards it to
    /// the name the plug-in claimed, the host hands it over gRPC to the plug-in, the chat server
    /// answers with a welcome, and the welcome leaves the node as a frame for system 100.
    /// NOT VERIFIED against a machine: the frame around the letter is built by our own frame
    /// builder, and no capture of the SINTRAN CHAT client joining a server on another machine exists.
    /// </remarks>
    public sealed class ChatPluginTests
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

        /// <summary>
        /// A join from system 100 comes back as a welcome addressed to system 100.
        /// </summary>
        /// <returns>
        /// The test.
        /// </returns>
        [Fact]
        public async Task ChatPlugin_AnswersAJoinFromTheWire()
        {
            await using NodeUnderTest node = await NodeUnderTest.StartAsync();

            await using PluginConnection plugin = await PluginConnection.ConnectAsync(node.Channel, "chat-test", "1");
            Assert.Equal(102, plugin.SystemNumber);

            ChatServer chat = new ChatServer(plugin.Kernel, plugin.Names);
            Assert.Equal(XroutError.XRSOK, chat.Open("*CHAT", 4, "WELCOME"));

            XmsgMagicNumber client = XmsgMagicNumber.Create(100, 5, 43);
            ChatMessage join = new ChatMessage(ChatMessageKind.Join, "RONNY", string.Empty);
            byte[] data = new byte[join.ByteCount];
            join.Encode(data);
            XmsgFrame letter = new XmsgFrameBuilder()
                .Between(102, 100)
                .WithDatagramSequence(1)
                .WithProtocol(SintranProtocolId.Tad)
                .WithFlags2(0x0108)
                .WithSubHeaderControl(0x10, 0x86, 0x84)
                .WithEndpoints(102, 0, 100, client.PortWord)
                .WithControlService(0x04000041)
                .Build();
            letter.TrailingBytes = XroutLetter.Build(123, "*CHAT", data);

            node.OnPump(n => n.Route(letter));

            // The join crosses the stream to the plug-in; the chat server's loop picks it up.
            Stopwatch waited = Stopwatch.StartNew();
            int handled = 0;
            while (handled == 0 && waited.Elapsed < Patience)
            {
                handled = chat.Poll();
                await Task.Delay(5);
            }

            Assert.Equal(1, handled);

            // The welcome crosses back and leaves the node as a frame.
            XmsgFrame? sent = await node.WaitForFrameAsync(Patience);
            Assert.NotNull(sent);
            Assert.Equal(100, sent!.SubHeader!.DestinationSystem);
            Assert.Equal(client.PortWord, sent.SubHeader!.DestinationPort);

            // The data sits at offset 28, as on the wire from a SINTRAN task (see ChatOverTheWireTests).
            byte[] raw = sent.ToArray();
            ChatMessage welcome;
            Assert.True(ChatMessage.TryDecode(new ReadOnlySpan<byte>(raw, 28, raw.Length - 28), out welcome));
            Assert.Equal(ChatMessageKind.Welcome, welcome.Kind);
        }

        /// <summary>
        /// A C# chat client connected to the node joins the chat server connected to the same node,
        /// and both talk through the node's XMSG ports - the node is the XMSG machine for both.
        /// </summary>
        /// <returns>
        /// The test.
        /// </returns>
        [Fact]
        public async Task ChatClient_OnTheNode_JoinsAndTalks()
        {
            await using NodeUnderTest node = await NodeUnderTest.StartAsync();

            await using PluginConnection serverSide = await PluginConnection.ConnectAsync(node.Channel, "chat-server", "1");
            ChatServer chat = new ChatServer(serverSide.Kernel, serverSide.Names);
            Assert.Equal(XroutError.XRSOK, chat.Open("*CHAT", 4, "WELCOME"));

            await using PluginConnection clientSide = await PluginConnection.ConnectAsync(node.Channel, "chat-client", "1");
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

            // What the user says reaches the server and comes back to the room.
            Assert.True(ronny.Say("HELLO"));
            bool echoed = false;
            waited.Restart();
            while (!echoed && waited.Elapsed < Patience)
            {
                chat.Poll();
                IReadOnlyList<ChatMessage> got = ronny.Poll();
                for (int i = 0; i < got.Count; i++)
                {
                    echoed |= got[i].Text.Contains("HELLO", StringComparison.Ordinal);
                }

                await Task.Delay(5);
            }

            Assert.True(echoed);
        }

        /// <summary>
        /// A chat client program that disconnects without leaving loses its seat: the next message to
        /// it comes back to the chat server program over the connection, and the server removes it.
        /// The same nickname can then join again.
        /// </summary>
        /// <returns>
        /// The test.
        /// </returns>
        /// <remarks>
        /// Found live on 2026-11-10: a retro-chat that ended early kept RONNY taken until restart.
        /// </remarks>
        [Fact]
        public async Task ClientThatDisconnects_IsRemovedFromTheRoom()
        {
            await using NodeUnderTest node = await NodeUnderTest.StartAsync();

            await using PluginConnection serverSide = await PluginConnection.ConnectAsync(node.Channel, "chat-server", "1");
            ChatServer chat = new ChatServer(serverSide.Kernel, serverSide.Names);
            Assert.Equal(XroutError.XRSOK, chat.Open("*CHAT", 4, "WELCOME"));

            PluginConnection ronnySide = await PluginConnection.ConnectAsync(node.Channel, "ronny", "1");
            ChatClient ronny = new ChatClient(ronnySide.Kernel, ronnySide.Names, "RONNY");
            await JoinAsync(chat, ronny);

            await using PluginConnection kariSide = await PluginConnection.ConnectAsync(node.Channel, "kari", "1");
            ChatClient kari = new ChatClient(kariSide.Kernel, kariSide.Names, "KARI");
            await JoinAsync(chat, kari);
            Assert.Equal(2, chat.MemberCount);

            // RONNY's program goes away without a Leave.
            await ronnySide.DisposeAsync();
            await Task.Delay(100);

            Assert.True(kari.Say("anyone there?"));
            Stopwatch waited = Stopwatch.StartNew();
            while (chat.MemberCount != 1 && waited.Elapsed < Patience)
            {
                chat.Poll();
                kari.Poll();
                await Task.Delay(5);
            }

            Assert.Equal(1, chat.MemberCount);

            await using PluginConnection againSide = await PluginConnection.ConnectAsync(node.Channel, "ronny-again", "1");
            ChatClient again = new ChatClient(againSide.Kernel, againSide.Names, "RONNY");
            await JoinAsync(chat, again);
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
        /// The answer to the Hello tells the program the node's revision, features and log level, and
        /// a program's log lines reach the node's logging under XmsgProgram.&lt;name&gt;.&lt;category&gt;
        /// with their level - lines below the node's level are not kept.
        /// </summary>
        /// <returns>
        /// The test.
        /// </returns>
        [Fact]
        public async Task ProgramLogLines_ReachTheNodesLogging_WithTheirLevel()
        {
            await using NodeUnderTest node = await NodeUnderTest.StartAsync();
            CapturingLogs logs = new CapturingLogs();
            node.Host.Logs = logs;
            node.Host.LogMinimum = Microsoft.Extensions.Logging.LogLevel.Information;

            await using PluginConnection program = await PluginConnection.ConnectAsync(node.Channel, "logger", "1");
            Assert.Equal(1u, program.NodeProtocolRevision);
            Assert.Contains("log", program.NodeFeatures);
            Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, program.NodeLogMinimum);

            using PluginLoggerProvider factory = new PluginLoggerProvider(program);
            Microsoft.Extensions.Logging.ILogger room = factory.CreateLogger("Room");
            Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(room, "too small to keep");
            Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(room, "room is nearly full");

            Stopwatch waited = Stopwatch.StartNew();
            while (logs.Lines.Count == 0 && waited.Elapsed < Patience)
            {
                await Task.Delay(5);
            }

            await Task.Delay(100);
            Assert.Single(logs.Lines);
            Assert.Equal("XmsgProgram.logger.Room", logs.Lines.ToArray()[0].Category);
            Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, logs.Lines.ToArray()[0].Level);
            Assert.Equal("room is nearly full", logs.Lines.ToArray()[0].Text);
        }

        /// <summary>
        /// A log store for the tests: keeps every line with its category and level.
        /// </summary>
        private sealed class CapturingLogs : Microsoft.Extensions.Logging.ILoggerFactory
        {
            internal ConcurrentQueue<(string Category, Microsoft.Extensions.Logging.LogLevel Level, string Text)> Lines { get; } =
                new ConcurrentQueue<(string Category, Microsoft.Extensions.Logging.LogLevel Level, string Text)>();

            public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider)
            {
            }

            public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName)
            {
                return new Line(this, categoryName);
            }

            public void Dispose()
            {
            }

            private sealed class Line : Microsoft.Extensions.Logging.ILogger
            {
                private readonly CapturingLogs _owner;
                private readonly string _category;

                internal Line(CapturingLogs owner, string category)
                {
                    _owner = owner;
                    _category = category;
                }

                public IDisposable? BeginScope<TState>(TState state)
                    where TState : notnull
                {
                    return null;
                }

                public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel)
                {
                    return true;
                }

                public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                {
                    _owner.Lines.Enqueue((_category, logLevel, formatter(state, exception)));
                }
            }
        }

        /// <summary>
        /// With a token set on the node, a program that sends the wrong token, or none, is refused,
        /// and one that sends the right token gets in.
        /// </summary>
        /// <returns>
        /// The test.
        /// </returns>
        [Fact]
        public async Task Token_IsRequiredWhenSet()
        {
            await using NodeUnderTest node = await NodeUnderTest.StartAsync();
            node.Host.Token = "S3CRET";

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => PluginConnection.ConnectAsync(node.Channel, "no-token", "1"));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => PluginConnection.ConnectAsync(node.Channel, "wrong-token", "1", "S3CRET2"));

            await using PluginConnection right = await PluginConnection.ConnectAsync(node.Channel, "right-token", "1", "S3CRET");
            Assert.Equal(102, right.SystemNumber);
        }

        /// <summary>
        /// When the plug-in goes away its name is released, so the node no longer answers for it.
        /// </summary>
        /// <returns>
        /// The test.
        /// </returns>
        [Fact]
        public async Task PluginThatDisconnects_LosesItsName()
        {
            await using NodeUnderTest node = await NodeUnderTest.StartAsync();

            PluginConnection plugin = await PluginConnection.ConnectAsync(node.Channel, "chat-test", "1");
            ChatServer chat = new ChatServer(plugin.Kernel, plugin.Names);
            Assert.Equal(XroutError.XRSOK, chat.Open("*CHAT", 4, "WELCOME"));
            Assert.True(await node.AskOnPumpAsync(n => node.Bridge.AnswersName("*CHAT")));

            await plugin.DisposeAsync();

            Stopwatch waited = Stopwatch.StartNew();
            bool stillThere = true;
            while (stillThere && waited.Elapsed < Patience)
            {
                stillThere = await node.AskOnPumpAsync(n => node.Bridge.AnswersName("*CHAT"));
                await Task.Delay(5);
            }

            Assert.False(stillThere);
        }

        /// <summary>
        /// A node with the plug-in host, served over an in-memory gRPC server, and a pump thread
        /// that is the only thread touching the node - as in a real node.
        /// </summary>
        private sealed class NodeUnderTest : IAsyncDisposable
        {
            private readonly IHost _web;
            private readonly Thread _pump;
            private readonly CancellationTokenSource _stop;
            private readonly ConcurrentQueue<Action<XmsgServerHost>> _onPump;
            private readonly ConcurrentQueue<XmsgFrame> _frames;

            private NodeUnderTest(IHost web, XmsgServerHost node, XmsgKernelServer bridge, PluginServerHost host)
            {
                _web = web;
                Node = node;
                Bridge = bridge;
                Host = host;
                _stop = new CancellationTokenSource();
                _onPump = new ConcurrentQueue<Action<XmsgServerHost>>();
                _frames = new ConcurrentQueue<XmsgFrame>();
                TestServer server = web.GetTestServer();
                Channel = GrpcChannel.ForAddress(server.BaseAddress, new GrpcChannelOptions { HttpHandler = server.CreateHandler() });
                _pump = new Thread(PumpLoop) { IsBackground = true, Name = "node pump" };
                _pump.Start();
            }

            internal XmsgServerHost Node { get; }

            internal XmsgKernelServer Bridge { get; }

            internal PluginServerHost Host { get; }

            internal GrpcChannel Channel { get; }

            internal static async Task<NodeUnderTest> StartAsync()
            {
                XmsgKernelServer bridge = XmsgKernelServer.Create(102, 0x1111, "*PLUGINS", 4);
                XroutDirectory directory = new XroutDirectory();
                PluginServerHost host = new PluginServerHost(bridge, directory);
                XmsgServerHost node = new XmsgServerHost(102);
                node.Register(bridge);

                IHost web = await new HostBuilder()
                    .ConfigureWebHost(builder => builder
                        .UseTestServer()
                        .ConfigureServices(services =>
                        {
                            services.AddGrpc();
                            services.AddSingleton(host);
                        })
                        .Configure(app =>
                        {
                            app.UseRouting();
                            app.UseEndpoints(endpoints => endpoints.MapGrpcService<PluginConnectionService>());
                        }))
                    .StartAsync();

                return new NodeUnderTest(web, node, bridge, host);
            }

            internal void OnPump(Action<XmsgServerHost> action)
            {
                _onPump.Enqueue(action);
            }

            internal Task<T> AskOnPumpAsync<T>(Func<XmsgServerHost, T> question)
            {
                TaskCompletionSource<T> answer = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                _onPump.Enqueue(n => answer.SetResult(question(n)));
                return answer.Task;
            }

            internal async Task<XmsgFrame?> WaitForFrameAsync(TimeSpan patience)
            {
                Stopwatch waited = Stopwatch.StartNew();
                while (waited.Elapsed < patience)
                {
                    XmsgFrame? frame;
                    if (_frames.TryDequeue(out frame))
                    {
                        return frame;
                    }

                    await Task.Delay(5);
                }

                return null;
            }

            public async ValueTask DisposeAsync()
            {
                _stop.Cancel();
                _pump.Join();
                Channel.Dispose();
                await _web.StopAsync();
                _web.Dispose();
                _stop.Dispose();
            }

            private void PumpLoop()
            {
                while (!_stop.IsCancellationRequested)
                {
                    Action<XmsgServerHost>? action;
                    while (_onPump.TryDequeue(out action))
                    {
                        action(Node);
                    }

                    Host.Pump();

                    IReadOnlyList<XmsgFrame> frames = Node.DrainPending();
                    for (int i = 0; i < frames.Count; i++)
                    {
                        _frames.Enqueue(frames[i]);
                    }

                    Thread.Sleep(2);
                }
            }
        }
    }
}
