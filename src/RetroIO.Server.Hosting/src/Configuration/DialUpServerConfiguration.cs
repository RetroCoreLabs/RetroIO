//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Server.Hosting.Configuration;

/// <summary>
/// Settings of RetroServer's emulated dial-up ISP, as saved in dialup-config.json.
/// </summary>
/// <remarks>
/// <para>
/// An emulated machine dials in with its virtual modem (RetroCore's Hayes modem: <c>ATDT host:port</c>,
/// then <c>CONNECT</c> once the TCP connection is up). RetroServer listens on a TCP port, gives each
/// call a text login against its own users (only users with the dial-up right get in), then carries
/// the caller's IP packets over SLIP onto one shared subnet, with a NAT behind it to the internet
/// and to this computer.
/// </para>
/// <para>
/// OFF by default, as every protocol is (Ronny, 2026-11-08). The subnet is 10.42.0.0/24 (Ronny,
/// 2026-11-22), with the size a setting. On it: the router at .1, the NAT's gateway at .2 (which also
/// reaches this computer's own services), the NAT's name server at .3, callers from .15 up.
/// </para>
/// </remarks>
public sealed class DialUpServerConfiguration
{
    /// <summary>
    /// Gets or sets whether the dial-up service runs. The default is false.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the TCP port a caller's modem dials. The default is 6400.
    /// </summary>
    public int Port { get; set; } = 6400;

    /// <summary>
    /// Gets or sets the address to listen on. The default is 127.0.0.1, this computer only.
    /// </summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>
    /// Gets or sets the most calls at once. The default is 32.
    /// </summary>
    public int MaxCalls { get; set; } = 32;

    /// <summary>
    /// Gets or sets the callers' subnet, in dotted form. The default is 10.42.0.0.
    /// </summary>
    public string Network { get; set; } = "10.42.0.0";

    /// <summary>
    /// Gets or sets the subnet's prefix length. The default is 24.
    /// </summary>
    public int PrefixLength { get; set; } = 24;

    /// <summary>
    /// Gets or sets the greeting printed before the login prompt.
    /// </summary>
    public string Greeting { get; set; } = "RetroServer ISP";

    /// <summary>
    /// Creates the default configuration.
    /// </summary>
    /// <returns>
    /// A new configuration: switched off, port 6400, this computer only, 10.42.0.0/24.
    /// </returns>
    public static DialUpServerConfiguration GetDefault()
    {
        return new DialUpServerConfiguration();
    }
}
