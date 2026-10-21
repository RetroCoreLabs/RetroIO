//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Management.Contracts;

/// <summary>
/// Counters of the DriveWire server.
/// </summary>
public class DriveWireStatisticsResponse
{
    /// <summary>
    /// Gets or sets whether a DriveWire server runs in this process.
    /// </summary>
    public bool IsHosted { get; set; }

    /// <summary>
    /// Gets or sets the end point the server listens on, or an empty string when it is not listening.
    /// </summary>
    public string Listening { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the client connections open now.
    /// </summary>
    public long ActiveConnections { get; set; }

    /// <summary>
    /// Gets or sets the client connections accepted since the start.
    /// </summary>
    public long TotalConnections { get; set; }

    /// <summary>
    /// Gets or sets the transactions handled.
    /// </summary>
    public long Transactions { get; set; }

    /// <summary>
    /// Gets or sets the sectors sent without error.
    /// </summary>
    public long SectorsRead { get; set; }

    /// <summary>
    /// Gets or sets the sectors written without error.
    /// </summary>
    public long SectorsWritten { get; set; }

    /// <summary>
    /// Gets or sets the reads answered with an error.
    /// </summary>
    public long ReadErrors { get; set; }

    /// <summary>
    /// Gets or sets the writes answered with an error.
    /// </summary>
    public long WriteErrors { get; set; }

    /// <summary>
    /// Gets or sets the checksum mismatches.
    /// </summary>
    public long ChecksumErrors { get; set; }

    /// <summary>
    /// Gets or sets the transactions given up because the client stopped sending.
    /// </summary>
    public long Timeouts { get; set; }

    /// <summary>
    /// Gets or sets the operation codes the server does not know.
    /// </summary>
    public long UnknownOpCodes { get; set; }

    /// <summary>
    /// Gets or sets the bytes received from clients.
    /// </summary>
    public long BytesReceived { get; set; }

    /// <summary>
    /// Gets or sets the bytes sent to clients.
    /// </summary>
    public long BytesSent { get; set; }

    /// <summary>
    /// Gets or sets the server uptime as text.
    /// </summary>
    public string Uptime { get; set; } = string.Empty;
}

/// <summary>
/// The mounted DriveWire drives.
/// </summary>
public class DriveWireDrivesResponse
{
    /// <summary>
    /// Gets or sets the drives that hold an image, in drive number order.
    /// </summary>
    public List<DriveWireDriveInfo> Drives { get; set; } = new();
}

/// <summary>
/// One mounted DriveWire drive.
/// </summary>
public class DriveWireDriveInfo
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
    /// Gets or sets whether writes are refused.
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Gets or sets the number of sectors in the volume.
    /// </summary>
    public long TotalSectors { get; set; }

    /// <summary>
    /// Gets or sets the sectors read from this drive since it was mounted or the last client reset.
    /// </summary>
    public long SectorsRead { get; set; }

    /// <summary>
    /// Gets or sets the sectors written to this drive since it was mounted or the last client reset.
    /// </summary>
    public long SectorsWritten { get; set; }

    /// <summary>
    /// Gets or sets the reads and writes of this drive answered with an error.
    /// </summary>
    public long Errors { get; set; }
}
