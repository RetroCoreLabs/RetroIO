//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Server.Hosting.Configuration;

/// <summary>
/// Settings for RetroServer's own XMSG node, read from <c>xmsgnode-config.json</c> in the RetroFS data
/// folder.
/// </summary>
/// <remarks>
/// The node makes RetroServer an XMSG machine: it joins an Ethernet segment (normally RetroServer's own
/// hub) and opens a port that client and server programs (<c>retro-chat</c>, <c>retro-chat-server</c>)
/// connect to. Off by default.
/// </remarks>
public sealed class XmsgNodeServerConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the node runs. Default false.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets RetroServer's own XMSG system number. Zero (the default) means not set, and the
    /// node does not start. It must not be the number an <c>xmsgrunner</c> on this machine uses.
    /// </summary>
    public int SystemNumber { get; set; }

    /// <summary>
    /// Gets or sets the system number of the one machine the node talks to. Zero means not set.
    /// </summary>
    public int PeerSystemNumber { get; set; }

    /// <summary>
    /// Gets or sets the Ethernet segment to join. Default <c>tcp:127.0.0.1:5010</c>, RetroServer's own hub.
    /// </summary>
    public string Ethernet { get; set; } = "tcp:127.0.0.1:5010";

    /// <summary>
    /// Gets or sets the address programs connect to. Default <c>127.0.0.1</c>, this machine only: the
    /// connection has no login yet.
    /// </summary>
    public string ProgramBindAddress { get; set; } = "127.0.0.1";

    /// <summary>
    /// Gets or sets the port programs connect to. Default 5011.
    /// </summary>
    public int ProgramPort { get; set; } = 5011;

    /// <summary>
    /// Gets or sets the secret every connecting program must send (in its own config file), or null or empty for
    /// insecure mode, where any program that reaches the port may connect. When set it is always checked, also for a
    /// program on the same machine (Ronny, 2026-11-10).
    /// </summary>
    public string? ProgramToken { get; set; }

    /// <summary>
    /// Gets or sets the lowest level of log line kept from the connected programs, by the names RetroServer's own
    /// logging uses: Trace, Debug, Information, Warning, Error, Critical, or None for no program lines at all.
    /// Default Information. Each program is told it when it connects, so it does not send lines that would be dropped.
    /// </summary>
    public string ProgramLogMinimum { get; set; } = "Information";

    /// <summary>
    /// Gets or sets the middle line of the TAD login banner, or null for the built-in one.
    /// </summary>
    public string? Motd { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the node serves files as <c>*FA-SERVER</c>, the COSMOS file
    /// server SINTRAN's <c>@LIST-FILES</c>, <c>@COPY-FILE</c> and friends talk to. Default false.
    /// </summary>
    public bool FileServerEnabled { get; set; }

    /// <summary>
    /// Gets or sets the kind of storage the file server's files are in: <c>native</c> (a folder on this
    /// machine) or <c>tnfs</c> (a TNFS server). Null when <see cref="FileServerMountRef"/> is used.
    /// </summary>
    public string? FileServerProvider { get; set; }

    /// <summary>
    /// Gets or sets where the files are: a folder for <c>native</c>, a <c>tnfs://host:port/path</c> address
    /// for <c>tnfs</c>. Null when <see cref="FileServerMountRef"/> is used.
    /// </summary>
    public string? FileServerUri { get; set; }

    /// <summary>
    /// Gets or sets the volume id of an entry in <c>mounts.json</c> to serve instead of an inline
    /// provider and URI, or null.
    /// </summary>
    public string? FileServerMountRef { get; set; }

    /// <summary>
    /// Returns the defaults: node off.
    /// </summary>
    /// <returns>
    /// A new configuration.
    /// </returns>
    public static XmsgNodeServerConfiguration GetDefault()
    {
        return new XmsgNodeServerConfiguration();
    }
}
