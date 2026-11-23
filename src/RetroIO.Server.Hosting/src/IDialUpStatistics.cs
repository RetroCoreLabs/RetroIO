//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Server.Hosting;

/// <summary>
/// The dial-up ISP's counters, as the management API reads them. The server itself lives in
/// RetroIO.Server, which the API cannot reference (RetroIO.Server already references the API), so
/// the API reads it through this interface.
/// </summary>
public interface IDialUpStatistics
{
    /// <summary>Gets the port the server listens on, 0 before it has started.</summary>
    int ListeningPort { get; }

    /// <summary>Gets the calls answered since start, including ones later refused at the login.</summary>
    long CallsAnswered { get; }

    /// <summary>Gets the calls up now.</summary>
    int CallsActive { get; }

    /// <summary>Gets the calls turned away because every line was in use.</summary>
    long CallsRefusedFull { get; }

    /// <summary>Gets the refused logins: wrong name, wrong password, or no dial-up right.</summary>
    long LoginsRefused { get; }

    /// <summary>Gets the bytes read from callers' lines.</summary>
    long BytesFromCallers { get; }

    /// <summary>Gets the bytes written to callers' lines.</summary>
    long BytesToCallers { get; }
}
