//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.DriveWire.Drives;
using RetroIO.Protocols.DriveWire.Protocol;
using RetroIO.Protocols.DriveWire.Server;

namespace RetroIO.Protocols.DriveWire.Tests;

/// <summary>
/// Every operation of the server, driven with byte frames written out from "DriveWire Protocol Version 4.0.0"
/// (https://github.com/DrPitre/DriveWire/blob/main/DriveWire%20Specification.md) over a real loopback TCP connection.
/// </summary>
/// <remarks>
/// Each test that leaves the session expecting an operation code ends with <see cref="SessionHarness.AssertInStepAsync"/>,
/// which proves the server read exactly the bytes of the transaction under test, no more and no fewer.
/// </remarks>
[TestFixture]
public class DriveWireSessionTests
{
    private DriveWireDriveTable _drives = null!;
    private DriveWireServerStatistics _statistics = null!;
    private MemoryDrive _drive0 = null!;
    private MemoryDrive _drive1ReadOnly = null!;

    [SetUp]
    public void SetUp()
    {
        _drives = new DriveWireDriveTable();
        _statistics = new DriveWireServerStatistics();
        _drive0 = new MemoryDrive(40);
        _drive1ReadOnly = new MemoryDrive(40, readOnly: true);
        _drives.Mount(0, _drive0);
        _drives.Mount(1, _drive1ReadOnly);
    }

    [TearDown]
    public void TearDown()
    {
        // The table disposes the drives it holds; the two are disposed here as well because a test may have replaced them.
        _drives.Dispose();
        _drive0.Dispose();
        _drive1ReadOnly.Dispose();
    }

    private Task<SessionHarness> Start(DriveWireServerOptions? options = null, TimeProvider? time = null)
    {
        return SessionHarness.StartAsync(_drives, _statistics, options, time);
    }

    private static byte[] Frame(byte op, byte drive, uint lsn)
    {
        return new byte[] { op, drive, (byte)(lsn >> 16), (byte)(lsn >> 8), (byte)lsn };
    }

    private static byte[] WriteFrame(byte op, byte drive, uint lsn, byte[] data, ushort checksum)
    {
        byte[] frame = new byte[263];
        Frame(op, drive, lsn).CopyTo(frame, 0);
        data.CopyTo(frame, 5);
        frame[261] = (byte)(checksum >> 8);
        frame[262] = (byte)checksum;
        return frame;
    }

    private static byte[] Pattern(byte seed)
    {
        byte[] data = new byte[256];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(seed + (i * 13));
        }

        return data;
    }

    // ---------------------------------------------------------------- system transactions

    [Test]
    public async Task Nop_Init_Term_AreOneByteAndHaveNoAnswer()
    {
        await using SessionHarness h = await Start();

        await h.SendAsync(DriveWireOpCode.Nop, DriveWireOpCode.Init, DriveWireOpCode.Term);

        Assert.That(await h.IsSilentAsync(150), Is.True, "NOP, INIT and TERM are one-way");
        await h.AssertInStepAsync();
        Assert.That(_statistics.Transactions, Is.EqualTo(4));
    }

    [Test]
    public async Task Reset_AllThreeCodes_SetTheDriveCountersToZero_AndHaveNoAnswer()
    {
        await using SessionHarness h = await Start();

        byte[] codes = { DriveWireOpCode.Reset1, DriveWireOpCode.Reset2, DriveWireOpCode.Reset3 };
        for (int i = 0; i < codes.Length; i++)
        {
            (byte status, _) = await DriveWireTestClient.ReadExtendedAsync(h.Client, 0, 3);
            Assert.That(status, Is.EqualTo(DriveWireError.None));
            Assert.That(_drives.GetCounters(0).SectorsRead, Is.EqualTo(1));

            await h.SendAsync(codes[i]);
            Assert.That(await h.IsSilentAsync(100), Is.True, "RESET is one-way");
            await h.AssertInStepAsync();
            Assert.That(_drives.GetCounters(0).SectorsRead, Is.Zero, $"RESET ${codes[i]:X2} clears the per-drive counters");
        }

        Assert.That(_statistics.Resets, Is.EqualTo(3));
        Assert.That(_statistics.SectorsRead, Is.EqualTo(3), "the server totals are not reset");
    }

    [Test]
    public async Task DwInit_AnswersVersion4_AndRecordsTheClientVersion()
    {
        await using SessionHarness h = await Start();

        // dwio.asm Init: lda #OP_DWINIT, ldb #1, two bytes out, one byte back.
        await h.SendAsync(DriveWireOpCode.DriveWireInit, 0x01);

        byte[] answer = await h.ReceiveAsync(1);
        Assert.That(answer[0], Is.EqualTo(4), "dwio.asm ChkVer installs the poller only for 4 or $FF");
        Assert.That(h.Session.ClientDriverVersion, Is.EqualTo(1));
        await h.AssertInStepAsync();
    }

    [Test]
    public async Task Time_AnswersSixBytes_YearMinus1900_MonthDayHourMinuteSecond()
    {
        FixedTimeProvider clock = new FixedTimeProvider(new DateTimeOffset(2026, 10, 21, 13, 45, 7, TimeSpan.Zero));
        await using SessionHarness h = await Start(time: clock);

        await h.SendAsync(DriveWireOpCode.Time);

        Assert.That(await h.ReceiveAsync(6), Is.EqualTo(new byte[] { 126, 10, 21, 13, 45, 7 }));
        Assert.That(await h.IsSilentAsync(100), Is.True, "exactly six bytes");
    }

    [Test]
    public async Task SetTime_TakesSixBytes_AndHasNoAnswer()
    {
        await using SessionHarness h = await Start();

        // clock2_dw.asm SetTime: OP_SETTIME, then six bytes from D.Year.
        await h.SendAsync(DriveWireOpCode.SetTime, 126, 10, 21, 13, 45, 7);

        Assert.That(await h.IsSilentAsync(150), Is.True);
        await h.AssertInStepAsync();
        Assert.That(h.Session.LastClientTime, Is.EqualTo(new byte[] { 126, 10, 21, 13, 45, 7 }));
    }

    // ---------------------------------------------------------------- READEX / REREADEX

    [Test]
    public async Task ReadExtended_SendsTheSector_ThenAnswers0ForTheRightChecksum()
    {
        await using SessionHarness h = await Start();

        await h.SendAsync(Frame(DriveWireOpCode.ReadExtended, 0, 5));
        byte[] data = await h.ReceiveAsync(256);
        Assert.That(data, Is.EqualTo(_drive0.Sector(5)));

        ushort sum = DriveWireChecksum.Compute(data);
        await h.SendAsync((byte)(sum >> 8), (byte)sum);
        Assert.That((await h.ReceiveAsync(1))[0], Is.EqualTo(DriveWireError.None));

        Assert.That(await h.IsSilentAsync(100), Is.True);
        await h.AssertInStepAsync();
        Assert.That(_statistics.SectorsRead, Is.EqualTo(1));
        Assert.That(_drives.GetCounters(0).SectorsRead, Is.EqualTo(1));
    }

    [Test]
    public async Task ReadExtended_UsesAll24BitsOfTheLsn()
    {
        MemoryDrive big = new MemoryDrive(0x10203);
        _drives.Mount(2, big);
        await using SessionHarness h = await Start();

        (byte status, byte[] data) = await DriveWireTestClient.ReadExtendedAsync(h.Client, 2, 0x010202);

        Assert.That(status, Is.EqualTo(DriveWireError.None));
        Assert.That(data, Is.EqualTo(big.Sector(0x010202)));
    }

    [Test]
    public async Task ReadExtended_WrongChecksum_AnswersCrc_ThenReReadExtendedSucceeds()
    {
        await using SessionHarness h = await Start();

        await h.SendAsync(Frame(DriveWireOpCode.ReadExtended, 0, 7));
        byte[] data = await h.ReceiveAsync(256);
        ushort sum = DriveWireChecksum.Compute(data);
        ushort wrong = (ushort)(sum ^ 0x0100);
        await h.SendAsync((byte)(wrong >> 8), (byte)wrong);
        Assert.That((await h.ReceiveAsync(1))[0], Is.EqualTo(DriveWireError.Crc), "E$CRC, 243");

        // rbdw.asm: lda #OP_REREADEX, bra Read2 - the same frame with the reread code.
        await h.SendAsync(Frame(DriveWireOpCode.ReReadExtended, 0, 7));
        byte[] again = await h.ReceiveAsync(256);
        Assert.That(again, Is.EqualTo(_drive0.Sector(7)));
        await h.SendAsync((byte)(sum >> 8), (byte)sum);
        Assert.That((await h.ReceiveAsync(1))[0], Is.EqualTo(DriveWireError.None));

        await h.AssertInStepAsync();
        Assert.That(_statistics.ChecksumErrors, Is.EqualTo(1));
        Assert.That(_statistics.Rereads, Is.EqualTo(1));
        Assert.That(_statistics.SectorsRead, Is.EqualTo(1));
    }

    [Test]
    public async Task ReadExtended_ChecksumHighByteOnly_Wrong_AnswersCrc()
    {
        await using SessionHarness h = await Start();

        await h.SendAsync(Frame(DriveWireOpCode.ReadExtended, 0, 1));
        byte[] data = await h.ReceiveAsync(256);
        ushort sum = DriveWireChecksum.Compute(data);

        // The two checksum bytes swapped: only a server that reads them high byte first rejects this.
        await h.SendAsync((byte)sum, (byte)(sum >> 8));
        byte expected = (byte)sum == (byte)(sum >> 8) ? DriveWireError.None : DriveWireError.Crc;
        Assert.That((await h.ReceiveAsync(1))[0], Is.EqualTo(expected));
    }

    [Test]
    public async Task ReadExtended_PastTheEndOfTheVolume_Sends256Zeros_ThenReadError()
    {
        await using SessionHarness h = await Start();

        // A sector with data first, so zeros afterwards cannot be what happened to be left in a buffer.
        (byte first, _) = await DriveWireTestClient.ReadExtendedAsync(h.Client, 0, 5);
        Assert.That(first, Is.EqualTo(DriveWireError.None));

        // Drive 0 has 40 sectors: LSN 40 is the first one past the end.
        await h.SendAsync(Frame(DriveWireOpCode.ReadExtended, 0, 40));
        byte[] data = await h.ReceiveAsync(256);
        Assert.That(data, Is.EqualTo(new byte[256]), "the Read Failure packet is 256 bytes of 0");
        await h.SendAsync(0, 0);
        Assert.That((await h.ReceiveAsync(1))[0], Is.EqualTo(DriveWireError.Read));

        await h.AssertInStepAsync();
        Assert.That(_statistics.ReadErrors, Is.EqualTo(1));
        Assert.That(_drives.GetCounters(0).ReadErrors, Is.EqualTo(1));
    }

    [Test]
    public async Task ReadExtended_LastSectorOfTheVolume_IsRead()
    {
        await using SessionHarness h = await Start();

        (byte status, byte[] data) = await DriveWireTestClient.ReadExtendedAsync(h.Client, 0, 39);

        Assert.That(status, Is.EqualTo(DriveWireError.None));
        Assert.That(data, Is.EqualTo(_drive0.Sector(39)));
    }

    [Test]
    public async Task ReadExtended_EmptyDrive_Sends256Zeros_ThenNotReady()
    {
        await using SessionHarness h = await Start();
        (byte first, _) = await DriveWireTestClient.ReadExtendedAsync(h.Client, 0, 6);
        Assert.That(first, Is.EqualTo(DriveWireError.None));

        await h.SendAsync(Frame(DriveWireOpCode.ReadExtended, 9, 0));
        Assert.That(await h.ReceiveAsync(256), Is.EqualTo(new byte[256]));
        await h.SendAsync(0, 0);
        Assert.That((await h.ReceiveAsync(1))[0], Is.EqualTo(DriveWireError.NotReady), "E$NotRdy, 246");
        await h.AssertInStepAsync();
    }

    // ---------------------------------------------------------------- READ / REREAD (DriveWire 3 form)

    [Test]
    public async Task Read_AnswersZero_Checksum_ThenTheSector()
    {
        await using SessionHarness h = await Start();

        await h.SendAsync(Frame(DriveWireOpCode.Read, 0, 2));

        byte[] answer = await h.ReceiveAsync(259);
        byte[] sector = _drive0.Sector(2);

        // Worked out here rather than with DriveWireChecksum, so this test also pins the algorithm: the plain sum of the 256
        // bytes, kept to 16 bits (dwcheck.asm DoCSum).
        int total = 0;
        for (int i = 0; i < sector.Length; i++)
        {
            total += sector[i];
        }

        ushort sum = (ushort)total;
        Assert.That(total, Is.GreaterThan(255), "the test sector's sum needs both checksum bytes");
        Assert.That(answer[0], Is.EqualTo(DriveWireError.None));
        Assert.That(answer[1], Is.EqualTo((byte)(sum >> 8)), "checksum high byte first");
        Assert.That(answer[2], Is.EqualTo((byte)sum));
        Assert.That(answer.AsSpan(3).ToArray(), Is.EqualTo(sector));
        Assert.That(await h.IsSilentAsync(100), Is.True, "the client sends nothing back after a READ");
        await h.AssertInStepAsync();
    }

    [Test]
    public async Task ReRead_IsTheSameTransactionAsRead()
    {
        await using SessionHarness h = await Start();

        await h.SendAsync(Frame(DriveWireOpCode.ReRead, 0, 2));

        byte[] answer = await h.ReceiveAsync(259);
        Assert.That(answer[0], Is.EqualTo(DriveWireError.None));
        Assert.That(answer.AsSpan(3).ToArray(), Is.EqualTo(_drive0.Sector(2)));
        await h.AssertInStepAsync();
        Assert.That(_statistics.Rereads, Is.EqualTo(1));
    }

    [Test]
    public async Task Read_Error_AnswersOnlyTheErrorByte()
    {
        await using SessionHarness h = await Start();

        await h.SendAsync(Frame(DriveWireOpCode.Read, 0, 100));

        Assert.That((await h.ReceiveAsync(1))[0], Is.EqualTo(DriveWireError.Read));
        Assert.That(await h.IsSilentAsync(100), Is.True, "no checksum and no data after an error");
        await h.AssertInStepAsync();
    }

    // ---------------------------------------------------------------- WRITE / REWRITE

    [Test]
    public async Task Write_RightChecksum_WritesTheSector_AndAnswers0()
    {
        await using SessionHarness h = await Start();
        byte[] data = Pattern(0x41);

        await h.SendAsync(WriteFrame(DriveWireOpCode.Write, 0, 12, data, DriveWireChecksum.Compute(data)));

        Assert.That((await h.ReceiveAsync(1))[0], Is.EqualTo(DriveWireError.None));
        Assert.That(_drive0.Sector(12), Is.EqualTo(data));
        await h.AssertInStepAsync();
        Assert.That(_statistics.SectorsWritten, Is.EqualTo(1));
        Assert.That(_drives.GetCounters(0).SectorsWritten, Is.EqualTo(1));
    }

    [Test]
    public async Task Write_WrongChecksum_AnswersCrc_AndWritesNothing_ThenReWriteWrites()
    {
        await using SessionHarness h = await Start();
        byte[] before = _drive0.Sector(12);
        byte[] data = Pattern(0x42);
        ushort sum = DriveWireChecksum.Compute(data);

        await h.SendAsync(WriteFrame(DriveWireOpCode.Write, 0, 12, data, (ushort)(sum + 1)));
        Assert.That((await h.ReceiveAsync(1))[0], Is.EqualTo(DriveWireError.Crc));
        Assert.That(_drive0.Sector(12), Is.EqualTo(before), "nothing is written on a checksum error");

        // rbdw.asm: on E$CRC, lda #OP_REWRIT and send the whole frame again.
        await h.SendAsync(WriteFrame(DriveWireOpCode.ReWrite, 0, 12, data, sum));
        Assert.That((await h.ReceiveAsync(1))[0], Is.EqualTo(DriveWireError.None));
        Assert.That(_drive0.Sector(12), Is.EqualTo(data));

        await h.AssertInStepAsync();
        Assert.That(_statistics.ChecksumErrors, Is.EqualTo(1));
        Assert.That(_statistics.Rewrites, Is.EqualTo(1));
        Assert.That(_statistics.SectorsWritten, Is.EqualTo(1));
    }

    [Test]
    public async Task Write_ChecksumOverAll256Bytes_LastByteCounts()
    {
        await using SessionHarness h = await Start();
        byte[] data = new byte[256];
        data[255] = 0x55;

        // A checksum computed over the first 255 bytes only, as the specification's sample C routine does, is 0 here and
        // must be refused, because the client (dwcheck.asm DoCSum) adds all 256 bytes.
        await h.SendAsync(WriteFrame(DriveWireOpCode.Write, 0, 3, data, 0));
        Assert.That((await h.ReceiveAsync(1))[0], Is.EqualTo(DriveWireError.Crc));

        await h.SendAsync(WriteFrame(DriveWireOpCode.Write, 0, 3, data, 0x0055));
        Assert.That((await h.ReceiveAsync(1))[0], Is.EqualTo(DriveWireError.None));
    }

    [Test]
    public async Task Write_ReadOnlyDrive_AnswersWriteProtect()
    {
        await using SessionHarness h = await Start();
        byte[] before = _drive1ReadOnly.Sector(4);
        byte[] data = Pattern(0x43);

        await h.SendAsync(WriteFrame(DriveWireOpCode.Write, 1, 4, data, DriveWireChecksum.Compute(data)));

        Assert.That((await h.ReceiveAsync(1))[0], Is.EqualTo(DriveWireError.WriteProtect), "E$WP, 242");
        Assert.That(_drive1ReadOnly.Sector(4), Is.EqualTo(before));
        await h.AssertInStepAsync();
        Assert.That(_statistics.WriteErrors, Is.EqualTo(1));
    }

    [Test]
    public async Task Write_PastTheEndOfTheVolume_AnswersWriteError()
    {
        await using SessionHarness h = await Start();
        byte[] data = Pattern(0x44);

        await h.SendAsync(WriteFrame(DriveWireOpCode.Write, 0, 40, data, DriveWireChecksum.Compute(data)));

        Assert.That((await h.ReceiveAsync(1))[0], Is.EqualTo(DriveWireError.Write), "E$Write, 245");
        await h.AssertInStepAsync();
    }

    [Test]
    public async Task Write_EmptyDrive_AnswersNotReady()
    {
        await using SessionHarness h = await Start();
        byte[] data = Pattern(0x45);

        await h.SendAsync(WriteFrame(DriveWireOpCode.Write, 200, 0, data, DriveWireChecksum.Compute(data)));

        Assert.That((await h.ReceiveAsync(1))[0], Is.EqualTo(DriveWireError.NotReady));
        await h.AssertInStepAsync();
    }

    // ---------------------------------------------------------------- GETSTAT / SETSTAT

    [Test]
    public async Task GetStat_And_SetStat_TakeDriveAndCode_AndHaveNoAnswer()
    {
        await using SessionHarness h = await Start();

        // rbdw.asm GetStat: three bytes, the op, the drive (PD.DRV) and the code (R$B).
        await h.SendAsync(DriveWireOpCode.GetStat, 0, 0x26, DriveWireOpCode.SetStat, 0, 0x0A);

        Assert.That(await h.IsSilentAsync(150), Is.True);
        await h.AssertInStepAsync();
    }

    // ---------------------------------------------------------------- printing

    [Test]
    public async Task Print_ThenPrintFlush_GivesTheJob_AndWritesItToThePrintFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "dw-print-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using SessionHarness h = await Start(new DriveWireServerOptions { PrintDirectory = folder });
            byte[]? job = null;
            h.Session.PrintJobReceived += (_, bytes) => job = bytes;

            await h.SendAsync(DriveWireOpCode.Print, (byte)'H', DriveWireOpCode.Print, (byte)'I', DriveWireOpCode.Print, 0x0D);
            Assert.That(await h.IsSilentAsync(100), Is.True);
            Assert.That(job, Is.Null, "nothing is printed before the flush");

            await h.SendAsync(DriveWireOpCode.PrintFlush);
            await h.AssertInStepAsync();

            Assert.That(job, Is.EqualTo(new byte[] { (byte)'H', (byte)'I', 0x0D }));
            string[] files = Directory.GetFiles(folder);
            Assert.That(files, Has.Length.EqualTo(1));
            Assert.That(File.ReadAllBytes(files[0]), Is.EqualTo(new byte[] { (byte)'H', (byte)'I', 0x0D }));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
    }

    // ---------------------------------------------------------------- named objects

    [Test]
    public async Task NamedObjectMount_WithoutAFolder_Answers0()
    {
        await using SessionHarness h = await Start();

        await h.SendAsync(DriveWireOpCode.NamedObjectMount, 3, (byte)'A', (byte)'B', (byte)'C');

        Assert.That((await h.ReceiveAsync(1))[0], Is.Zero);
        await h.AssertInStepAsync();
    }

    [Test]
    public async Task NamedObjectMount_FileInTheFolder_MountsInTheLowestFreeDriveFrom1_AndAgainGivesTheSameDrive()
    {
        string folder = Path.Combine(Path.GetTempPath(), "dw-named-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "boot.dsk"), Os9VolumeBuilder.Build(200, false, out _));
            await using SessionHarness h = await Start(new DriveWireServerOptions { NamedObjectDirectory = folder });
            byte[] name = System.Text.Encoding.ASCII.GetBytes("boot.dsk");
            byte[] request = new byte[2 + name.Length];
            request[0] = DriveWireOpCode.NamedObjectMount;
            request[1] = (byte)name.Length;
            name.CopyTo(request, 2);

            await h.SendAsync(request);
            byte drive = (await h.ReceiveAsync(1))[0];
            Assert.That(drive, Is.EqualTo(2), "drives 0 and 1 are in use, so the first free one from 1 is 2");

            (byte status, byte[] sector0) = await DriveWireTestClient.ReadExtendedAsync(h.Client, drive, 0);
            Assert.That(status, Is.EqualTo(DriveWireError.None));
            Assert.That(sector0[2], Is.EqualTo(200), "DD.TOT of the mounted volume");

            await h.SendAsync(request);
            Assert.That((await h.ReceiveAsync(1))[0], Is.EqualTo(drive), "an object already mounted keeps its drive");

            byte[] missing = { DriveWireOpCode.NamedObjectMount, 4, (byte)'n', (byte)'o', (byte)'n', (byte)'e' };
            await h.SendAsync(missing);
            Assert.That((await h.ReceiveAsync(1))[0], Is.Zero, "a name with no file fails");

            byte[] escape = { DriveWireOpCode.NamedObjectMount, 5, (byte)'.', (byte)'.', (byte)'/', (byte)'x', (byte)'y' };
            await h.SendAsync(escape);
            Assert.That((await h.ReceiveAsync(1))[0], Is.Zero, "a name cannot leave the folder");
            await h.AssertInStepAsync();
        }
        finally
        {
            _drives.EjectAll();
            Directory.Delete(folder, true);
        }
    }

    [Test]
    public async Task NamedObjectCreate_Answers0()
    {
        await using SessionHarness h = await Start();

        await h.SendAsync(DriveWireOpCode.NamedObjectCreate, 2, (byte)'N', (byte)'W');

        Assert.That((await h.ReceiveAsync(1))[0], Is.Zero);
        await h.AssertInStepAsync();
    }

    // ---------------------------------------------------------------- virtual serial

    [Test]
    public async Task SerialRead_NothingWaiting_AnswersTwoZeros()
    {
        await using SessionHarness h = await Start();

        await h.SendAsync(DriveWireOpCode.SerialRead);

        Assert.That(await h.ReceiveAsync(2), Is.EqualTo(new byte[] { 0, 0 }));
        await h.AssertInStepAsync();
    }

    [Test]
    public async Task SerialRead_OneByteWaiting_AnswersChannelPlus1_AndTheByte()
    {
        await using SessionHarness h = await Start();
        h.Session.VirtualSerial.Send(2, new byte[] { 0x41 });

        await h.SendAsync(DriveWireOpCode.SerialRead);

        Assert.That(await h.ReceiveAsync(2), Is.EqualTo(new byte[] { 3, 0x41 }));
        await h.SendAsync(DriveWireOpCode.SerialRead);
        Assert.That(await h.ReceiveAsync(2), Is.EqualTo(new byte[] { 0, 0 }), "the byte was delivered");
    }

    [Test]
    public async Task SerialRead_SeveralBytes_AnswersChannelPlus17_AndTheCount_ThenSerialReadMultipleFetchesThem()
    {
        await using SessionHarness h = await Start();
        h.Session.VirtualSerial.Send(0, new byte[] { 1, 2, 3 });

        await h.SendAsync(DriveWireOpCode.SerialRead);
        Assert.That(await h.ReceiveAsync(2), Is.EqualTo(new byte[] { 17, 3 }));

        // dwio.asm IRQMulti3: OP_SERREADM, port, byte count.
        await h.SendAsync(DriveWireOpCode.SerialReadMultiple, 0, 3);
        Assert.That(await h.ReceiveAsync(3), Is.EqualTo(new byte[] { 1, 2, 3 }));
        await h.AssertInStepAsync();
    }

    [Test]
    public async Task SerialReadMultiple_MoreThanWaiting_SendsNothing()
    {
        await using SessionHarness h = await Start();
        h.Session.VirtualSerial.Send(1, new byte[] { 9, 9 });

        await h.SendAsync(DriveWireOpCode.SerialReadMultiple, 1, 3);

        Assert.That(await h.IsSilentAsync(200), Is.True, "the specification: the server does not respond");
        Assert.That(h.Session.VirtualSerial.Pending(1), Is.EqualTo(2), "nothing was taken");
        await h.AssertInStepAsync();
    }

    [Test]
    public async Task SerialRead_ServerClosedChannel_AnswersStatus16_AndTheChannel()
    {
        await using SessionHarness h = await Start();
        await h.SendAsync(DriveWireOpCode.SerialInit, 5);
        await h.AssertInStepAsync();
        Assert.That(h.Session.VirtualSerial.IsOpen(5), Is.True);

        h.Session.VirtualSerial.Close(5);
        await h.SendAsync(DriveWireOpCode.SerialRead);

        Assert.That(await h.ReceiveAsync(2), Is.EqualTo(new byte[] { 16, 5 }));
        Assert.That(h.Session.VirtualSerial.IsOpen(5), Is.False);
    }

    [Test]
    public async Task SerialWrite_FastWrite_SerialWriteMultiple_HandTheBytesToTheHost()
    {
        await using SessionHarness h = await Start();
        List<string> seen = new List<string>();
        h.Session.VirtualSerial.OutputReceived += (channel, bytes) =>
        {
            lock (seen)
            {
                seen.Add(channel + ":" + Convert.ToHexString(bytes));
            }
        };

        await h.SendAsync(DriveWireOpCode.SerialWrite, 4, 0x61);
        await h.SendAsync((byte)(DriveWireOpCode.FastWriteBase + 2), 0x62);
        await h.SendAsync(DriveWireOpCode.SerialWriteMultiple, 7, 3, 0x63, 0x64, 0x65);
        await h.AssertInStepAsync();

        Assert.That(seen, Is.EqualTo(new[] { "4:61", "2:62", "7:636465" }));
    }

    [Test]
    public async Task FastWrite_Channel15_IsCountedAsDropped()
    {
        await using SessionHarness h = await Start();

        await h.SendAsync(DriveWireOpCode.FastWriteLast, 0x70);
        await h.AssertInStepAsync();

        Assert.That(h.Session.VirtualSerial.DroppedBytes, Is.EqualTo(1));
    }

    [Test]
    public async Task SerialSetStat_ComSt_Takes26MoreBytes_OpenAndCloseChangeTheChannel()
    {
        await using SessionHarness h = await Start();

        // scdwv.asm SetStat SS.Open: ldd #SS.Open*256+OP_SERSETSTAT, sent as op, channel, code.
        await h.SendAsync(DriveWireOpCode.SerialSetStat, 3, DriveWireVirtualSerial.SetStatOpen);
        await h.AssertInStepAsync();
        Assert.That(h.Session.VirtualSerial.IsOpen(3), Is.True);

        byte[] comSt = new byte[3 + 26];
        comSt[0] = DriveWireOpCode.SerialSetStat;
        comSt[1] = 3;
        comSt[2] = DriveWireVirtualSerial.SetStatComSt;
        for (int i = 3; i < comSt.Length; i++)
        {
            // Descriptor bytes that are also operation codes: a server that does not take all 26 answers one of them.
            comSt[i] = DriveWireOpCode.Time;
        }

        await h.SendAsync(comSt);
        Assert.That(await h.IsSilentAsync(150), Is.True, "the 26 descriptor bytes are data, not operations");
        await h.AssertInStepAsync();

        await h.SendAsync(DriveWireOpCode.SerialSetStat, 3, DriveWireVirtualSerial.SetStatClose);
        await h.AssertInStepAsync();
        Assert.That(h.Session.VirtualSerial.IsOpen(3), Is.False);
    }

    [Test]
    public async Task SerialInit_SerialTerm_SerialGetStat()
    {
        await using SessionHarness h = await Start();

        await h.SendAsync(DriveWireOpCode.SerialInit, 6);
        await h.AssertInStepAsync();
        Assert.That(h.Session.VirtualSerial.IsOpen(6), Is.True);

        await h.SendAsync(DriveWireOpCode.SerialGetStat, 6, 0x01);
        await h.AssertInStepAsync();

        await h.SendAsync(DriveWireOpCode.SerialTerm, 6);
        await h.AssertInStepAsync();
        Assert.That(h.Session.VirtualSerial.IsOpen(6), Is.False);
    }

    // ---------------------------------------------------------------- framing

    [Test]
    public async Task UnknownOpCode_IsCountedAndIgnored()
    {
        await using SessionHarness h = await Start();

        await h.SendAsync(0x99);

        Assert.That(await h.IsSilentAsync(150), Is.True);
        await h.AssertInStepAsync();
        Assert.That(_statistics.UnknownOpCodes, Is.EqualTo(1));
    }

    [Test]
    public async Task ClientStopsInTheMiddle_TheTransactionIsGivenUp_AfterTheTimeout()
    {
        await using SessionHarness h = await Start(new DriveWireServerOptions { TransactionTimeoutMilliseconds = 100 });

        // Half a READEX request, then nothing.
        await h.SendAsync(DriveWireOpCode.ReadExtended, 0);
        Assert.That(await h.IsSilentAsync(400), Is.True);

        await h.AssertInStepAsync();
        Assert.That(_statistics.Timeouts, Is.EqualTo(1));
    }

    [Test]
    public async Task ClientStopsBeforeTheChecksum_TheTransactionIsGivenUp()
    {
        await using SessionHarness h = await Start(new DriveWireServerOptions { TransactionTimeoutMilliseconds = 100 });

        await h.SendAsync(Frame(DriveWireOpCode.ReadExtended, 0, 1));
        await h.ReceiveAsync(256);
        Assert.That(await h.IsSilentAsync(400), Is.True);

        await h.AssertInStepAsync();
        Assert.That(_statistics.Timeouts, Is.EqualTo(1));
        Assert.That(_statistics.SectorsRead, Is.Zero, "a read the client never confirmed is not counted");
    }

    [Test]
    public async Task ConnectionClosedInTheMiddle_EndsTheSession()
    {
        SessionHarness h = await Start();
        await h.SendAsync(DriveWireOpCode.Write, 0, 0);
        h.Client.Socket.Shutdown(System.Net.Sockets.SocketShutdown.Send);

        await h.Run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(h.Run.IsCompletedSuccessfully, Is.True);
        await h.DisposeAsync();
    }

    [Test]
    public void Checksum_IsTheSixteenBitSumOfAllBytes()
    {
        byte[] data = new byte[256];
        Array.Fill(data, (byte)0xFF);

        Assert.That(DriveWireChecksum.Compute(data), Is.EqualTo(256 * 255 % 65536));
        Assert.That(DriveWireChecksum.Compute(new byte[] { 1, 2, 3 }), Is.EqualTo(6));
        Assert.That(DriveWireChecksum.Compute(ReadOnlySpan<byte>.Empty), Is.Zero);
    }
}
