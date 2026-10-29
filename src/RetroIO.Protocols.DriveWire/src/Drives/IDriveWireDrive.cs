//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.DriveWire.Drives;

/// <summary>
/// One virtual drive: 256-byte sectors addressed by a 24-bit logical sector number (LSN).
/// </summary>
/// <remarks>
/// The protocol addresses a sector by drive number and LSN only ("DriveWire Protocol Version 4.0.0", "The Read Transaction":
/// bytes 2 to 4 are bits 23-16, 15-8 and 7-0 of the LSN). A drive has no geometry on the wire.
/// </remarks>
public interface IDriveWireDrive : IDisposable
{
    /// <summary>
    /// Gets the name shown for the drive, normally the image file path.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets whether writes are refused.
    /// </summary>
    bool IsReadOnly { get; }

    /// <summary>
    /// Gets the number of sectors in the volume. A sector number at or above this is past the end of the volume.
    /// </summary>
    uint TotalSectors { get; }

    /// <summary>
    /// Reads one sector.
    /// </summary>
    /// <param name="lsn">
    /// The logical sector number.
    /// </param>
    /// <param name="buffer">
    /// Receives the 256 sector bytes. It must hold at least 256 bytes.
    /// </param>
    /// <returns>
    /// 0 when the sector was read, otherwise the DriveWire error code to answer (see <see cref="Protocol.DriveWireError"/>).
    /// </returns>
    byte ReadSector(uint lsn, Span<byte> buffer);

    /// <summary>
    /// Writes one sector.
    /// </summary>
    /// <param name="lsn">
    /// The logical sector number.
    /// </param>
    /// <param name="data">
    /// The 256 sector bytes.
    /// </param>
    /// <returns>
    /// 0 when the sector was written, otherwise the DriveWire error code to answer.
    /// </returns>
    byte WriteSector(uint lsn, ReadOnlySpan<byte> data);
}
