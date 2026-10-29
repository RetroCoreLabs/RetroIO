//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.DriveWire.Protocol;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.Drivers;

namespace RetroIO.Protocols.DriveWire.Drives;

/// <summary>
/// A virtual drive backed by a Dragon or Color Computer disk image file, read and written through the RetroIO.Storage.Local
/// drivers.
/// </summary>
/// <remarks>
/// <para>
/// The driver is chosen by the file extension: .vdk opens with <see cref="DragonVdkDriver"/>, .dmk with
/// <see cref="DragonDmkDriver"/>, and every other name (.dsk, .jvc, .os9, .img and so on) with <see cref="DragonDskDriver"/>,
/// which also mounts the headerless OS-9 hard disk volumes NitrOS-9 builds for DriveWire. The DriveWire LSN is the
/// driver's linear block number (<see cref="BlockAddress.FromLBA"/>), which for a floppy image is the sector order of the
/// file: cylinder, then side, then sector.
/// </para>
/// <para>
/// The end of the volume is what the driver reports as its total blocks. For a headerless OS-9 volume that is DD.TOT from
/// sector 0, even when the file is shorter: the NitrOS-9 build formats its DriveWire disks without Toolshed's -e option, so
/// the files hold only the sectors written so far, and the driver reads the sectors past the end of the file as zero bytes.
/// A read at or past the end of the volume answers E$Read and a write there answers E$Write (see
/// <see cref="DriveWireError"/>); the specification gives no rule of its own for a sector outside the image.
/// </para>
/// <para>
/// A write goes to the driver's block write. How far the file grows is decided by the driver; this class adds nothing to
/// it. For a short headerless OS-9 volume the file grows only to the last sector written, as Toolshed os9 copy grows it;
/// a VDK or JVC file cut inside its last track becomes the whole disk at the first write, as XRoar's save makes it.
/// </para>
/// <para>
/// The drivers hold the image in 4 KB pages: only the parts the file holds and the parts written, not the whole volume, and
/// a flush writes back only the sectors that changed. A NitrOS-9 DriveWire volume of 524268 sectors in a 256 KB file
/// starts at about 256 KB per drive and grows with what is written.
/// </para>
/// </remarks>
public sealed class DriveWireImageDrive : IDriveWireDrive
{
    private const int SectorSize = 256;

    private readonly object _gate = new object();
    private readonly FileStream _stream;
    private readonly IFileSystemDriver _driver;
    private bool _disposed;

    private DriveWireImageDrive(string path, bool readOnly, FileStream stream, IFileSystemDriver driver, uint totalSectors)
    {
        Name = path;
        IsReadOnly = readOnly;
        _stream = stream;
        _driver = driver;
        TotalSectors = totalSectors;
    }

    /// <summary>
    /// Gets the image file path.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets whether the drive was opened read-only.
    /// </summary>
    public bool IsReadOnly { get; }

    /// <summary>
    /// Gets the number of sectors in the volume, as the driver reports it.
    /// </summary>
    public uint TotalSectors { get; }

    /// <summary>
    /// Gets the file system the driver found on the image, for display.
    /// </summary>
    public string FileSystemType => _driver.FileSystemType;

    /// <summary>
    /// Gets the text of the last read or write failure, or null when there was none.
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Opens an image file and mounts it through the matching driver.
    /// </summary>
    /// <param name="path">
    /// The image file.
    /// </param>
    /// <param name="readOnly">
    /// True to refuse writes and open the file for reading only.
    /// </param>
    /// <returns>
    /// The mounted drive.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when the path is empty.
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// Thrown when the file does not exist.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// Thrown when the driver mounts the image with a sector size other than 256 bytes.
    /// </exception>
    /// <remarks>
    /// Any exception the driver throws while mounting (an image it does not recognise, a bad header) is passed on, and the
    /// file is closed.
    /// </remarks>
    public static DriveWireImageDrive Open(string path, bool readOnly)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("The image path is empty.", nameof(path));
        }

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The DriveWire image file does not exist: " + fullPath, fullPath);
        }

        FileStream stream = new FileStream(
            fullPath,
            FileMode.Open,
            readOnly ? FileAccess.Read : FileAccess.ReadWrite,
            FileShare.Read);

        IFileSystemDriver driver = CreateDriver(fullPath);
        try
        {
            driver.Mount(stream, readOnly);
            BlockDeviceInfo? info = driver.GetBlockDeviceInfo();
            if (info == null)
            {
                throw new InvalidDataException("The driver mounted " + fullPath + " but reports no block device.");
            }

            if (info.BlockSize != SectorSize)
            {
                throw new InvalidDataException(
                    "The image " + fullPath + " has " + info.BlockSize + "-byte sectors; DriveWire moves 256-byte sectors.");
            }

            return new DriveWireImageDrive(fullPath, readOnly, stream, driver, info.TotalBlocks);
        }
        catch
        {
            driver.Dispose();
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads one sector through the driver.
    /// </summary>
    /// <param name="lsn">
    /// The logical sector number.
    /// </param>
    /// <param name="buffer">
    /// Receives the 256 sector bytes.
    /// </param>
    /// <returns>
    /// 0 when read; E$Read when the LSN is at or past the end of the volume or the driver fails; E$NotRdy after
    /// <see cref="Dispose"/>.
    /// </returns>
    public byte ReadSector(uint lsn, Span<byte> buffer)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return DriveWireError.NotReady;
            }

            if (lsn >= TotalSectors)
            {
                LastError = "Read of LSN " + lsn + " is past the end of the volume (" + TotalSectors + " sectors).";
                return DriveWireError.Read;
            }

            try
            {
                _driver.ReadBlock(BlockAddress.FromLBA(lsn), buffer.Slice(0, SectorSize));
                return DriveWireError.None;
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is InvalidOperationException)
            {
                // The driver could not read the sector. The client gets E$Read, which is the protocol's answer for exactly
                // this case (Appendix A), and the reason is kept for the log.
                LastError = "Read of LSN " + lsn + " failed: " + ex.Message;
                return DriveWireError.Read;
            }
        }
    }

    /// <summary>
    /// Writes one sector through the driver, which writes the image back.
    /// </summary>
    /// <param name="lsn">
    /// The logical sector number.
    /// </param>
    /// <param name="data">
    /// The 256 sector bytes.
    /// </param>
    /// <returns>
    /// 0 when written; E$WP when the drive is read-only or the driver refuses the write as read-only; E$Write when the LSN is
    /// at or past the end of the volume or the driver fails; E$NotRdy after <see cref="Dispose"/>.
    /// </returns>
    public byte WriteSector(uint lsn, ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return DriveWireError.NotReady;
            }

            if (IsReadOnly)
            {
                LastError = "Write of LSN " + lsn + " refused: the drive is read-only.";
                return DriveWireError.WriteProtect;
            }

            if (lsn >= TotalSectors)
            {
                LastError = "Write of LSN " + lsn + " is past the end of the volume (" + TotalSectors + " sectors).";
                return DriveWireError.Write;
            }

            try
            {
                _driver.WriteBlock(BlockAddress.FromLBA(lsn), data.Slice(0, SectorSize));
                return DriveWireError.None;
            }
            catch (UnauthorizedAccessException ex)
            {
                // The driver mounted the image read-only although the drive was opened for writing. DragonDskDriver does that
                // for a headerless OS-9 volume whose file is shorter than DD.TOT; its message says why.
                LastError = "Write of LSN " + lsn + " refused by the driver: " + ex.Message;
                return DriveWireError.WriteProtect;
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is InvalidOperationException)
            {
                LastError = "Write of LSN " + lsn + " failed: " + ex.Message;
                return DriveWireError.Write;
            }
        }
    }

    /// <summary>
    /// Unmounts the driver and closes the image file. Later reads and writes answer E$NotRdy.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Every write was already written back by the driver's block write, so nothing is pending here. DragonDskDriver's
            // Dispose drops the volume without closing the stream, while DragonVdkDriver's disposes its block device, which
            // closes it; FileStream.Dispose may be called twice, so the file is closed here in every case.
            _driver.Dispose();
            _stream.Dispose();
        }
    }

    private static IFileSystemDriver CreateDriver(string path)
    {
        string extension = Path.GetExtension(path);
        if (string.Equals(extension, ".vdk", StringComparison.OrdinalIgnoreCase))
        {
            return new DragonVdkDriver();
        }

        if (string.Equals(extension, ".dmk", StringComparison.OrdinalIgnoreCase))
        {
            return new DragonDmkDriver();
        }

        return new DragonDskDriver();
    }
}
