//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards
//

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetroIO.Management.Contracts;
using RetroIO.Protocols.Econet.Server;
using RetroIO.Protocols.Tnfs.Server;

namespace RetroIO.Management.Api.Services;

/// <summary>
/// Starts and stops the protocol servers running in this process.
/// </summary>
/// <remarks>
/// This used to set a boolean and nothing else, so <c>POST /api/v1/server/start</c> reported
/// success while starting nothing and <c>GetStatus</c> reported uptime for a server that had
/// never run. It could not do better: the management API was its own process and had no protocol
/// servers to drive.
///
/// Now that the API is hosted alongside them, it drives the real <see cref="TnfsServerService"/>
/// and <see cref="EconetFileServerService"/> background services. Stopping closes their listening
/// sockets; the host process itself keeps running so the API stays reachable to start them again.
///
/// The host starts these services at boot, so this reports running from construction rather than
/// waiting for someone to call <see cref="StartAsync"/>.
/// </remarks>
public class ServerManager : IServerManager
{
    private readonly ILogger<ServerManager> _logger;
    private readonly IServiceProvider _services;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

    private BackgroundService[]? _protocolServices;
    private DateTime? _startedAt;
    private bool _isRunning;

    /// <summary>
    /// Creates the manager.
    /// </summary>
    /// <param name="logger">Log sink.</param>
    /// <param name="services">
    /// Container, used to find the protocol services on first use.
    /// </param>
    /// <remarks>
    /// Takes the container rather than IEnumerable&lt;IHostedService&gt; on purpose. Injecting the
    /// hosted services directly is a circular dependency: the host resolves
    /// IEnumerable&lt;IHostedService&gt; to start everything, that includes LiveDataService, and
    /// LiveDataService needs IServerManager -- which would need the same enumerable. The process
    /// refuses to start with "A circular dependency was detected". Resolving lazily, after the
    /// graph is built, breaks the cycle.
    ///
    /// Assumes the host started the protocol services at boot, so it reports running from the
    /// first status call rather than waiting for StartAsync.
    /// </remarks>
    public ServerManager(ILogger<ServerManager> logger, IServiceProvider services)
    {
        _logger = logger;
        _services = services;
        _isRunning = true;
        _startedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Finds the protocol background services, once.
    /// </summary>
    /// <returns>
    /// The TNFS and Econet services hosted in this process. Empty when the API is hosted on its
    /// own, in which case start and stop have nothing to drive and say so.
    /// </returns>
    /// <remarks>
    /// Only the protocol servers are picked out. The management API's own background pusher must
    /// keep running, or stopping the protocols would also close the channel used to start them
    /// again.
    /// </remarks>
    private BackgroundService[] GetProtocolServices()
    {
        if (_protocolServices != null)
        {
            return _protocolServices;
        }

        List<BackgroundService> found = new List<BackgroundService>();

        foreach (IHostedService service in _services.GetServices<IHostedService>())
        {
            if (service is TnfsServerService || service is EconetFileServerService)
            {
                found.Add((BackgroundService)service);
            }
        }

        _protocolServices = found.ToArray();

        _logger.LogInformation(
            "ServerManager is driving {Count} protocol service(s)", _protocolServices.Length);

        return _protocolServices;
    }

    /// <inheritdoc />
    public ServerStatusResponse GetStatus()
    {
        TimeSpan uptime = _startedAt.HasValue
            ? DateTime.UtcNow - _startedAt.Value
            : TimeSpan.Zero;

        return new ServerStatusResponse
        {
            IsRunning = _isRunning,
            Uptime = FormatUptime(uptime),
            Version = "1.0.0",
            StartedAt = _startedAt
        };
    }

    /// <inheritdoc />
    public async Task<ServerActionResponse> StartAsync()
    {
        await _gate.WaitAsync();

        try
        {
            if (_isRunning)
            {
                return Failed("Server is already running");
            }

            BackgroundService[] protocols = GetProtocolServices();

            if (protocols.Length == 0)
            {
                return Failed("No protocol servers are hosted in this process");
            }

            for (int i = 0; i < protocols.Length; i++)
            {
                await protocols[i].StartAsync(CancellationToken.None);
            }

            _isRunning = true;
            _startedAt = DateTime.UtcNow;

            _logger.LogInformation("Protocol servers started");
            return Succeeded("Server started successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start the protocol servers");
            return Failed($"Failed to start server: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ServerActionResponse> StopAsync()
    {
        await _gate.WaitAsync();

        try
        {
            if (!_isRunning)
            {
                return Failed("Server is not running");
            }

            BackgroundService[] protocols = GetProtocolServices();

            for (int i = 0; i < protocols.Length; i++)
            {
                await protocols[i].StopAsync(CancellationToken.None);
            }

            _isRunning = false;
            _startedAt = null;

            _logger.LogInformation("Protocol servers stopped");
            return Succeeded("Server stopped successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop the protocol servers");
            return Failed($"Failed to stop server: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ServerActionResponse> RestartAsync()
    {
        ServerActionResponse stopResult = await StopAsync();

        if (!stopResult.Success)
        {
            return stopResult;
        }

        // Gives the operating system a moment to release the listening ports before rebinding.
        await Task.Delay(1000);

        return await StartAsync();
    }

    /// <summary>Builds a failed response.</summary>
    /// <param name="message">Reason shown to the caller.</param>
    /// <returns>The response.</returns>
    private static ServerActionResponse Failed(string message) =>
        new ServerActionResponse { Success = false, Message = message };

    /// <summary>Builds a successful response.</summary>
    /// <param name="message">Message shown to the caller.</param>
    /// <returns>The response.</returns>
    private static ServerActionResponse Succeeded(string message) =>
        new ServerActionResponse { Success = true, Message = message };

    /// <summary>
    /// Renders an uptime the way the UI displays it.
    /// </summary>
    /// <param name="uptime">Elapsed time since start.</param>
    /// <returns>Short human-readable duration.</returns>
    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1)
        {
            return $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m";
        }

        if (uptime.TotalHours >= 1)
        {
            return $"{uptime.Hours}h {uptime.Minutes}m {uptime.Seconds}s";
        }

        return $"{uptime.Minutes}m {uptime.Seconds}s";
    }
}
