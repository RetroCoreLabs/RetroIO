//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroIO.Protocols.Tnfs.Protocol;
using RetroIO.Protocols.Tnfs.Server.Handlers;
using RetroIO.Protocols.Tnfs.Server.Transport;
using RetroIO.Security.Implementation.Auditing;
using RetroIO.Security.Implementation.RateLimiting;

namespace RetroIO.Protocols.Tnfs.Server;

/// <summary>
/// TNFS protocol server background service.
/// Coordinates multiple transports (UDP, TCP, etc.) and TNFS message handling.
/// </summary>
public sealed class TnfsServerService : BackgroundService
{
    private readonly IEnumerable<ITransport> _transports;
    private readonly SessionCommandHandler _sessionHandler;
    private readonly DirectoryCommandHandler _directoryHandler;
    private readonly FileCommandHandler _fileHandler;
    private readonly ILogger<TnfsServerService> _logger;
    private readonly SessionManager _sessionManager;
    private readonly InMemoryRateLimiter? _rateLimiter;
    private readonly SecurityEventEmitter? _securityEvents;
    private readonly TnfsServerStatistics? _statistics;

    /// <summary>
    /// Creates the service.
    /// </summary>
    /// <param name="transports">
    /// The transports to run.
    /// </param>
    /// <param name="sessionHandler">
    /// The handler for session commands.
    /// </param>
    /// <param name="directoryHandler">
    /// The handler for directory commands.
    /// </param>
    /// <param name="fileHandler">
    /// The handler for file commands.
    /// </param>
    /// <param name="sessionManager">
    /// The session manager.
    /// </param>
    /// <param name="logger">
    /// The logger to write to.
    /// </param>
    /// <param name="rateLimiter">
    /// The rate limiter, or null for none.
    /// </param>
    /// <param name="securityEvents">
    /// The emitter for security events, or null for none.
    /// </param>
    /// <param name="statistics">
    /// The statistics collector, or null for none.
    /// </param>
    public TnfsServerService(
        IEnumerable<ITransport> transports,
        SessionCommandHandler sessionHandler,
        DirectoryCommandHandler directoryHandler,
        FileCommandHandler fileHandler,
        SessionManager sessionManager,
        ILogger<TnfsServerService> logger,
        InMemoryRateLimiter? rateLimiter = null,
        SecurityEventEmitter? securityEvents = null,
        TnfsServerStatistics? statistics = null)
    {
        _transports = transports;
        _sessionHandler = sessionHandler;
        _directoryHandler = directoryHandler;
        _fileHandler = fileHandler;
        _sessionManager = sessionManager;
        _logger = logger;
        _rateLimiter = rateLimiter;
        _securityEvents = securityEvents;
        _statistics = statistics;
    }

    /// <summary>
    /// Hooks the message handler to every transport and runs all transports at the same time.
    /// </summary>
    /// <param name="stoppingToken">
    /// Signals that the service is stopping.
    /// </param>
    /// <returns>
    /// A task that completes when all transports have ended. It completes at once, after logging an error, when there are no transports.
    /// </returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var transportList = _transports.ToList();
        _logger.LogInformation("TNFS Server starting with {Count} transport(s)...", transportList.Count);

        if (transportList.Count == 0)
        {
            _logger.LogError("No transports configured! Server cannot start.");
            return;
        }

        // Hook up message handler to ALL transports
        foreach (var transport in transportList)
        {
            transport.OnMessageReceived += HandleMessageAsync;
            _logger.LogInformation("  - {Type} transport registered", transport.TransportType);
        }

        // Run ALL transports concurrently
        var transportTasks = transportList.Select(t => t.RunAsync(stoppingToken));

        try
        {
            await Task.WhenAll(transportTasks);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // A normal stop, not a failure. It must NOT be rethrown: a BackgroundService whose
            // ExecuteAsync faults takes the whole host down with it (the default
            // BackgroundServiceExceptionBehavior is StopHost). That used to be invisible because
            // cancellation only ever happened while the process was shutting down anyway. Now that
            // the management API can stop the protocol servers on their own, rethrowing here would
            // kill the process -- and with it the API you would use to start them again.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TNFS Server error");
            throw;
        }
        finally
        {
            _logger.LogInformation("TNFS Server stopped");
        }
    }

    private async ValueTask HandleMessageAsync(TnfsMessage request)
    {
        TnfsMessage? response = null;

        try
        {
            // Get session info for rate limiting
            var session = _sessionManager.FindSession(request.Header.SessionId);
            Guid userId = session?.UserId ?? Guid.Empty;
            bool isAdmin = session?.IsAuthenticated == true && session.IsAdmin;
            string clientIP = request.ClientIP?.ToString() ?? "UNKNOWN";

            // Estimate bytes to transfer (request size + estimated response size)
            int bytesToTransfer = request.Data.Length + 1024; // Conservative estimate

            // CHECK RATE LIMIT (if enabled)
            if (_rateLimiter != null)
            {
                var rateLimitResult = await _rateLimiter.CheckRateLimitAsync(
                    userId, clientIP, bytesToTransfer, isAdmin, CancellationToken.None);

                if (!rateLimitResult.IsAllowed)
                {
                    _logger.LogWarning("Rate limit exceeded for {IP} (User: {UserId}): {Reason}",
                        clientIP, userId, rateLimitResult.DenialReason);

                    // Emit security event
                    if (_securityEvents != null)
                    {
                        await _securityEvents.EmitAsync(new SecurityAuditEntry
                        {
                            EventType = SecurityEventType.RateLimitExceeded,
                            Severity = AuditSeverity.Warning,
                            PrincipalId = userId,
                            PrincipalName = session?.Username ?? "UNKNOWN",
                            IpAddress = clientIP,
                            SessionId = request.Header.SessionId.ToString("X4"),
                            Operation = $"TNFS Command 0x{request.Header.Command:X2}",
                            AccessGranted = false,
                            DenialReason = rateLimitResult.DenialReason,
                            Message = $"Rate limit exceeded: {rateLimitResult.DenialReason}"
                        });
                    }

                    // Return rate limit error (map to TNFS error code)
                    response = TnfsMessage.CreateResponse(request, TnfsReturnCode.EBUSY); // Closest TNFS error
                    if (request.Transport != null)
                    {
                        await request.Transport.SendAsync(response);
                    }
                    return;
                }
            }

            // Route by command
            response = request.Header.Command switch
            {
                // Session commands
                TnfsConstants.TNFS_MOUNT => _sessionHandler.HandleMount(request),
                TnfsConstants.TNFS_UMOUNT => _sessionHandler.HandleUnmount(request),

                // Directory commands
                TnfsConstants.TNFS_OPENDIR => _directoryHandler.HandleOpenDir(request),
                TnfsConstants.TNFS_READDIR => _directoryHandler.HandleReadDir(request),
                TnfsConstants.TNFS_READDIRX => _directoryHandler.HandleReadDirX(request),
                TnfsConstants.TNFS_CLOSEDIR => _directoryHandler.HandleCloseDir(request),
                TnfsConstants.TNFS_MKDIR => _directoryHandler.HandleMkDir(request),
                TnfsConstants.TNFS_RMDIR => _directoryHandler.HandleRmDir(request),

                // File commands
                TnfsConstants.TNFS_READ => _fileHandler.HandleRead(request),
                TnfsConstants.TNFS_WRITE => _fileHandler.HandleWrite(request),
                TnfsConstants.TNFS_CLOSE => _fileHandler.HandleClose(request),
                TnfsConstants.TNFS_STAT => _fileHandler.HandleStat(request),
                TnfsConstants.TNFS_LSEEK => _fileHandler.HandleLseek(request),
                TnfsConstants.TNFS_OPEN => _fileHandler.HandleOpen(request),
                TnfsConstants.TNFS_UNLINK => _fileHandler.HandleUnlink(request),
                TnfsConstants.TNFS_RENAME => _fileHandler.HandleRename(request),

                _ => TnfsMessage.CreateResponse(request, TnfsReturnCode.ENOSYS)
            };

            // Send response via the SAME transport that received the request
            if (request.Transport != null)
            {
                await request.Transport.SendAsync(response);
            }
            else
            {
                _logger.LogWarning("Request has no transport reference, cannot send response");
            }

            // Record real traffic statistics — previously the Management API's GetStatistics
            // endpoint always returned hardcoded zeros for these fields.
            if (_statistics != null)
            {
                _statistics.RecordRequest(request.Data.Length + response.Data.Length);

                if (request.Header.Command == TnfsConstants.TNFS_OPEN &&
                    response.Header.Status == (byte)TnfsReturnCode.SUCCESS)
                {
                    _statistics.RecordFileTransfer();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling message: {Message}", request);

            // Send error response via same transport
            response = TnfsMessage.CreateResponse(request, TnfsReturnCode.EIO);
            if (request.Transport != null)
            {
                await request.Transport.SendAsync(response);
            }
        }
        finally
        {
            request.Dispose();
            response?.Dispose();
        }
    }
}
