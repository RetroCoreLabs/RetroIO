//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroIO.Protocols.Econet.FileServer;
using RetroIO.Protocols.Econet.FileServer.Configuration;
using RetroIO.Protocols.Econet.FileServer.Handlers;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.Protocol;
using RetroIO.Protocols.Econet.Protocol.Aun;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server.Transport;
using RetroIO.Security.Implementation.Auditing;
using RetroIO.Security.Implementation.RateLimiting;

namespace RetroIO.Protocols.Econet.Server;

/// <summary>
/// Background service that listens for AUN datagrams and dispatches file server traffic.
/// </summary>
public sealed class EconetFileServerService : BackgroundService
{
    private readonly ITransport[] _transports;
    private readonly FileServerCommandHandler _commandHandler;
    private readonly BulkTransferHandler _bulkHandler;
    private readonly FileServerSessionManager _sessions;
    private readonly ILogger<EconetFileServerService> _logger;
    private readonly InMemoryRateLimiter? _rateLimiter;
    private readonly SecurityEventEmitter? _securityEvents;
    private readonly IEconetStatisticsTracker? _statistics;
    private readonly AunOutboundRetryQueue _retryQueue = new();
    private readonly AunReplyRetransmitter _replyRetransmitter;

    // Per-server outbound AUN sequence for FS reply DATA. PiFS assigns a FRESH server sequence
    // (get_local_seq, += 4) to every reply — it does NOT echo the client's request seq. BeebEm ACKs
    // the reply by that server seq, which is what lets the retransmit loop terminate. See
    // AunOutboundSequence and Econet_Message_Protocol_Analysis.md §7.7.2 / §7.7.5.
    private readonly AunOutboundSequence _outboundSequence = new();

    public EconetFileServerService(
        ITransport[] transports,
        FileServerCommandHandler commandHandler,
        BulkTransferHandler bulkHandler,
        FileServerSessionManager sessions,
        ILogger<EconetFileServerService> logger,
        InMemoryRateLimiter? rateLimiter = null,
        SecurityEventEmitter? securityEvents = null,
        IEconetStatisticsTracker? statistics = null,
        FileServerConfiguration? configuration = null)
    {
        _transports = transports;
        _commandHandler = commandHandler;
        _bulkHandler = bulkHandler;
        _sessions = sessions;
        _logger = logger;
        _rateLimiter = rateLimiter;
        _securityEvents = securityEvents;
        _statistics = statistics;
        _replyRetransmitter = new AunReplyRetransmitter(
            configuration?.AunRetransmitIntervalMs ?? 1000,
            configuration?.AunMaxTransmits ?? 5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Econet file server starting with {Count} transport(s)...", _transports.Length);

        if (_transports.Length == 0)
        {
            _logger.LogError("No Econet transports configured.");
            return;
        }

        for (int i = 0; i < _transports.Length; i++)
        {
            ITransport transport = _transports[i];
            transport.OnMessageReceived += OnMessageReceived;
            _logger.LogInformation("  - {Type} transport registered", transport.TransportType);
        }

        try
        {
            Task[] transportTasks = new Task[_transports.Length];
            for (int i = 0; i < _transports.Length; i++)
                transportTasks[i] = _transports[i].RunAsync(stoppingToken);

            await Task.WhenAll(transportTasks);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // A normal stop. See the matching note in TnfsServerService: rethrowing would fault
            // the BackgroundService and take the whole host down, including the management API.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Econet file server stopped with an error");
            throw;
        }
        finally
        {
            _logger.LogInformation("Econet file server stopped");
        }
    }

    private async ValueTask OnMessageReceived(EconetMessage request)
    {
        try
        {
            EconetServerJsonLog.WriteInboundAun(request);

            if (ShouldAutoAck(request))
                await SendInboundAckAsync(request);

            string clientIp = request.ClientAddress?.ToString() ?? "UNKNOWN";
            FileServerSession? session = _sessions.GetSession(request.ClientNet, request.ClientStation);
            Guid userId = session?.RetroUserId ?? Guid.Empty;
            bool isAdmin = session?.IsAdmin ?? false;
            int bytesToTransfer = request.Payload.Length + 1024;

            if (_rateLimiter != null)
            {
                RateLimitResult rateLimitResult = await _rateLimiter.CheckRateLimitAsync(
                    userId, clientIp, bytesToTransfer, isAdmin, CancellationToken.None);

                if (!rateLimitResult.IsAllowed)
                {
                    _logger.LogWarning("Econet rate limit exceeded for {IP} (User: {UserId}): {Reason}",
                        clientIp, userId, rateLimitResult.DenialReason);

                    if (_securityEvents != null)
                    {
                        await _securityEvents.EmitAsync(new SecurityAuditEntry
                        {
                            EventType = SecurityEventType.RateLimitExceeded,
                            Severity = AuditSeverity.Warning,
                            PrincipalId = userId,
                            PrincipalName = session?.Username ?? "UNKNOWN",
                            IpAddress = clientIp,
                            Operation = "Econet AUN",
                            AccessGranted = false,
                            DenialReason = rateLimitResult.DenialReason,
                            Message = $"Econet rate limit exceeded: {rateLimitResult.DenialReason}"
                        });
                    }

                    return;
                }
            }

            EconetMessage? response = null;
            bool trackReplyForRetransmit = false;

            if (request.PacketType == AunPacketType.Ack)
            {
                _replyRetransmitter.Acknowledge(request.ClientNet, request.ClientStation, request.Sequence);
                response = _bulkHandler.HandleAck(request);
            }
            else if (request.PacketType == AunPacketType.Nak)
                _bulkHandler.HandleNak(request);
            else if (request.PacketType == AunPacketType.Immediate)
                response = HandleImmediate(request);
            else if (request.PacketType == AunPacketType.Unicast || request.PacketType == AunPacketType.Broadcast)
            {
                if (request.EconetPort == EconetPorts.FileServer)
                {
                    response = _commandHandler.Handle(request);
                    trackReplyForRetransmit = request.PacketType == AunPacketType.Unicast;
                }
                else
                    response = _bulkHandler.HandleData(request);
            }

            if (response?.Transport != null)
            {
                // FS reply DATA gets a FRESH server sequence (PiFS get_local_seq, += 4) — NOT the client's
                // request seq. Verified against econet-hpbridge.c: BeebEm ACKs a server-originated reply by
                // quoting the reply's server seq, and the bridge matches that ACK to stop retransmitting
                // (econet-hpbridge.c:3725, 5891). Echoing the request seq (the old behaviour) is why an
                // earlier trace saw "reply not acknowledged" — BeebEm did not treat the echoed-seq reply as
                // a new server transaction to ACK. Independent confirmation: RISC OS PRM Vol 5a Ch.122 and
                // BeebWiki "Econet over IP" (seq += 4 per transaction; ACK/NAK quote the seq). See
                // Econet_Message_Protocol_Analysis.md §7.7.2 / §7.7.5. Only the tracked FS command replies
                // (unicast on port 0x99) get a fresh seq; bulk transfers manage their own sequencing.
                if (trackReplyForRetransmit)
                    response = response.WithSequence(_outboundSequence.Next());

                try
                {
                    EconetServerJsonLog.WriteOutboundAun(response);
                    await response.Transport!.SendAsync(response);

                    // Retransmit the reply (≤ AunMaxTransmits, ~AunRetransmitIntervalMs apart) until the
                    // client ACKs it by the server seq, then Acknowledge() cancels it (see the Ack branch
                    // above). Mirrors the bridge's EB_CONFIG_AUN_RETX / EB_CONFIG_AUN_RETRIES loop.
                    if (trackReplyForRetransmit)
                        _replyRetransmitter.Track(response);
                }
                catch (Exception sendEx)
                {
                    _logger.LogWarning(sendEx, "Outbound AUN send failed; queued for retry");
                    _retryQueue.Enqueue(response);
                }
            }

            await _retryQueue.FlushAsync(CancellationToken.None);

            RecordStatistics(request, response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle Econet message from {Client}", request.ClientAddress);
        }
    }

    private void RecordStatistics(EconetMessage request, EconetMessage? response)
    {
        if (_statistics == null)
            return;

        if (request.PacketType == AunPacketType.Ack)
            return;

        int bytesTransferred = request.Payload.Length;
        if (response != null)
            bytesTransferred += response.Payload.Length;

        bool isFileOperation = IsFileOperation(request);
        _statistics.RecordRequest(bytesTransferred, isFileOperation);
    }

    private static bool IsFileOperation(EconetMessage request)
    {
        if (request.EconetPort != EconetPorts.FileServer)
            return true;

        if (request.Payload.Length < 2)
            return false;

        byte opcode = request.Payload[1];
        return opcode is FileServerOpcodes.Save
            or FileServerOpcodes.Load
            or FileServerOpcodes.LoadAs
            or FileServerOpcodes.Open
            or FileServerOpcodes.Delete
            or FileServerOpcodes.CreateDir
            or FileServerOpcodes.CreateFile;
    }

    private static bool ShouldAutoAck(EconetMessage request)
    {
        if (request.Transport == null)
            return false;

        if (!request.Transport.Capabilities.AutoAckInboundData)
            return false;

        return request.PacketType == AunPacketType.Unicast
            || request.PacketType == AunPacketType.Broadcast;
    }

    private static async ValueTask SendInboundAckAsync(EconetMessage request)
    {
        EconetMessage ack = new EconetMessage
        {
            PacketType = AunPacketType.Ack,
            EconetPort = request.EconetPort,
            Control = request.Control,
            Sequence = request.Sequence,
            Payload = Array.Empty<byte>(),
            ClientAddress = request.ClientAddress,
            ClientPort = request.ClientPort,
            ClientNet = request.ClientNet,
            ClientStation = request.ClientStation,
            Transport = request.Transport,
        };

        await request.Transport!.SendAsync(ack);
    }

    private static EconetMessage HandleImmediate(EconetMessage request)
    {
        return new EconetMessage
        {
            PacketType = AunPacketType.ImmediateReply,
            EconetPort = request.EconetPort,
            Control = request.Control,
            Sequence = request.Sequence,
            Payload = request.Payload,
            ClientAddress = request.ClientAddress,
            ClientPort = request.ClientPort,
            ClientNet = request.ClientNet,
            ClientStation = request.ClientStation,
            Transport = request.Transport,
        };
    }
}
