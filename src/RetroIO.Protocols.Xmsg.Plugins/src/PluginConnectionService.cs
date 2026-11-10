//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Threading;
using System.Threading.Tasks;

using Grpc.Core;

using RetroIO.Protocols.Xmsg.Plugins.Contracts;

namespace RetroIO.Protocols.Xmsg.Plugins
{
    /// <summary>
    /// The gRPC service a plug-in dials: one <see cref="Connect"/> stream per plug-in.
    /// </summary>
    /// <remarks>
    /// This class only moves messages. Requests read off the stream go to
    /// <see cref="PluginServerHost.Submit"/>; answers and arrivals the host posts are written back.
    /// Nothing here touches the node, because these methods run on gRPC's threads and the node runs
    /// on its own pump thread. When the stream ends, for any reason, the session is detached, so the
    /// plug-in's ports and names are released.
    /// </remarks>
    public sealed class PluginConnectionService : XmsgPluginHost.XmsgPluginHostBase
    {
        private readonly PluginServerHost _host;

        /// <summary>
        /// Initialises the service on a host.
        /// </summary>
        /// <param name="host">
        /// The host the plug-ins' requests go to.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="host"/> is null.
        /// </exception>
        public PluginConnectionService(PluginServerHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <inheritdoc/>
        public override async Task Connect(
            IAsyncStreamReader<PluginRequest> requestStream,
            IServerStreamWriter<HostEvent> responseStream,
            ServerCallContext context)
        {
            PluginSession session = _host.Attach();
            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);

            Task writer = WriteAsync(session, responseStream, stop.Token);
            try
            {
                while (await requestStream.MoveNext(stop.Token).ConfigureAwait(false))
                {
                    _host.Submit(session, requestStream.Current);
                }
            }
            catch (OperationCanceledException)
            {
                // The plug-in went away or the node is stopping; either way the session ends below.
            }
            finally
            {
                _host.Detach(session);
                stop.Cancel();
                try
                {
                    await writer.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The writer stops by cancellation; that is its normal end.
                }
            }
        }

        private static async Task WriteAsync(
            PluginSession session,
            IServerStreamWriter<HostEvent> responseStream,
            CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await session.WaitForOutboundAsync(cancellationToken).ConfigureAwait(false);

                HostEvent? next;
                while (session.Outbound.TryDequeue(out next))
                {
                    await responseStream.WriteAsync(next, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }
}
