//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RetroIO.Protocols.Xmsg.Hub.Server
{
    /// <summary>
    /// Where the hosted hub is in its life.
    /// </summary>
    public enum XmsgHubState
    {
        /// <summary>
        /// Not running: never started, or stopped.
        /// </summary>
        Stopped = 0,

        /// <summary>
        /// Listening and repeating frames.
        /// </summary>
        Running = 1,

        /// <summary>
        /// Could not start; <see cref="XmsgHubService.FailureReason"/> says why.
        /// </summary>
        Failed = 2,
    }

    /// <summary>
    /// Runs the COSMOS Ethernet hub inside a host (RetroServer).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hub itself is <see cref="HubServer"/>, the same code the standalone xmsghub runs. This
    /// service adds what a long-running server needs: start and stop with the host, the hub's log
    /// lines kept for the admin view and passed to the host's logger, the buffer of recent frames
    /// for the frame list and pcap download, and a state that says when it could not start.
    /// </para>
    /// <para>
    /// A port that cannot be bound does NOT stop the host. The service logs the error, sets
    /// <see cref="State"/> to <see cref="XmsgHubState.Failed"/> with the reason, and ends; the
    /// other protocols keep running and the admin UI shows why the hub is down. (DriveWire throws
    /// instead, which stops the host; for the hub that would let one known port clash on this
    /// box - Armoury Crate taking 5010 - take TNFS and Econet down with it.)
    /// </para>
    /// <para>
    /// The frame and log buffers belong to the service, not to one run of the hub, so stopping
    /// and starting it from the management API keeps what was already seen.
    /// </para>
    /// </remarks>
    public sealed class XmsgHubService : BackgroundService
    {
        private readonly XmsgHubOptions _options;
        private readonly ILogger _logger;
        private TaskCompletionSource<IPEndPoint> _listeningNow =
            new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);

        private HubServer? _hub;

        /// <summary>
        /// Initialises the service.
        /// </summary>
        /// <param name="options">
        /// The hub settings.
        /// </param>
        /// <param name="loggerFactory">
        /// Where the hub's lines also go. Null discards them (they are still kept for the log view).
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="options"/> is null.
        /// </exception>
        public XmsgHubService(XmsgHubOptions options, ILoggerFactory? loggerFactory = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<XmsgHubService>();
            Frames = new HubFrameRing(Math.Max(1, options.FrameBufferSize));
            LogLines = new HubLogRing(Math.Max(1, options.LogBufferSize));
        }

        /// <summary>
        /// Gets the settings the service runs with.
        /// </summary>
        public XmsgHubOptions Options
        {
            get { return _options; }
        }

        /// <summary>
        /// Gets the recent frames the hub carried.
        /// </summary>
        public HubFrameRing Frames { get; }

        /// <summary>
        /// Gets the hub's recent log lines.
        /// </summary>
        public HubLogRing LogLines { get; }

        /// <summary>
        /// Gets where the hub is in its life.
        /// </summary>
        public XmsgHubState State { get; private set; }

        /// <summary>
        /// Gets why the hub could not start, or empty.
        /// </summary>
        public string FailureReason { get; private set; } = string.Empty;

        /// <summary>
        /// Gets the address and port the hub listens on, or null when it is not running.
        /// </summary>
        public IPEndPoint? LocalEndPoint { get; private set; }

        /// <summary>
        /// Gets when the current run started, or null when not running.
        /// </summary>
        public DateTime? StartedUtc { get; private set; }

        /// <summary>
        /// Gets the running hub, or null when not running.
        /// </summary>
        public HubServer? Hub
        {
            get { return _hub; }
        }

        /// <summary>
        /// Completes when the hub is listening.
        /// </summary>
        /// <param name="cancellationToken">
        /// Cancels the wait.
        /// </param>
        /// <returns>
        /// The address and port it listens on.
        /// </returns>
        /// <remarks>
        /// Faults with the bind error when the hub could not start.
        /// </remarks>
        public Task<IPEndPoint> WaitUntilListeningAsync(CancellationToken cancellationToken)
        {
            return _listeningNow.Task.WaitAsync(cancellationToken);
        }

        /// <summary>
        /// Gets a snapshot of the hub's members; empty when not running.
        /// </summary>
        /// <returns>
        /// One entry per member.
        /// </returns>
        public HubMemberInfo[] GetMembers()
        {
            HubServer? hub = _hub;
            return hub == null ? Array.Empty<HubMemberInfo>() : hub.GetMembers();
        }

        /// <inheritdoc/>
        public override Task StartAsync(CancellationToken cancellationToken)
        {
            // A second start (the management API stopping and starting the service) needs a fresh
            // "listening" signal; the old one has already fired.
            if (_listeningNow.Task.IsCompleted)
            {
                _listeningNow = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return base.StartAsync(cancellationToken);
        }

        /// <inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            HubServer hub;
            try
            {
                hub = CreateHub();
            }
            catch (Exception ex) when (ex is FormatException || ex is ArgumentException)
            {
                Fail($"the settings are wrong: {ex.Message}", ex);
                return;
            }

            hub.FrameLog = Frames;
            hub.Log += Record;

            if (!string.IsNullOrWhiteSpace(_options.CaptureFile))
            {
                // Open the capture BEFORE Start, so the very first frame of the run is in the
                // file. A capture that begins after the machines have said hello is missing the
                // part that sets up everything that follows.
                try
                {
                    hub.StartCapture(_options.CaptureFile);
                    Record($"capturing every frame to {_options.CaptureFile} (pcap, link type Ethernet)");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Fail($"cannot write the capture file {_options.CaptureFile}: {ex.Message}", ex);
                    return;
                }
            }

            try
            {
                hub.Start();
            }
            catch (SocketException ex)
            {
                hub.Stop();
                Fail(
                    $"cannot listen on {_options.BindAddress}:{_options.Port}: {ex.Message}. "
                    + "Another program may hold the port (after a Windows reboot ASUS Armoury Crate is known to take 5010), "
                    + "or a standalone xmsghub is still running.",
                    ex);
                return;
            }

            _hub = hub;
            LocalEndPoint = new IPEndPoint(ParseBindAddress(_options.BindAddress), hub.Port);
            StartedUtc = DateTime.UtcNow;
            State = XmsgHubState.Running;
            FailureReason = string.Empty;
            Record($"XMSG Ethernet hub running: {hub.Description}. Machines join with: device add ETH 0 --net=tcp:HOST:{hub.Port}");
            _listeningNow.TrySetResult(LocalEndPoint);

            try
            {
                await ReportStatusUntilStoppedAsync(hub, stoppingToken).ConfigureAwait(false);
            }
            finally
            {
                _hub = null;
                hub.Stop();
                Record(
                    $"stopped. in {hub.FramesIn}  forwarded {hub.FramesForwarded}  "
                    + $"dropped slow {hub.FramesDroppedSlow} / loop {hub.FramesDroppedLoop} / ttl {hub.FramesDroppedTtl}");
                hub.Log -= Record;
                LocalEndPoint = null;
                StartedUtc = null;
                State = XmsgHubState.Stopped;
            }
        }

        /// <summary>
        /// Parses a bind address; an empty one means every address.
        /// </summary>
        /// <param name="text">
        /// The address as text.
        /// </param>
        /// <returns>
        /// The address.
        /// </returns>
        /// <exception cref="FormatException">
        /// Thrown when the text is not an IP address.
        /// </exception>
        public static IPAddress ParseBindAddress(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return IPAddress.Any;
            }

            return IPAddress.Parse(text.Trim());
        }

        /// <summary>
        /// Builds the hub from the settings.
        /// </summary>
        /// <returns>
        /// The hub, not yet started.
        /// </returns>
        private HubServer CreateHub()
        {
            IPAddress address = ParseBindAddress(_options.BindAddress);

            string? uplinkHost = null;
            int uplinkPort = 0;
            if (!string.IsNullOrWhiteSpace(_options.Uplink))
            {
                string up = _options.Uplink.Trim();
                int colon = up.LastIndexOf(':');
                if (colon <= 0 || !int.TryParse(up.Substring(colon + 1), out uplinkPort) || uplinkPort <= 0 || uplinkPort > 65535)
                {
                    throw new FormatException($"Uplink must be host:port, not '{_options.Uplink}'.");
                }

                uplinkHost = up.Substring(0, colon);
            }

            return new HubServer(address, _options.Port, uplinkHost, uplinkPort);
        }

        /// <summary>
        /// Writes a status line every interval, but only when a number in it changed.
        /// </summary>
        /// <param name="hub">
        /// The running hub.
        /// </param>
        /// <param name="stoppingToken">
        /// Ends the loop.
        /// </param>
        /// <returns>
        /// Completes when stopped.
        /// </returns>
        private async Task ReportStatusUntilStoppedAsync(HubServer hub, CancellationToken stoppingToken)
        {
            TimeSpan interval = TimeSpan.FromSeconds(Math.Max(1, _options.StatusIntervalSeconds));
            string lastStatus = string.Empty;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                string status =
                    $"members {hub.MemberCount} (machines {hub.MachineMemberCount})   "
                    + $"in {hub.FramesIn}  fwd {hub.FramesForwarded}   "
                    + $"dropped slow {hub.FramesDroppedSlow} / loop {hub.FramesDroppedLoop} / ttl {hub.FramesDroppedTtl}";

                // ONLY LOG WHEN SOMETHING MOVED - the same rule as the standalone hub. An idle
                // segment writing the same line every ten seconds buries the lines that carry news.
                if (status != lastStatus)
                {
                    Record(status);
                    lastStatus = status;
                }
            }
        }

        /// <summary>
        /// Keeps one hub line for the log view and passes it to the host's logger.
        /// </summary>
        /// <param name="message">
        /// The line.
        /// </param>
        private void Record(string message)
        {
            LogLines.Add(DateTime.UtcNow, message);
            _logger.LogInformation("[hub] {Message}", message);
        }

        /// <summary>
        /// Marks the hub as failed to start and says why.
        /// </summary>
        /// <param name="reason">
        /// The reason, for the log view and the status.
        /// </param>
        /// <param name="ex">
        /// The exception behind it.
        /// </param>
        private void Fail(string reason, Exception ex)
        {
            State = XmsgHubState.Failed;
            FailureReason = reason;
            LogLines.Add(DateTime.UtcNow, "FAILED: " + reason);
            _logger.LogError(ex, "[hub] {Reason}", reason);
            _listeningNow.TrySetException(ex);
        }
    }
}
