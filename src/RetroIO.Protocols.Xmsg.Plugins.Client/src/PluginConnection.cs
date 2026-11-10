//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

using Grpc.Core;
using Grpc.Net.Client;

using RetroIO.Protocols.Xmsg.Api;
using RetroIO.Protocols.Xmsg.Plugins.Contracts;

namespace RetroIO.Protocols.Xmsg.Plugins.Client
{
    /// <summary>
    /// A C# plug-in's connection to an XMSG node: one gRPC stream, and the XMSG kernel and XROUT
    /// name table a task-style server is written against, carried over it.
    /// </summary>
    /// <remarks>
    /// <para><b>Use</b></para>
    /// <code>
    /// PluginConnection node = await PluginConnection.ConnectAsync(channel, "retro-chat-server", "1.0");
    /// ChatServer chat = new ChatServer(node.Kernel, node.Names);
    /// chat.Open("*CHAT", 20, "welcome");
    /// while (true) { chat.Poll(); ... }
    /// </code>
    /// <para><b>Calls wait for the node's answer</b></para>
    /// The kernel and name-table calls are synchronous, as XMSG calls are on a SINTRAN machine, so a
    /// call that needs the node (open a port, claim a name, send) blocks until the node answers or
    /// <see cref="CallTimeout"/> passes. A timeout is reported as an error status, never as success.
    /// Do not make those calls on a thread that the stream's own reading depends on.
    /// <para><b>When the stream ends</b></para>
    /// The node releases every port and name the plug-in held. This side marks itself
    /// <see cref="IsClosed"/>, and every later call fails. Reconnecting means a new connection
    /// and a new server object, as a restarted SINTRAN server program starts afresh.
    /// </remarks>
    public sealed class PluginConnection : IAsyncDisposable
    {
        private readonly AsyncDuplexStreamingCall<PluginRequest, HostEvent> _call;
        private readonly ConcurrentDictionary<uint, TaskCompletionSource<Result>> _waiting;
        private readonly SemaphoreSlim _writeLock;
        private readonly CancellationTokenSource _stop;
        private Task _reader;
        private int _nextId;
        private volatile bool _closed;

        private PluginConnection(AsyncDuplexStreamingCall<PluginRequest, HostEvent> call)
        {
            _call = call;
            _waiting = new ConcurrentDictionary<uint, TaskCompletionSource<Result>>();
            _writeLock = new SemaphoreSlim(1, 1);
            _stop = new CancellationTokenSource();
            _reader = Task.CompletedTask;
            CallTimeout = TimeSpan.FromSeconds(10);
            Kernel = null!;
            Names = null!;
        }

        /// <summary>
        /// Gets or sets how long a call waits for the node's answer. 10 seconds by default.
        /// </summary>
        public TimeSpan CallTimeout { get; set; }

        /// <summary>
        /// Gets the node's system number, from the answer to the Hello.
        /// </summary>
        public ushort SystemNumber { get; private set; }

        /// <summary>
        /// Gets the node's protocol revision, from the answer to the Hello; 0 from a node built before
        /// revisions existed.
        /// </summary>
        public uint NodeProtocolRevision { get; private set; }

        /// <summary>
        /// Gets what the node said it can do, from the answer to the Hello (for example "log").
        /// Empty from a node built before features existed.
        /// </summary>
        public System.Collections.Generic.IReadOnlyList<string> NodeFeatures { get; private set; } = Array.Empty<string>();

        /// <summary>
        /// Gets the lowest log level the node keeps from this program. Lines below it are not sent.
        /// </summary>
        public Microsoft.Extensions.Logging.LogLevel NodeLogMinimum { get; private set; } = Microsoft.Extensions.Logging.LogLevel.None;

        /// <summary>
        /// Gets the XMSG kernel calls, carried to the node.
        /// </summary>
        public RemoteXmsgKernel Kernel { get; private set; }

        /// <summary>
        /// Gets the XROUT name calls, carried to the node.
        /// </summary>
        public RemoteXroutNames Names { get; private set; }

        /// <summary>
        /// Gets a value indicating whether the stream has ended.
        /// </summary>
        public bool IsClosed
        {
            get { return _closed; }
        }

        /// <summary>
        /// Opens the stream on a channel, says Hello, and waits for the node's answer.
        /// </summary>
        /// <param name="channel">
        /// The gRPC channel to the node, for example <c>GrpcChannel.ForAddress("http://127.0.0.1:5011")</c>.
        /// </param>
        /// <param name="pluginName">
        /// What the plug-in calls itself, for the node's logs and admin page.
        /// </param>
        /// <param name="version">
        /// The plug-in's version.
        /// </param>
        /// <param name="token">
        /// The secret RetroServer is configured with, or null or empty when it runs without one.
        /// </param>
        /// <param name="cancellationToken">
        /// Ends the wait for the Hello's answer.
        /// </param>
        /// <returns>
        /// The open connection.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when an argument is null.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the node refuses the Hello or does not answer it.
        /// </exception>
        public static async Task<PluginConnection> ConnectAsync(
            ChannelBase channel,
            string pluginName,
            string version,
            string? token = null,
            CancellationToken cancellationToken = default)
        {
            if (channel == null)
            {
                throw new ArgumentNullException(nameof(channel));
            }

            if (pluginName == null)
            {
                throw new ArgumentNullException(nameof(pluginName));
            }

            if (version == null)
            {
                throw new ArgumentNullException(nameof(version));
            }

            XmsgPluginHost.XmsgPluginHostClient client = new XmsgPluginHost.XmsgPluginHostClient(channel);
            PluginConnection connection = new PluginConnection(client.Connect(cancellationToken: CancellationToken.None));
            connection._reader = connection.ReadAsync();

            Result hello = await connection.CallAsync(
                new PluginRequest { Hello = new Hello
                    {
                        PluginName = pluginName,
                        Version = version,
                        Token = token ?? string.Empty,
                        ProtocolRevision = (uint)ProtocolRevision.Current,
                    } },
                cancellationToken).ConfigureAwait(false);

            if (hello.Status != XmsgStatus.Completed.Value)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException("The node refused the Hello: " + hello.Status + " " + hello.Text);
            }

            connection.SystemNumber = (ushort)hello.Value;
            if (hello.Node != null)
            {
                connection.NodeProtocolRevision = hello.Node.ProtocolRevision;
                string[] features = new string[hello.Node.Features.Count];
                hello.Node.Features.CopyTo(features, 0);
                connection.NodeFeatures = features;

                // Only a node that says it keeps log lines is sent any.
                if (Array.IndexOf(features, "log") >= 0)
                {
                    connection.NodeLogMinimum = (Microsoft.Extensions.Logging.LogLevel)(int)hello.Node.LogMinimum;
                }
            }

            connection.Kernel = new RemoteXmsgKernel(connection);
            connection.Names = new RemoteXroutNames(connection, connection.Kernel);
            return connection;
        }

        /// <summary>
        /// Sends a request and blocks until its answer comes back or <see cref="CallTimeout"/> passes.
        /// </summary>
        /// <param name="request">
        /// The request; its id is set here.
        /// </param>
        /// <returns>
        /// The answer, or null when the stream has ended or the node did not answer in time.
        /// </returns>
        internal Result? Call(PluginRequest request)
        {
            try
            {
                using CancellationTokenSource timeout = new CancellationTokenSource(CallTimeout);
                return CallAsync(request, timeout.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (RpcException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            _closed = true;
            _stop.Cancel();
            try
            {
                await _call.RequestStream.CompleteAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Already completed.
            }
            catch (RpcException)
            {
                // The stream is already gone.
            }

            try
            {
                await _reader.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The reader stops by cancellation.
            }
            catch (RpcException)
            {
                // The stream ended with an error; it is closed either way.
            }

            _call.Dispose();
            _stop.Dispose();
        }

        /// <summary>
        /// Sends one log line to the node, where it is kept with the node's own log lines. Does not
        /// wait: the node never answers a log line.
        /// </summary>
        /// <param name="level">
        /// How serious it is. Lines below <see cref="NodeLogMinimum"/> are not sent.
        /// </param>
        /// <param name="category">
        /// The part of the program it came from, or empty.
        /// </param>
        /// <param name="text">
        /// The line.
        /// </param>
        /// <param name="exception">
        /// An error the line is about, or null.
        /// </param>
        /// <returns>
        /// True when the line was handed to the stream; false when it was below the node's level,
        /// the node keeps no log lines, or the connection has ended.
        /// </returns>
        public bool Log(Microsoft.Extensions.Logging.LogLevel level, string category, string text, Exception? exception = null)
        {
            if (_closed
                || NodeLogMinimum == Microsoft.Extensions.Logging.LogLevel.None
                || level == Microsoft.Extensions.Logging.LogLevel.None
                || level < NodeLogMinimum)
            {
                return false;
            }

            PluginRequest request = new PluginRequest
            {
                Log = new Contracts.Log
                {
                    Level = (Contracts.LogLevel)(int)level,
                    Category = category ?? string.Empty,
                    Text = text ?? string.Empty,
                    UnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Exception = exception?.ToString() ?? string.Empty,
                },
            };

            // Written in the background, in order with other writes (the write lock). A failed write
            // means the stream has ended, which the reader reports; a log line must not throw into
            // the code that logged it.
            _ = WriteQuietlyAsync(request);
            return true;
        }

        private async Task WriteQuietlyAsync(PluginRequest request)
        {
            try
            {
                await _writeLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    await _call.RequestStream.WriteAsync(request).ConfigureAwait(false);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Grpc.Core.RpcException || ex is ObjectDisposedException)
            {
                // The connection has ended; the line is lost, as the program's own console still has it.
            }
        }

        private async Task<Result> CallAsync(PluginRequest request, CancellationToken cancellationToken)
        {
            if (_closed)
            {
                throw new InvalidOperationException("The connection to the node has ended.");
            }

            uint id = (uint)Interlocked.Increment(ref _nextId);
            request.Id = id;
            TaskCompletionSource<Result> answer = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting[id] = answer;

            try
            {
                await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await _call.RequestStream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _writeLock.Release();
                }

                using (cancellationToken.Register(() => answer.TrySetCanceled(cancellationToken)))
                {
                    return await answer.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                _waiting.TryRemove(id, out _);
            }
        }

        private async Task ReadAsync()
        {
            try
            {
                while (await _call.ResponseStream.MoveNext(_stop.Token).ConfigureAwait(false))
                {
                    HostEvent next = _call.ResponseStream.Current;
                    if (next.BodyCase == HostEvent.BodyOneofCase.Result)
                    {
                        TaskCompletionSource<Result>? waiting;
                        if (_waiting.TryGetValue(next.Result.AnswersId, out waiting))
                        {
                            waiting.TrySetResult(next.Result);
                        }
                    }
                    else if (next.BodyCase == HostEvent.BodyOneofCase.Arrived)
                    {
                        // Kernel is set before any request but the Hello goes out, and nothing can
                        // arrive before a port is open.
                        Kernel.Arrive(next.Arrived);
                    }
                }
            }
            finally
            {
                _closed = true;
                foreach (System.Collections.Generic.KeyValuePair<uint, TaskCompletionSource<Result>> pending in _waiting)
                {
                    pending.Value.TrySetException(new InvalidOperationException("The connection to the node has ended."));
                }
            }
        }
    }
}
