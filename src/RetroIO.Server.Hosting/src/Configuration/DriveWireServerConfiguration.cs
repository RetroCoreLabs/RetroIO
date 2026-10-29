//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Server.Hosting.Configuration;

/// <summary>
/// Settings of the DriveWire server, as saved in drivewire-config.json.
/// </summary>
/// <remarks>
/// The defaults are those of RetroIO.Protocols.DriveWire: TCP port 65504 (the Becker port default of the emulators), the
/// loopback address only, a 250 ms byte time limit inside a transaction (from the DriveWire specification) and no drives.
/// </remarks>
public sealed class DriveWireServerConfiguration
{
    /// <summary>
    /// Gets or sets whether the DriveWire server runs. The default is true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the TCP port. The default is 65504.
    /// </summary>
    public int Port { get; set; } = 65504;

    /// <summary>
    /// Gets or sets the address to listen on. The default is 127.0.0.1, this machine only.
    /// </summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>
    /// Gets or sets the byte time limit inside a transaction, in milliseconds. The default is 250.
    /// </summary>
    public int TransactionTimeoutMilliseconds { get; set; } = 250;

    /// <summary>
    /// Gets or sets the drives to mount. The default is none.
    /// </summary>
    public List<DriveWireDriveConfiguration> Drives { get; set; } = new List<DriveWireDriveConfiguration>();

    /// <summary>
    /// Gets or sets the folder named objects are mounted from, or null for none.
    /// </summary>
    public string? NamedObjectDirectory { get; set; }

    /// <summary>
    /// Gets or sets the folder print jobs are written to, or null for none.
    /// </summary>
    public string? PrintDirectory { get; set; }

    /// <summary>
    /// Creates the default configuration.
    /// </summary>
    /// <returns>
    /// A new configuration with the default values and no drives.
    /// </returns>
    public static DriveWireServerConfiguration GetDefault()
    {
        return new DriveWireServerConfiguration();
    }
}

/// <summary>
/// One DriveWire drive in drivewire-config.json.
/// </summary>
public sealed class DriveWireDriveConfiguration
{
    /// <summary>
    /// Gets or sets the drive number, 0 to 255.
    /// </summary>
    public int Drive { get; set; }

    /// <summary>
    /// Gets or sets the image file.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether writes are refused. The default is false.
    /// </summary>
    public bool ReadOnly { get; set; }
}
