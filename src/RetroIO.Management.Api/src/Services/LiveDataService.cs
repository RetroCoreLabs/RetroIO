//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using Microsoft.AspNetCore.SignalR;
using RetroIO.Management.Api.Hubs;
using RetroIO.Management.Contracts;

namespace RetroIO.Management.Api.Services;

/// <summary>
/// Background service that pushes live data to connected clients
/// </summary>
public class LiveDataService : BackgroundService
{
    private readonly IHubContext<ServerHub, IServerHubClient> _hubContext;
    private readonly InMemoryLogProvider _logProvider;
    private readonly IServerManager _serverManager;
    private readonly ILogger<LiveDataService> _logger;
    private int _lastLogIndex = 0;

    public LiveDataService(
        IHubContext<ServerHub, IServerHubClient> hubContext,
        InMemoryLogProvider logProvider,
        IServerManager serverManager,
        ILogger<LiveDataService> logger)
    {
        _hubContext = hubContext;
        _logProvider = logProvider;
        _serverManager = serverManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Live data service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PushServerStatusAsync();
                await PushNewLogsAsync();
                await PushTnfsStatsAsync();

                await Task.Delay(1000, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error pushing live data");
            }
        }

        _logger.LogInformation("Live data service stopped");
    }

    // Every push below goes through the typed IServerHubClient, so the compiler checks the
    // payload against what the clients deserialise. These used to be anonymous objects sent by
    // method-name string: the Category and Exception fields of a log entry were quietly dropped
    // on the way out, and nothing would have caught a renamed property on either side.

    private async Task PushServerStatusAsync()
    {
        ServerStatusResponse statusResponse = _serverManager.GetStatus();

        ServerStatusUpdate status = new ServerStatusUpdate
        {
            IsRunning = statusResponse.IsRunning,
            Uptime = statusResponse.Uptime
        };

        await _hubContext.Clients.All.ServerStatus(status);
    }

    private async Task PushNewLogsAsync()
    {
        var logs = _logProvider.GetLogEntries();
        if (logs.Count <= _lastLogIndex) return;

        int newCount = logs.Count - _lastLogIndex;
        LogEntryDto[] newLogs = new LogEntryDto[newCount];

        for (int i = 0; i < newCount; i++)
        {
            var entry = logs[_lastLogIndex + i];
            newLogs[i] = new LogEntryDto
            {
                Timestamp = entry.Timestamp,
                Level = entry.Level,
                Category = entry.Category,
                Message = entry.Message,
                Exception = entry.Exception
            };
        }

        _lastLogIndex = logs.Count;

        await _hubContext.Clients.All.NewLogs(newLogs);
    }

    private async Task PushTnfsStatsAsync()
    {
        // Counters are not wired to the TNFS server yet; the shape is what the UI binds to.
        TnfsStatsUpdate stats = new TnfsStatsUpdate
        {
            ActiveConnections = 0,
            TotalRequests = 0L,
            TotalBytesTransferred = 0L
        };

        await _hubContext.Clients.All.TnfsStats(stats);
    }
}
