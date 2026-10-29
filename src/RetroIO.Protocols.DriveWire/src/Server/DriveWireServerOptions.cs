//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.DriveWire.Server;

/// <summary>
/// Settings of a DriveWire server.
/// </summary>
/// <remarks>
/// <para>
/// The DriveWire specification describes a serial line, not TCP, so it names no port number. The TCP default here, 65504, is
/// the port the emulators' Becker ports connect to unless told otherwise: the XRoar manual, section 6.4 "Becker port",
/// gives -becker-ip 127.0.0.1 and -becker-port 65504 as the defaults (https://www.6809.org.uk/xroar/doc/xroar.shtml), and
/// the MAME issue tracker records 127.0.0.1:65504 as MAME's Becker port setting (https://mametesters.org/view.php?id=6686).
/// </para>
/// <para>
/// The server listens on the loopback address by default. DriveWire has no log-in: whoever reaches the port can read and
/// write every drive, so it is reachable from other machines only when <see cref="BindAddress"/> is set to say so.
/// </para>
/// <para>
/// Serial line rates are not settings of this TCP server. For a later serial transport, the specification ("Low Level
/// Protocol") gives 8-N-1 at 57,600 bit/s for a Color Computer 2 and 115,200 or 230,400 bit/s for a Color Computer 3.
/// </para>
/// </remarks>
public sealed class DriveWireServerOptions
{
    /// <summary>
    /// The default TCP port, 65504.
    /// </summary>
    public const int DefaultPort = 65504;

    /// <summary>
    /// The default time limit for each byte inside a transaction, 250 ms.
    /// </summary>
    /// <remarks>
    /// "DriveWire Protocol Version 4.0.0", "Low Level Protocol": "both the CoCo and the Server shall send their respective
    /// responses within 250 milliseconds (1/4 of a second) of receiving the last packet from its peer, or else each shall
    /// presume a timeout condition and abort the transaction"; and "The Write Transaction": "The Server shall implement a
    /// timeout of 250 milliseconds (1/4 second), in the event that the CoCo stops sending data."
    /// </remarks>
    public const int DefaultTransactionTimeoutMilliseconds = 250;

    /// <summary>
    /// Gets or sets the TCP port. The default is 65504.
    /// </summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>
    /// Gets or sets the address to listen on. The default is 127.0.0.1, this machine only; "0.0.0.0" listens on every
    /// network.
    /// </summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>
    /// Gets or sets how long the server waits for each further byte once a transaction has started. The default is 250 ms.
    /// </summary>
    /// <remarks>
    /// When it runs out, the transaction is given up, the bytes received for it are dropped, and the next byte is read as a
    /// new operation code. The wait for the first byte of a transaction has no limit.
    /// </remarks>
    public int TransactionTimeoutMilliseconds { get; set; } = DefaultTransactionTimeoutMilliseconds;

    /// <summary>
    /// Gets or sets the drives to mount when the server starts. The default is none.
    /// </summary>
    public List<DriveWireDriveOptions> Drives { get; set; } = new List<DriveWireDriveOptions>();

    /// <summary>
    /// Gets or sets the folder whose files OP_NAMEOBJ_MOUNT may mount by name, or null (the default) for no named objects,
    /// in which case every named object request is answered 0.
    /// </summary>
    public string? NamedObjectDirectory { get; set; }

    /// <summary>
    /// Gets or sets the folder each print job (the bytes of OP_PRINT up to OP_PRINTFLUSH) is written to as a file, or null
    /// (the default) to only raise the print event.
    /// </summary>
    public string? PrintDirectory { get; set; }
}

/// <summary>
/// One drive to mount when the server starts.
/// </summary>
public sealed class DriveWireDriveOptions
{
    /// <summary>
    /// Gets or sets the drive number, 0 to 255. NitrOS-9's rbdw driver uses 0 to 3.
    /// </summary>
    public int Drive { get; set; }

    /// <summary>
    /// Gets or sets the image file.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether writes to the drive are refused (answered E$WP). The default is false.
    /// </summary>
    public bool ReadOnly { get; set; }
}
