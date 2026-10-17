//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Lapb;
using RetroIO.Link.Models;

namespace RetroIO.Link
{
    /// <summary>
    /// Manages all protocol links and provides routing capabilities.
    /// Each link has its own independent LAPB state machine (for LAPB-capable links).
    /// </summary>
    public class LinkManager : IDisposable
    {
        private readonly ILogger _logger;
        private readonly Dictionary<string, ILink> _links;
        private readonly Dictionary<string, ILink> _linksByMachineId;
        private readonly object _lock = new object();
        private bool _disposed = false;

        /// <summary>
        /// Create a new link manager
        /// </summary>
        public LinkManager(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _links = new Dictionary<string, ILink>(StringComparer.OrdinalIgnoreCase);
            _linksByMachineId = new Dictionary<string, ILink>(StringComparer.OrdinalIgnoreCase);

            _logger.LogInformation("LinkManager: Created");
        }

        #region Link Management

        /// <summary>
        /// Create and add a serial link
        /// </summary>
        public SerialLink CreateSerialLink(SerialLinkConfig config)
        {
            lock (_lock)
            {
                if (_links.ContainsKey(config.Name))
                {
                    throw new InvalidOperationException($"Link '{config.Name}' already exists");
                }

                var link = new SerialLink(_logger, config);
                AddLink(link, config.RemoteMachineId);

                _logger.LogInformation("LinkManager: Created serial link '{Name}'", config.Name);
                return link;
            }
        }

        /// <summary>
        /// Create and add a TCP-LAPB link
        /// </summary>
        public TcpLapbLink CreateTcpLapbLink(TcpLapbLinkConfig config)
        {
            lock (_lock)
            {
                if (_links.ContainsKey(config.Name))
                {
                    throw new InvalidOperationException($"Link '{config.Name}' already exists");
                }

                var link = new TcpLapbLink(_logger, config);
                AddLink(link, config.RemoteMachineId);

                _logger.LogInformation("LinkManager: Created TCP-LAPB link '{Name}'", config.Name);
                return link;
            }
        }

        /// <summary>
        /// Create and add an XOT link
        /// </summary>
        public XotLink CreateXotLink(XotLinkConfig config)
        {
            lock (_lock)
            {
                if (_links.ContainsKey(config.Name))
                {
                    throw new InvalidOperationException($"Link '{config.Name}' already exists");
                }

                var link = new XotLink(_logger, config);
                AddLink(link, config.RemoteMachineId);

                _logger.LogInformation("LinkManager: Created XOT link '{Name}'", config.Name);
                return link;
            }
        }

        /// <summary>
        /// Create and add a codec-based link (SLIP or HDLC framing) over the supplied transport.
        /// </summary>
        /// <param name="config">Framing and LAPB configuration for the link.</param>
        /// <param name="transport">The byte transport (serial or TCP) the link runs over.</param>
        /// <returns>The created link.</returns>
        public CodecLink CreateCodecLink(CodecLinkConfig config, ILinkTransport transport)
        {
            lock (_lock)
            {
                if (_links.ContainsKey(config.Name))
                {
                    throw new InvalidOperationException($"Link '{config.Name}' already exists");
                }

                var link = new CodecLink(_logger, config, transport);
                AddLink(link, config.RemoteMachineId);

                _logger.LogInformation("LinkManager: Created codec link '{Name}' ({Framing})", config.Name, config.Framing);
                return link;
            }
        }

        /// <summary>
        /// Create and add a test link for frame injection and simulation
        /// </summary>
        public TestLink CreateTestLink(string name, byte windowSize = 7, byte maxRetries = 10,
            int t1TimeoutMs = 3000, int t2TimeoutMs = 1500, int t3TimeoutMs = 0, bool isPrimary = true)
        {
            lock (_lock)
            {
                if (_links.ContainsKey(name))
                {
                    throw new InvalidOperationException($"Link '{name}' already exists");
                }

                var link = new TestLink(_logger, name, windowSize, maxRetries, t1TimeoutMs, t2TimeoutMs, t3TimeoutMs, isPrimary);
                AddLink(link, string.Empty);

                _logger.LogInformation("LinkManager: Created test link '{Name}'", name);
                return link;
            }
        }

        /// <summary>
        /// Get or create the default test link used for frame injection
        /// </summary>
        public TestLink GetOrCreateDefaultTestLink()
        {
            const string defaultName = "_default_test";

            lock (_lock)
            {
                if (_links.TryGetValue(defaultName, out var existingLink))
                {
                    return (TestLink)existingLink;
                }

                return CreateTestLink(defaultName);
            }
        }

        /// <summary>
        /// Add a link to the manager
        /// </summary>
        private void AddLink(ILink link, string remoteMachineId)
        {
            _links[link.Name] = link;

            if (!string.IsNullOrWhiteSpace(remoteMachineId))
            {
                _linksByMachineId[remoteMachineId] = link;
            }

            // Subscribe to events
            link.PayloadReceived += OnLinkPayloadReceived;
            link.StatusChanged += OnLinkStatusChanged;

            // Subscribe to LAPB events if capable
            if (link is ILapbCapable lapbLink)
            {
                lapbLink.LapbStateChanged += OnLinkLapbStateChanged;
            }
        }

        /// <summary>
        /// Remove and dispose a link
        /// </summary>
        public bool RemoveLink(string linkName)
        {
            lock (_lock)
            {
                if (!_links.TryGetValue(linkName, out var link))
                {
                    return false;
                }

                // Unsubscribe from events
                link.PayloadReceived -= OnLinkPayloadReceived;
                link.StatusChanged -= OnLinkStatusChanged;

                if (link is ILapbCapable lapbLink)
                {
                    lapbLink.LapbStateChanged -= OnLinkLapbStateChanged;
                }

                // Remove from machine ID index
                var machineId = link.RemoteMachineId;
                if (!string.IsNullOrWhiteSpace(machineId))
                {
                    _linksByMachineId.Remove(machineId);
                }

                _links.Remove(linkName);

                link.Stop();
                link.Dispose();

                _logger.LogInformation("LinkManager: Removed link '{Name}'", linkName);
                return true;
            }
        }

        /// <summary>
        /// Get a link by name
        /// </summary>
        public ILink? GetLink(string linkName)
        {
            lock (_lock)
            {
                _links.TryGetValue(linkName, out var link);
                return link;
            }
        }

        /// <summary>
        /// Get a link by remote machine ID
        /// </summary>
        public ILink? GetLinkByMachineId(string machineId)
        {
            lock (_lock)
            {
                _linksByMachineId.TryGetValue(machineId, out var link);
                return link;
            }
        }

        /// <summary>
        /// Get all links
        /// </summary>
        public ILink[] GetAllLinks()
        {
            lock (_lock)
            {
                var links = new ILink[_links.Count];
                int i = 0;
                foreach (var link in _links.Values)
                {
                    links[i++] = link;
                }
                return links;
            }
        }

        /// <summary>
        /// Get all active links
        /// </summary>
        public ILink[] GetActiveLinks()
        {
            lock (_lock)
            {
                var activeList = new List<ILink>(_links.Count);
                foreach (var link in _links.Values)
                {
                    if (link.Status == LinkStatus.Active)
                    {
                        activeList.Add(link);
                    }
                }
                return activeList.ToArray();
            }
        }

        /// <summary>
        /// Number of links
        /// </summary>
        public int LinkCount
        {
            get
            {
                lock (_lock)
                {
                    return _links.Count;
                }
            }
        }

        #endregion

        #region Link Operations

        /// <summary>
        /// Start all links
        /// </summary>
        public void StartAll()
        {
            lock (_lock)
            {
                foreach (var link in _links.Values)
                {
                    try
                    {
                        link.Start();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "LinkManager: Failed to start link '{Name}'", link.Name);
                    }
                }
            }
        }

        /// <summary>
        /// Stop all links
        /// </summary>
        public void StopAll()
        {
            lock (_lock)
            {
                foreach (var link in _links.Values)
                {
                    try
                    {
                        link.Stop();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "LinkManager: Failed to stop link '{Name}'", link.Name);
                    }
                }
            }
        }

        /// <summary>
        /// Start a specific link
        /// </summary>
        public bool StartLink(string linkName)
        {
            lock (_lock)
            {
                if (_links.TryGetValue(linkName, out var link))
                {
                    return link.Start();
                }
                return false;
            }
        }

        /// <summary>
        /// Stop a specific link
        /// </summary>
        public void StopLink(string linkName)
        {
            lock (_lock)
            {
                if (_links.TryGetValue(linkName, out var link))
                {
                    link.Stop();
                }
            }
        }

        #endregion

        #region Routing

        /// <summary>
        /// Send an opaque L3 payload (X.25 packet or SINTRAN frame) to a specific machine via the
        /// appropriate link
        /// </summary>
        public bool SendToMachine(string machineId, ReadOnlySpan<byte> payload)
        {
            var link = GetLinkByMachineId(machineId);
            if (link == null)
            {
                _logger.LogWarning("LinkManager: No link found for machine '{MachineId}'", machineId);
                return false;
            }

            return link.SendData(payload);
        }

        /// <summary>
        /// Send an opaque L3 payload (X.25 packet or SINTRAN frame) via a specific link
        /// </summary>
        public bool SendViaLink(string linkName, ReadOnlySpan<byte> payload)
        {
            var link = GetLink(linkName);
            if (link == null)
            {
                _logger.LogWarning("LinkManager: Link '{LinkName}' not found", linkName);
                return false;
            }

            return link.SendData(payload);
        }

        #endregion

        #region LAPB Operations

        /// <summary>
        /// Connect LAPB on a specific link
        /// </summary>
        public void ConnectLapb(string linkName)
        {
            var link = GetLink(linkName);
            if (link is ILapbCapable lapbLink)
            {
                lapbLink.Connect();
            }
            else
            {
                _logger.LogWarning("LinkManager: Link '{LinkName}' does not support LAPB", linkName);
            }
        }

        /// <summary>
        /// Disconnect LAPB on a specific link
        /// </summary>
        public void DisconnectLapb(string linkName)
        {
            var link = GetLink(linkName);
            if (link is ILapbCapable lapbLink)
            {
                lapbLink.Disconnect();
            }
        }

        /// <summary>
        /// Get LAPB state for a link
        /// </summary>
        public LapbState? GetLapbState(string linkName)
        {
            var link = GetLink(linkName);
            if (link is ILapbCapable lapbLink)
            {
                return lapbLink.LapbState;
            }
            return null;
        }

        /// <summary>
        /// Get LAPB statistics for a link
        /// </summary>
        public LapbStatistics? GetLapbStatistics(string linkName)
        {
            var link = GetLink(linkName);
            if (link is ILapbCapable lapbLink)
            {
                return lapbLink.GetLapbStatistics();
            }
            return null;
        }

        #endregion

        #region Statistics

        /// <summary>
        /// Get combined statistics for all links
        /// </summary>
        public LinkManagerStatistics GetStatistics()
        {
            var stats = new LinkManagerStatistics();

            lock (_lock)
            {
                stats.TotalLinks = _links.Count;

                foreach (var link in _links.Values)
                {
                    switch (link.Status)
                    {
                        case LinkStatus.Active:
                            stats.ActiveLinks++;
                            break;
                        case LinkStatus.Error:
                            stats.ErrorLinks++;
                            break;
                        case LinkStatus.Stopped:
                            stats.StoppedLinks++;
                            break;
                    }

                    var linkStats = link.GetStatistics();
                    stats.TotalBytesTransmitted += linkStats.BytesTransmitted;
                    stats.TotalBytesReceived += linkStats.BytesReceived;
                    stats.TotalFramesTransmitted += linkStats.FramesTransmitted;
                    stats.TotalFramesReceived += linkStats.FramesReceived;
                    stats.TotalErrors += linkStats.Errors;
                }
            }

            return stats;
        }

        #endregion

        #region Events

        /// <summary>
        /// Raised when any link receives an opaque payload (protocol classification happens above).
        /// </summary>
        public event LinkPayloadReceived? PayloadReceived;

        /// <summary>
        /// Raised when any link's status changes (forwarded with the originating link as sender).
        /// </summary>
        public event LinkStatusChanged? LinkStatusChanged;

        /// <summary>
        /// Raised when any LAPB-capable link's LAPB state changes
        /// </summary>
        public event EventHandler<LapbStateChangedEventArgs>? LapbStateChanged;

        private void OnLinkPayloadReceived(ILink link, byte[] payload, int length)
        {
            PayloadReceived?.Invoke(link, payload, length);
        }

        private void OnLinkStatusChanged(ILink link, LinkStatus oldStatus, LinkStatus newStatus, string reason)
        {
            LinkStatusChanged?.Invoke(link, oldStatus, newStatus, reason);
        }

        private void OnLinkLapbStateChanged(object? sender, LapbStateChangedEventArgs e)
        {
            LapbStateChanged?.Invoke(this, e);
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            lock (_lock)
            {
                foreach (var link in _links.Values)
                {
                    try
                    {
                        link.Stop();
                        link.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "LinkManager: Error disposing link '{Name}'", link.Name);
                    }
                }

                _links.Clear();
                _linksByMachineId.Clear();
            }

            _logger.LogInformation("LinkManager: Disposed");
        }

        #endregion
    }

    /// <summary>
    /// Combined statistics for all links
    /// </summary>
    public class LinkManagerStatistics
    {
        public int TotalLinks { get; set; }
        public int ActiveLinks { get; set; }
        public int ErrorLinks { get; set; }
        public int StoppedLinks { get; set; }

        public long TotalBytesTransmitted { get; set; }
        public long TotalBytesReceived { get; set; }
        public long TotalFramesTransmitted { get; set; }
        public long TotalFramesReceived { get; set; }
        public long TotalErrors { get; set; }
    }
}
