//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Net;
using System.Net.Sockets;
using RetroIO.Protocols.DriveWire.Drives;
using RetroIO.Protocols.DriveWire.Protocol;
using RetroIO.Protocols.DriveWire.Server;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.Drivers;

namespace RetroIO.Protocols.DriveWire.Tests;

/// <summary>
/// The server with image files behind its drives: a client over TCP against the whole server, on built OS-9 volumes and on
/// the real NitrOS-9 DriveWire (Becker) images.
/// </summary>
/// <remarks>
/// The real images are found through the RETROIO_NITROS9_IMAGES environment variable, which names a folder holding the
/// NitrOS-9 3.3.0 images (the files ending in _becker.dsk). The tests that need them are skipped with the reason when it is
/// not set. They are only read; the write test works on a copy in the temp folder.
/// </remarks>
[TestFixture]
public class DriveWireImageTests
{
    private const string ImagesVariable = "RETROIO_NITROS9_IMAGES";

    // The coco3 Level 2 Becker disk of the NitrOS-9 3.3.0 build; the first *_becker.dsk is used when it is not there.
    private const string PreferredImage = "NOS9_6809_L2_v030300_coco3_becker.dsk";

    private readonly List<string> _tempFiles = new List<string>();

    [TearDown]
    public void TearDown()
    {
        for (int i = 0; i < _tempFiles.Count; i++)
        {
            if (File.Exists(_tempFiles[i]))
            {
                File.Delete(_tempFiles[i]);
            }
        }

        _tempFiles.Clear();
    }

    // ---------------------------------------------------------------- the whole server over TCP, real images

    [Test]
    public async Task RealBeckerImage_Sector0AndTheBootFile_ReadOverTcp_MatchOurOwnReader()
    {
        string image = FindBeckerImage();
        byte[] file = File.ReadAllBytes(image);

        await using RunningServer server = await RunningServer.StartAsync(new DriveWireDriveOptions { Drive = 0, Path = image, ReadOnly = true });
        using TcpClient client = await server.ConnectAsync();
        NetworkStream stream = client.GetStream();

        (byte status, byte[] lsn0) = await DriveWireTestClient.ReadExtendedAsync(stream, 0, 0);
        Assert.That(status, Is.EqualTo(DriveWireError.None));
        Assert.That(lsn0, Is.EqualTo(file.AsSpan(0, 256).ToArray()), "LSN 0 is the first 256 bytes of the file");

        byte[] bootOverDriveWire = await ReadBootFileAsync(stream, lsn0);

        byte[] bootFromOurReader;
        using (FileStream fs = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            DragonDskDriver driver = new DragonDskDriver();
            driver.Mount(fs, true);
            bootFromOurReader = driver.ReadFile("/OS9Boot");
            driver.Dispose();
        }

        Assert.That(bootOverDriveWire.Length, Is.GreaterThan(0));
        Assert.That(bootOverDriveWire, Is.EqualTo(bootFromOurReader), "OS9Boot read the way boot_common.asm reads it");
        TestContext.Out.WriteLine($"{Path.GetFileName(image)}: OS9Boot {bootOverDriveWire.Length} bytes over DriveWire");
    }

    [Test]
    public async Task RealBeckerImage_EndOfVolume_ZerosBetweenTheEndOfTheFileAndDdTot_ReadErrorFromDdTot()
    {
        string image = FindBeckerImage();
        byte[] file = File.ReadAllBytes(image);
        uint ddTot = (uint)((file[0] << 16) | (file[1] << 8) | file[2]);
        uint fileSectors = (uint)(file.Length / 256);
        Assert.That(ddTot, Is.GreaterThan(fileSectors), "a NitrOS-9 DriveWire image is shorter than its volume");

        await using RunningServer server = await RunningServer.StartAsync(new DriveWireDriveOptions { Drive = 0, Path = image, ReadOnly = true });
        using TcpClient client = await server.ConnectAsync();
        NetworkStream stream = client.GetStream();

        (byte lastInFile, byte[] lastData) = await DriveWireTestClient.ReadExtendedAsync(stream, 0, fileSectors - 1);
        Assert.That(lastInFile, Is.EqualTo(DriveWireError.None));
        Assert.That(lastData, Is.EqualTo(file.AsSpan((int)((fileSectors - 1) * 256), 256).ToArray()));

        (byte pastFile, byte[] pastData) = await DriveWireTestClient.ReadExtendedAsync(stream, 0, fileSectors);
        Assert.That(pastFile, Is.EqualTo(DriveWireError.None), "below DD.TOT the sector exists");
        Assert.That(pastData, Is.EqualTo(new byte[256]), "a sector the file does not hold reads as zeros");

        (byte lastOfVolume, _) = await DriveWireTestClient.ReadExtendedAsync(stream, 0, ddTot - 1);
        Assert.That(lastOfVolume, Is.EqualTo(DriveWireError.None));

        (byte atDdTot, byte[] atDdTotData) = await DriveWireTestClient.ReadExtendedAsync(stream, 0, ddTot);
        Assert.That(atDdTot, Is.EqualTo(DriveWireError.Read), "DD.TOT is the first sector past the volume");
        Assert.That(atDdTotData, Is.EqualTo(new byte[256]));
    }

    [Test]
    public async Task RealBeckerImage_WriteOnACopy_GrowsTheFileOnlyToTheWrittenSector()
    {
        string copy = CopyToTemp(FindBeckerImage());
        byte[] original = File.ReadAllBytes(copy);
        uint fileSectors = (uint)(original.Length / 256);
        uint lsn = fileSectors + 5;
        byte[] data = new byte[256];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(0xA5 ^ i);
        }

        byte status;
        await using (RunningServer server = await RunningServer.StartAsync(new DriveWireDriveOptions { Drive = 0, Path = copy, ReadOnly = false }))
        {
            using TcpClient client = await server.ConnectAsync();
            status = await DriveWireTestClient.WriteAsync(client.GetStream(), 0, lsn, data);
        }

        Assert.That(status, Is.EqualTo(DriveWireError.None), "a write below DD.TOT on a writable drive");
        byte[] after = File.ReadAllBytes(copy);
        Assert.That(after.Length, Is.EqualTo((lsn + 1) * 256), "the file grows to the written sector and no further");
        Assert.That(after.AsSpan(0, original.Length).ToArray(), Is.EqualTo(original), "the sectors that were there are unchanged");
        Assert.That(after.AsSpan(original.Length, 5 * 256).ToArray(), Is.EqualTo(new byte[5 * 256]), "the gap reads as zeros");
        Assert.That(after.AsSpan((int)(lsn * 256), 256).ToArray(), Is.EqualTo(data));
    }

    // ---------------------------------------------------------------- built OS-9 volumes, no image folder needed

    [Test]
    public async Task BuiltWholeVolume_ReadWriteOverTcp_ChangesExactlyThatSectorOfTheFile()
    {
        string path = WriteTemp(Os9VolumeBuilder.Build(400, false, out _));
        byte[] before = File.ReadAllBytes(path);
        byte[] data = new byte[256];
        Array.Fill(data, (byte)0x5A);

        await using (RunningServer server = await RunningServer.StartAsync(new DriveWireDriveOptions { Drive = 3, Path = path }))
        {
            using TcpClient client = await server.ConnectAsync();
            NetworkStream stream = client.GetStream();

            (byte status, byte[] lsn0) = await DriveWireTestClient.ReadExtendedAsync(stream, 3, 0);
            Assert.That(status, Is.EqualTo(DriveWireError.None));
            Assert.That(lsn0, Is.EqualTo(before.AsSpan(0, 256).ToArray()));

            Assert.That(await DriveWireTestClient.WriteAsync(stream, 3, 300, data), Is.EqualTo(DriveWireError.None));
            Assert.That(await DriveWireTestClient.WriteAsync(stream, 3, 400, data), Is.EqualTo(DriveWireError.Write), "LSN 400 is past the volume");

            (byte back, byte[] readBack) = await DriveWireTestClient.ReadExtendedAsync(stream, 3, 300);
            Assert.That(back, Is.EqualTo(DriveWireError.None));
            Assert.That(readBack, Is.EqualTo(data));
        }

        byte[] after = File.ReadAllBytes(path);
        Assert.That(after.Length, Is.EqualTo(before.Length));
        Array.Copy(data, 0, before, 300 * 256, 256);
        Assert.That(after, Is.EqualTo(before), "only sector 300 changed");
    }

    [Test]
    public async Task BuiltVolume_ReadOnlyDrive_RefusesWrites_AndLeavesTheFileAlone()
    {
        string path = WriteTemp(Os9VolumeBuilder.Build(400, false, out _));
        byte[] before = File.ReadAllBytes(path);

        await using (RunningServer server = await RunningServer.StartAsync(new DriveWireDriveOptions { Drive = 0, Path = path, ReadOnly = true }))
        {
            using TcpClient client = await server.ConnectAsync();
            Assert.That(await DriveWireTestClient.WriteAsync(client.GetStream(), 0, 10, new byte[256]), Is.EqualTo(DriveWireError.WriteProtect));
        }

        Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
    }

    [Test]
    public async Task BuiltShortVolume_ReadsZerosPastTheFile_AndAWriteThereGrowsTheFileToThatSector()
    {
        byte[] image = Os9VolumeBuilder.Build(1000, true, out int used);
        string path = WriteTemp(image);
        byte[] data = new byte[256];
        Array.Fill(data, (byte)0x3C);

        byte writeStatus;
        await using (RunningServer server = await RunningServer.StartAsync(new DriveWireDriveOptions { Drive = 0, Path = path }))
        {
            using TcpClient client = await server.ConnectAsync();
            NetworkStream stream = client.GetStream();

            (byte status, byte[] past) = await DriveWireTestClient.ReadExtendedAsync(stream, 0, (uint)used + 2);
            Assert.That(status, Is.EqualTo(DriveWireError.None));
            Assert.That(past, Is.EqualTo(new byte[256]));

            (byte atEnd, _) = await DriveWireTestClient.ReadExtendedAsync(stream, 0, 1000);
            Assert.That(atEnd, Is.EqualTo(DriveWireError.Read), "DD.TOT 1000 ends the volume");

            writeStatus = await DriveWireTestClient.WriteAsync(stream, 0, (uint)used + 2, data);
        }

        Assert.That(writeStatus, Is.EqualTo(DriveWireError.None), "a write below DD.TOT on a writable drive");
        byte[] after = File.ReadAllBytes(path);
        Assert.That(after.Length, Is.EqualTo((used + 3) * 256), "the file grows to the written sector and no further");
        Assert.That(after.AsSpan(0, image.Length).ToArray(), Is.EqualTo(image));
        Assert.That(after.AsSpan((used + 2) * 256, 256).ToArray(), Is.EqualTo(data));
    }

    [Test]
    public void ImageDrive_TheVolumeEndsAtDdTot_TheSectorBeforeIsServed_DdTotItselfIsRefusedAsPastTheEnd()
    {
        byte[] image = Os9VolumeBuilder.Build(1000, true, out int used);
        string path = WriteTemp(image);
        using DriveWireImageDrive drive = DriveWireImageDrive.Open(path, true);
        byte[] buffer = new byte[256];

        Assert.That(drive.TotalSectors, Is.EqualTo(1000u), "DD.TOT, not the " + used + " sectors of the file");
        Assert.That(drive.ReadSector(999, buffer), Is.EqualTo(DriveWireError.None));
        Assert.That(buffer, Is.EqualTo(new byte[256]));
        Assert.That(drive.LastError, Is.Null);

        Assert.That(drive.ReadSector(1000, buffer), Is.EqualTo(DriveWireError.Read));
        Assert.That(drive.LastError, Does.Contain("past the end of the volume"), "refused by the volume rule, not by a driver failure");
        Assert.That(drive.ReadSector(0xFFFFFF, buffer), Is.EqualTo(DriveWireError.Read));
    }

    [Test]
    public void ImageDrive_WriteAtDdTot_IsRefusedAsPastTheEnd()
    {
        string path = WriteTemp(Os9VolumeBuilder.Build(400, false, out _));
        using DriveWireImageDrive drive = DriveWireImageDrive.Open(path, false);

        Assert.That(drive.WriteSector(400, new byte[256]), Is.EqualTo(DriveWireError.Write));
        Assert.That(drive.LastError, Does.Contain("past the end of the volume"));
        Assert.That(new FileInfo(path).Length, Is.EqualTo(400 * 256));
    }

    [Test]
    public async Task Server_StartReadStop_ClosesThePortAndTheImageFile_AndStartsAgain()
    {
        string path = WriteTemp(Os9VolumeBuilder.Build(400, false, out _));
        DriveWireServerOptions options = new DriveWireServerOptions { Port = 0 };
        options.Drives.Add(new DriveWireDriveOptions { Drive = 0, Path = path });
        DriveWireServerService service = new DriveWireServerService(options, new DriveWireDriveTable(), new DriveWireServerStatistics());

        for (int round = 0; round < 2; round++)
        {
            await service.StartAsync(CancellationToken.None);
            IPEndPoint endPoint = await service.WaitUntilListeningAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

            using (TcpClient client = new TcpClient())
            {
                await client.ConnectAsync(endPoint);
                (byte status, _) = await DriveWireTestClient.ReadExtendedAsync(client.GetStream(), 0, 0);
                Assert.That(status, Is.EqualTo(DriveWireError.None), $"round {round}");
                Assert.That(service.Drives.Get(0), Is.Not.Null);
            }

            await service.StopAsync(CancellationToken.None);

            Assert.That(service.Drives.Get(0), Is.Null, "stopping ejects the drives");
            using (FileStream exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.That(exclusive.Length, Is.EqualTo(400 * 256), "the image file was closed");
            }

            using TcpClient refused = new TcpClient();
            Assert.ThrowsAsync<SocketException>(async () => await refused.ConnectAsync(endPoint), "the port is closed");
        }

        Assert.That(service.Statistics.Connections, Is.EqualTo(2));
        Assert.That(service.Statistics.ActiveConnections, Is.Zero);
        service.Dispose();
    }

    [Test]
    public void ImageDrive_UnknownImage_IsNotMounted_AndTheServerKeepsTheOtherDrives()
    {
        string bad = WriteTemp(new byte[1000 * 256]);
        string good = WriteTemp(Os9VolumeBuilder.Build(400, false, out _));
        DriveWireServerOptions options = new DriveWireServerOptions { Port = 0 };
        options.Drives.Add(new DriveWireDriveOptions { Drive = 0, Path = bad });
        options.Drives.Add(new DriveWireDriveOptions { Drive = 1, Path = good });
        DriveWireServerService service = new DriveWireServerService(options, new DriveWireDriveTable(), new DriveWireServerStatistics());

        service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        service.WaitUntilListeningAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();

        Assert.That(service.Drives.Get(0), Is.Null, "an image no driver recognises leaves the drive empty");
        Assert.That(service.Drives.Get(1), Is.Not.Null);
        service.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        service.Dispose();
    }

    // ---------------------------------------------------------------- helpers

    // Reads OS9Boot the way boot_common.asm does: when DD.BSZ is not zero the boot file is DD.BSZ bytes from sector DD.BT on;
    // when it is zero DD.BT is the boot file's descriptor and the segment list (FD.SEG, 5 bytes each: 3 LSN, 2 count) gives the
    // sectors and FD.SIZ the size. Offsets from NitrOS-9 defs/rbf.d: DD.BT $15, DD.BSZ $18, FD.SIZ 9, FD.SEG $10.
    private static async Task<byte[]> ReadBootFileAsync(Stream stream, byte[] lsn0)
    {
        uint bootLsn = (uint)((lsn0[0x15] << 16) | (lsn0[0x16] << 8) | lsn0[0x17]);
        int bootSize = (lsn0[0x18] << 8) | lsn0[0x19];
        List<(uint Lsn, int Count)> segments = new List<(uint, int)>();
        if (bootSize != 0)
        {
            segments.Add((bootLsn, (bootSize + 255) / 256));
        }
        else
        {
            (byte status, byte[] fd) = await DriveWireTestClient.ReadExtendedAsync(stream, 0, bootLsn);
            Assert.That(status, Is.EqualTo(DriveWireError.None), "the boot file descriptor");
            bootSize = (fd[9] << 24) | (fd[10] << 16) | (fd[11] << 8) | fd[12];
            for (int at = 0x10; at + 5 <= 256; at += 5)
            {
                uint lsn = (uint)((fd[at] << 16) | (fd[at + 1] << 8) | fd[at + 2]);
                int count = (fd[at + 3] << 8) | fd[at + 4];
                if (lsn == 0 && count == 0)
                {
                    break;
                }

                segments.Add((lsn, count));
            }
        }

        byte[] boot = new byte[bootSize];
        int filled = 0;
        for (int s = 0; s < segments.Count && filled < bootSize; s++)
        {
            for (int k = 0; k < segments[s].Count && filled < bootSize; k++)
            {
                (byte status, byte[] sector) = await DriveWireTestClient.ReadExtendedAsync(stream, 0, segments[s].Lsn + (uint)k);
                Assert.That(status, Is.EqualTo(DriveWireError.None), $"boot sector {segments[s].Lsn + (uint)k}");
                int take = Math.Min(256, bootSize - filled);
                Array.Copy(sector, 0, boot, filled, take);
                filled += take;
            }
        }

        Assert.That(filled, Is.EqualTo(bootSize), "the segments cover the boot file");
        return boot;
    }

    private static string FindBeckerImage()
    {
        string? folder = Environment.GetEnvironmentVariable(ImagesVariable);
        if (string.IsNullOrEmpty(folder))
        {
            Assert.Ignore(ImagesVariable + " is not set, so there is no NitrOS-9 DriveWire image to serve");
        }

        if (!Directory.Exists(folder))
        {
            Assert.Ignore(ImagesVariable + " names a folder that does not exist: " + folder);
        }

        string preferred = Path.Combine(folder!, PreferredImage);
        if (File.Exists(preferred))
        {
            return preferred;
        }

        string[] found = Directory.GetFiles(folder!, "*_becker.dsk");
        if (found.Length == 0)
        {
            Assert.Ignore("No *_becker.dsk image in " + folder);
        }

        Array.Sort(found, StringComparer.OrdinalIgnoreCase);
        return found[0];
    }

    private string CopyToTemp(string source)
    {
        string path = Path.Combine(Path.GetTempPath(), "dw-" + Guid.NewGuid().ToString("N") + ".dsk");
        File.Copy(source, path);
        _tempFiles.Add(path);
        return path;
    }

    private string WriteTemp(byte[] content)
    {
        string path = Path.Combine(Path.GetTempPath(), "dw-" + Guid.NewGuid().ToString("N") + ".dsk");
        File.WriteAllBytes(path, content);
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>
    /// A <see cref="DriveWireServerService"/> on a free loopback port, stopped on dispose.
    /// </summary>
    private sealed class RunningServer : IAsyncDisposable
    {
        private RunningServer(DriveWireServerService service, IPEndPoint endPoint)
        {
            Service = service;
            EndPoint = endPoint;
        }

        public DriveWireServerService Service { get; }

        public IPEndPoint EndPoint { get; }

        public static async Task<RunningServer> StartAsync(DriveWireDriveOptions drive)
        {
            DriveWireServerOptions options = new DriveWireServerOptions { Port = 0 };
            options.Drives.Add(drive);
            DriveWireServerService service = new DriveWireServerService(options, new DriveWireDriveTable(), new DriveWireServerStatistics());
            await service.StartAsync(CancellationToken.None);
            IPEndPoint endPoint = await service.WaitUntilListeningAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(service.Drives.Get((byte)drive.Drive), Is.Not.Null, "the image was mounted: " + drive.Path);
            return new RunningServer(service, endPoint);
        }

        public async Task<TcpClient> ConnectAsync()
        {
            TcpClient client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(EndPoint);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            await Service.StopAsync(CancellationToken.None);
            Service.Dispose();
        }
    }
}
