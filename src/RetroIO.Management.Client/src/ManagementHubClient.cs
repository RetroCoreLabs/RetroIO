//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using RetroIO.Management.Contracts;

namespace RetroIO.Management.Client;

/// <summary>
/// Live push channel from the RetroFS management API, over SignalR.
/// </summary>
/// <remarks>
/// The push types and the wire names both come from the contract assembly
/// (<see cref="IServerHubClient"/> and <see cref="ServerHubMethods"/>), which is what keeps this
/// client and the server in step. Previously this class declared its own private copies of the
/// payload classes while the server pushed anonymous objects, so nothing checked that the two
/// agreed and a renamed property would have gone unnoticed until a field showed up empty.
///
/// This was RetroAdmin.Desktop.Services.SignalRService and lived inside the Avalonia app; it has no UI
/// dependency, so it belongs here where any client can use it.
/// </remarks>
public sealed class ManagementHubClient : IAsyncDisposable
{
    private readonly string _hubUrl;
    private readonly bool _allowUntrustedCertificates;
    private HubConnection? _connection;

    /// <summary>
    /// Supplies the bearer token for the handshake, or null when the hub needs no token.
    /// </summary>
    /// <remarks>
    /// A delegate rather than a stored string so a reconnect picks up a token that was renewed
    /// since the first connect. SignalR calls it on every connection attempt.
    /// </remarks>
    private readonly Func<string?>? _accessTokenProvider;

    /// <summary>
    /// Raised when the server pushes a status update.
    /// </summary>
    public event Action<ServerStatusUpdate>? ServerStatusUpdated;

    /// <summary>
    /// Raised when the server pushes log entries added since the last push.
    /// </summary>
    public event Action<LogEntryDto[]>? LogsAdded;

    /// <summary>
    /// Raised when the server pushes TNFS counters.
    /// </summary>
    public event Action<TnfsStatsUpdate>? TnfsStatsUpdated;

    /// <summary>
    /// Raised on reconnect attempts, reconnection and close, for the host to log.
    /// </summary>
    /// <remarks>
    /// Replaces the Console.WriteLine calls this class used to make, which went nowhere in a
    /// windowed application.
    /// </remarks>
    public event Action<string, Exception?>? ConnectionStateChanged;

    /// <summary>
    /// True while the hub connection is up.
    /// </summary>
    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    /// <summary>
    /// Creates a client for the hub hosted by the management API at <paramref name="apiBaseUrl"/>.
    /// </summary>
    /// <param name="apiBaseUrl">
    /// Root address of the management API.
    /// </param>
    /// <param name="allowUntrustedCertificates">
    /// When true, ANY server certificate is accepted. For a development server with a self-signed
    /// certificate on localhost only; defaults to false. The previous version had it always on.
    /// </param>
    /// <param name="accessTokenProvider">
    /// Returns the bearer token to present on the handshake. The hub requires the same
    /// authorization policy as the REST endpoints, so without a token the connection is refused.
    /// Pass the client's AccessToken property as a delegate, not its value, so reconnects use the
    /// current token.
    /// </param>
    public ManagementHubClient(string apiBaseUrl = "https://localhost:5001",
                               bool allowUntrustedCertificates = false,
                               Func<string?>? accessTokenProvider = null)
    {
        if (string.IsNullOrWhiteSpace(apiBaseUrl))
            throw new ArgumentException("Base URL is required.", nameof(apiBaseUrl));

        _hubUrl = $"{apiBaseUrl}{ServerHubMethods.HubPath}";
        _allowUntrustedCertificates = allowUntrustedCertificates;
        _accessTokenProvider = accessTokenProvider;
    }

    /// <summary>
    /// Opens the connection, or does nothing if it is already open.
    /// </summary>
    /// <returns>
    /// A task that completes once the connection is up.
    /// </returns>
    public async Task ConnectAsync()
    {
        if (_connection != null && _connection.State == HubConnectionState.Connected)
            return;

        _connection = new HubConnectionBuilder()
            .WithUrl(_hubUrl, options =>
            {
                // SignalR appends this as the access_token query parameter, which the server
                // honours for the hub path only.
                if (_accessTokenProvider != null)
                {
                    options.AccessTokenProvider = () => Task.FromResult(_accessTokenProvider());
                }

                if (!_allowUntrustedCertificates) return;

                options.HttpMessageHandlerFactory = handler =>
                {
                    if (handler is HttpClientHandler clientHandler)
                    {
                        clientHandler.ServerCertificateCustomValidationCallback =
                            (sender, certificate, chain, errors) => true;
                    }

                    return handler;
                };
            })
            .WithAutomaticReconnect()
            .Build();

        // Wire names come from the contract, not from string literals repeated on both sides.
        _connection.On<ServerStatusUpdate>(ServerHubMethods.ServerStatus,
            status => ServerStatusUpdated?.Invoke(status));

        _connection.On<LogEntryDto[]>(ServerHubMethods.NewLogs,
            entries => LogsAdded?.Invoke(entries));

        _connection.On<TnfsStatsUpdate>(ServerHubMethods.TnfsStats,
            stats => TnfsStatsUpdated?.Invoke(stats));

        _connection.Reconnecting += error =>
        {
            ConnectionStateChanged?.Invoke("reconnecting", error);
            return Task.CompletedTask;
        };

        _connection.Reconnected += connectionId =>
        {
            ConnectionStateChanged?.Invoke($"reconnected ({connectionId})", null);
            return Task.CompletedTask;
        };

        _connection.Closed += error =>
        {
            ConnectionStateChanged?.Invoke("closed", error);
            return Task.CompletedTask;
        };

        await _connection.StartAsync().ConfigureAwait(false);
        ConnectionStateChanged?.Invoke("connected", null);
    }

    /// <summary>
    /// Closes and disposes the connection.
    /// </summary>
    /// <returns>
    /// A task that completes once the connection is closed.
    /// </returns>
    public async Task DisconnectAsync()
    {
        if (_connection == null) return;

        await _connection.StopAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
        _connection = null;
    }

    /// <summary>
    /// Closes the connection.
    /// </summary>
    /// <returns>
    /// A task that completes once the connection is closed.
    /// </returns>
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
    }
}
