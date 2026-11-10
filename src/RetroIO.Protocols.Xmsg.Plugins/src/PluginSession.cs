//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Concurrent;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Api;
using RetroIO.Protocols.Xmsg.Plugins.Contracts;

namespace RetroIO.Protocols.Xmsg.Plugins
{
    /// <summary>
    /// One connected plug-in: what it holds on the node, and what is waiting to be sent back to it.
    /// </summary>
    /// <remarks>
    /// Everything but <see cref="Outbound"/> is touched only on the node's pump thread
    /// (<see cref="PluginServerHost.Pump"/>). <see cref="Outbound"/> is filled there and emptied by
    /// the thread that writes the plug-in's gRPC stream.
    /// </remarks>
    public sealed class PluginSession
    {
        internal PluginSession(int id)
        {
            Id = id;
            PluginName = string.Empty;
            Version = string.Empty;
            Ports = new Dictionary<uint, XmsgPortNumber>();
            Names = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            Outbound = new ConcurrentQueue<HostEvent>();
            _signal = new System.Threading.SemaphoreSlim(0);
        }

        private readonly System.Threading.SemaphoreSlim _signal;

        /// <summary>
        /// Gets the session's number, unique while the node runs.
        /// </summary>
        public int Id { get; }

        /// <summary>
        /// Gets what the plug-in called itself in its Hello.
        /// </summary>
        public string PluginName { get; internal set; }

        /// <summary>
        /// Gets the version the plug-in gave in its Hello.
        /// </summary>
        public string Version { get; internal set; }

        /// <summary>
        /// Gets the protocol revision the plug-in gave in its Hello; 0 from a program built before
        /// revisions existed.
        /// </summary>
        public uint ProtocolRevision { get; internal set; }

        /// <summary>
        /// Gets the number of log lines the plug-in sent that the node kept.
        /// </summary>
        public long LogLines { get; internal set; }

        /// <summary>
        /// Gets the number of messages handed to the plug-in so far.
        /// </summary>
        public long Arrivals { get; internal set; }

        /// <summary>
        /// Gets the answers and arrivals waiting to be written to the plug-in's stream.
        /// </summary>
        public ConcurrentQueue<HostEvent> Outbound { get; }

        /// <summary>
        /// Gets the names the plug-in holds now. Pump thread only.
        /// </summary>
        public IReadOnlyCollection<string> HeldNames
        {
            get { return Names; }
        }

        /// <summary>
        /// Gets how many ports the plug-in has open now. Pump thread only.
        /// </summary>
        public int PortCount
        {
            get { return Ports.Count; }
        }

        /// <summary>
        /// Waits until something is in <see cref="Outbound"/>, or the wait is cancelled.
        /// </summary>
        /// <param name="cancellationToken">
        /// Ends the wait.
        /// </param>
        /// <returns>
        /// A task that completes when there may be something to send.
        /// </returns>
        public System.Threading.Tasks.Task WaitForOutboundAsync(System.Threading.CancellationToken cancellationToken)
        {
            return _signal.WaitAsync(cancellationToken);
        }

        /// <summary>
        /// Queues an event for the plug-in and wakes the writer.
        /// </summary>
        /// <param name="hostEvent">
        /// The event.
        /// </param>
        internal void Post(HostEvent hostEvent)
        {
            Outbound.Enqueue(hostEvent);
            _signal.Release();
        }

        internal bool SaidHello { get; set; }

        internal bool Detached { get; set; }

        /// <summary>
        /// The plug-in's ports, by magic number.
        /// </summary>
        internal Dictionary<uint, XmsgPortNumber> Ports { get; }

        /// <summary>
        /// The names the plug-in claimed.
        /// </summary>
        internal HashSet<string> Names { get; }
    }
}
