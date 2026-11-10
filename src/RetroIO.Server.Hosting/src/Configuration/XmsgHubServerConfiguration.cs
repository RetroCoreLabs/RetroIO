//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Server.Hosting.Configuration;

/// <summary>
/// Settings of the COSMOS Ethernet hub (XMSG over Ethernet), as saved in xmsghub-config.json.
/// </summary>
/// <remarks>
/// <para>
/// The hub is a virtual Ethernet segment: ND machines join it over TCP (in RetroCore,
/// <c>device add ETH 0 --net=tcp:HOST:PORT</c>) and every frame one sends reaches all the others.
/// </para>
/// <para>
/// It is OFF by default: a new protocol starts switched off (RetroServer's rule for protocols from
/// 2026-11-08). The other defaults are those of the standalone xmsghub, except the address, which
/// is this computer only until it is changed.
/// </para>
/// </remarks>
public sealed class XmsgHubServerConfiguration
{
    /// <summary>
    /// Gets or sets whether the hub runs. The default is false.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the TCP port machines join on. The default is 5010.
    /// </summary>
    public int Port { get; set; } = 5010;

    /// <summary>
    /// Gets or sets the address to listen on. The default is 127.0.0.1, this computer only; 0.0.0.0
    /// lets machines on other computers join.
    /// </summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>
    /// Gets or sets another hub to join, as host:port, or null for none.
    /// </summary>
    public string? Uplink { get; set; }

    /// <summary>
    /// Gets or sets a pcap file that receives every frame, or null for none. Replaced each time the hub starts.
    /// </summary>
    public string? CaptureFile { get; set; }

    /// <summary>
    /// Gets or sets how many recent frames are kept for the frame list. The default is 4096.
    /// </summary>
    public int FrameBufferSize { get; set; } = 4096;

    /// <summary>
    /// Gets or sets how many recent log lines are kept for the log view. The default is 1000.
    /// </summary>
    public int LogBufferSize { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the seconds between status lines; one is written only when a number changed. The default is 10.
    /// </summary>
    public int StatusIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Creates the default configuration.
    /// </summary>
    /// <returns>
    /// A new configuration with the default values: switched off, port 5010, this computer only.
    /// </returns>
    public static XmsgHubServerConfiguration GetDefault()
    {
        return new XmsgHubServerConfiguration();
    }
}
