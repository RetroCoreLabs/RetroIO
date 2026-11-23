//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Management.Contracts;

/// <summary>
/// The dial-up ISP's state and counters.
/// </summary>
public class DialUpStatusResponse
{
    /// <summary>Gets or sets whether this server runs the dial-up ISP (it is switched on in its settings).</summary>
    public bool IsHosted { get; set; }

    /// <summary>Gets or sets the port the modem dials, 0 when not hosted.</summary>
    public int Port { get; set; }

    /// <summary>Gets or sets the calls answered since start.</summary>
    public long CallsAnswered { get; set; }

    /// <summary>Gets or sets the calls up now.</summary>
    public int CallsActive { get; set; }

    /// <summary>Gets or sets the calls turned away because every line was in use.</summary>
    public long CallsRefusedFull { get; set; }

    /// <summary>Gets or sets the refused logins.</summary>
    public long LoginsRefused { get; set; }

    /// <summary>Gets or sets the bytes read from callers.</summary>
    public long BytesFromCallers { get; set; }

    /// <summary>Gets or sets the bytes written to callers.</summary>
    public long BytesToCallers { get; set; }
}
