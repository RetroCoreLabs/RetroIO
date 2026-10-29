//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.DriveWire.Drives;

/// <summary>
/// The 256 virtual drives a DriveWire server offers, numbered 0 to 255, with a transfer count for each.
/// </summary>
/// <remarks>
/// <para>
/// The drive number is one byte on the wire ("DriveWire Protocol Version 4.0.0", "The Read Transaction": "Drive number
/// (0-255)"), so the table has 256 places. A client may use fewer: the NitrOS-9 driver rbdw.asm serves 4 drives (NumDrvs set
/// 4) and answers E$Unit itself for a higher number without sending anything.
/// </para>
/// <para>
/// One table is shared by every connection, so two clients that use the same drive number reach the same image. Each drive
/// serialises its own reads and writes.
/// </para>
/// </remarks>
public sealed class DriveWireDriveTable : IDisposable
{
    /// <summary>
    /// The number of drive numbers, 256.
    /// </summary>
    public const int DriveCount = 256;

    private readonly object _gate = new object();
    private readonly IDriveWireDrive?[] _drives = new IDriveWireDrive?[DriveCount];
    private readonly DriveWireDriveCounters[] _counters = new DriveWireDriveCounters[DriveCount];

    /// <summary>
    /// Creates an empty table.
    /// </summary>
    public DriveWireDriveTable()
    {
        for (int i = 0; i < DriveCount; i++)
        {
            _counters[i] = new DriveWireDriveCounters();
        }
    }

    /// <summary>
    /// Puts a drive in a drive number, replacing and disposing any drive that was there.
    /// </summary>
    /// <param name="driveNumber">
    /// The drive number.
    /// </param>
    /// <param name="drive">
    /// The drive. The table owns it from now on and disposes it when it is replaced or ejected.
    /// </param>
    public void Mount(byte driveNumber, IDriveWireDrive drive)
    {
        ArgumentNullException.ThrowIfNull(drive);

        IDriveWireDrive? old;
        lock (_gate)
        {
            old = _drives[driveNumber];
            _drives[driveNumber] = drive;
            _counters[driveNumber].Reset();
        }

        old?.Dispose();
    }

    /// <summary>
    /// Removes and disposes the drive in a drive number.
    /// </summary>
    /// <param name="driveNumber">
    /// The drive number.
    /// </param>
    /// <returns>
    /// True when a drive was there.
    /// </returns>
    public bool Eject(byte driveNumber)
    {
        IDriveWireDrive? old;
        lock (_gate)
        {
            old = _drives[driveNumber];
            _drives[driveNumber] = null;
        }

        if (old == null)
        {
            return false;
        }

        old.Dispose();
        return true;
    }

    /// <summary>
    /// Removes and disposes every drive.
    /// </summary>
    public void EjectAll()
    {
        for (int i = 0; i < DriveCount; i++)
        {
            Eject((byte)i);
        }
    }

    /// <summary>
    /// Gets the drive in a drive number.
    /// </summary>
    /// <param name="driveNumber">
    /// The drive number.
    /// </param>
    /// <returns>
    /// The drive, or null when the drive number is empty.
    /// </returns>
    public IDriveWireDrive? Get(byte driveNumber)
    {
        lock (_gate)
        {
            return _drives[driveNumber];
        }
    }

    /// <summary>
    /// Gets the transfer counters of a drive number.
    /// </summary>
    /// <param name="driveNumber">
    /// The drive number.
    /// </param>
    /// <returns>
    /// The counters. They are kept per drive number and start again from zero when a drive is mounted.
    /// </returns>
    public DriveWireDriveCounters GetCounters(byte driveNumber)
    {
        return _counters[driveNumber];
    }

    /// <summary>
    /// Finds the drive number that holds an image file.
    /// </summary>
    /// <param name="name">
    /// The drive name to look for (for an image drive, its full path), compared without regard to case.
    /// </param>
    /// <returns>
    /// The drive number, or -1 when no drive has that name.
    /// </returns>
    public int Find(string name)
    {
        lock (_gate)
        {
            for (int i = 0; i < DriveCount; i++)
            {
                IDriveWireDrive? drive = _drives[i];
                if (drive != null && string.Equals(drive.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Finds the lowest empty drive number in a range.
    /// </summary>
    /// <param name="first">
    /// The first drive number to look at.
    /// </param>
    /// <returns>
    /// The drive number, or -1 when every drive number from <paramref name="first"/> to 255 is in use.
    /// </returns>
    public int FindFree(int first)
    {
        lock (_gate)
        {
            for (int i = first; i < DriveCount; i++)
            {
                if (_drives[i] == null)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Sets the transfer counters of every drive back to zero.
    /// </summary>
    /// <remarks>
    /// This is what a RESET transaction asks for ("DriveWire Protocol Version 4.0.0", "The RESET Transaction": "Reset any
    /// statistical values related to read/write and sector transfer throughput for ALL drives").
    /// </remarks>
    public void ResetCounters()
    {
        for (int i = 0; i < DriveCount; i++)
        {
            _counters[i].Reset();
        }
    }

    /// <summary>
    /// Ejects every drive.
    /// </summary>
    public void Dispose()
    {
        EjectAll();
    }
}

/// <summary>
/// The sector transfer counts of one drive number.
/// </summary>
public sealed class DriveWireDriveCounters
{
    private long _sectorsRead;
    private long _sectorsWritten;
    private long _readErrors;
    private long _writeErrors;

    /// <summary>
    /// Gets the sectors sent to clients without error.
    /// </summary>
    public long SectorsRead => Interlocked.Read(ref _sectorsRead);

    /// <summary>
    /// Gets the sectors written to the image without error.
    /// </summary>
    public long SectorsWritten => Interlocked.Read(ref _sectorsWritten);

    /// <summary>
    /// Gets the reads answered with an error code.
    /// </summary>
    public long ReadErrors => Interlocked.Read(ref _readErrors);

    /// <summary>
    /// Gets the writes answered with an error code.
    /// </summary>
    public long WriteErrors => Interlocked.Read(ref _writeErrors);

    internal void RecordRead(bool ok)
    {
        if (ok)
        {
            Interlocked.Increment(ref _sectorsRead);
        }
        else
        {
            Interlocked.Increment(ref _readErrors);
        }
    }

    internal void RecordWrite(bool ok)
    {
        if (ok)
        {
            Interlocked.Increment(ref _sectorsWritten);
        }
        else
        {
            Interlocked.Increment(ref _writeErrors);
        }
    }

    internal void Reset()
    {
        Interlocked.Exchange(ref _sectorsRead, 0);
        Interlocked.Exchange(ref _sectorsWritten, 0);
        Interlocked.Exchange(ref _readErrors, 0);
        Interlocked.Exchange(ref _writeErrors, 0);
    }
}
