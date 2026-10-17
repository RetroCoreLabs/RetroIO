//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Management.Contracts;

/// <summary>
/// Everything the management server pushes to a connected client over SignalR.
/// </summary>
/// <remarks>
/// This interface is the whole point of the contract assembly. The server declares its hub as
/// <c>Hub&lt;IServerHubClient&gt;</c> and pushes through <c>IHubContext&lt;ServerHub,
/// IServerHubClient&gt;</c>, so the compiler checks every push against the same types the client
/// deserialises into. Before this existed the server sent anonymous objects and the client
/// deserialised them into private classes of its own -- renaming a property on either side
/// compiled cleanly and silently delivered empty values at runtime.
///
/// A method name here IS the wire name. Renaming one is a breaking protocol change.
/// </remarks>
public interface IServerHubClient
{
    /// <summary>Pushed on a timer with the server's run state.</summary>
    /// <param name="status">Current run state and uptime.</param>
    Task ServerStatus(ServerStatusUpdate status);

    /// <summary>Pushed when log entries have been added since the last push.</summary>
    /// <param name="entries">The entries added since the previous push, oldest first.</param>
    Task NewLogs(LogEntryDto[] entries);

    /// <summary>Pushed on a timer with TNFS traffic counters.</summary>
    /// <param name="stats">Current TNFS counters.</param>
    Task TnfsStats(TnfsStatsUpdate stats);
}

/// <summary>
/// Wire names of the <see cref="IServerHubClient"/> pushes, for callers that subscribe by string
/// (the SignalR client's <c>HubConnection.On</c> overloads take a name, not a method group).
/// </summary>
/// <remarks>
/// These MUST match the <see cref="IServerHubClient"/> method names exactly -- that is asserted by
/// a unit test, using reflection over the interface, so the two can never drift apart unnoticed.
/// </remarks>
public static class ServerHubMethods
{
    /// <summary>Wire name of <see cref="IServerHubClient.ServerStatus"/>.</summary>
    public const string ServerStatus = "ServerStatus";

    /// <summary>Wire name of <see cref="IServerHubClient.NewLogs"/>.</summary>
    public const string NewLogs = "NewLogs";

    /// <summary>Wire name of <see cref="IServerHubClient.TnfsStats"/>.</summary>
    public const string TnfsStats = "TnfsStats";

    /// <summary>Path the hub is mapped at, relative to the API base address.</summary>
    public const string HubPath = "/hubs/server";
}

/// <summary>Server run state, pushed over <see cref="IServerHubClient.ServerStatus"/>.</summary>
public class ServerStatusUpdate
{
    /// <summary>True while the protocol servers are running.</summary>
    public bool IsRunning { get; set; }

    /// <summary>Human-readable uptime, empty when the server is stopped.</summary>
    public string Uptime { get; set; } = string.Empty;
}

/// <summary>TNFS traffic counters, pushed over <see cref="IServerHubClient.TnfsStats"/>.</summary>
public class TnfsStatsUpdate
{
    /// <summary>Sessions currently mounted.</summary>
    public int ActiveConnections { get; set; }

    /// <summary>Requests served since the server started.</summary>
    public long TotalRequests { get; set; }

    /// <summary>Bytes read and written since the server started.</summary>
    public long TotalBytesTransferred { get; set; }
}
