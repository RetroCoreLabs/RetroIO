//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Mvc;
using RetroIO.Management.Api.Controllers;
using RetroIO.Management.Contracts;
using RetroIO.Protocols.Xmsg.Hub;
using RetroIO.Protocols.Xmsg.Hub.Server;

namespace RetroIO.Management.Api.Tests.Controllers;

/// <summary>
/// The XMSG Ethernet hub endpoints: status, members, log, frames as JSON and as pcap, and the settings file.
/// </summary>
/// <remarks>
/// The hubs here listen on port 0. Port 5010 is in live use on the development machine.
/// </remarks>
[TestFixture]
public class XmsgHubControllerTests
{
    // The first frame of the LIST-NAMES capture of 2026-11-10: D102 (0x66) to D100 (0x64).
    private const string CaptureFrame =
        "0800266400000800266600000030A8A8030B022000231466067700222113000E0064006600110008DDFB210082940064098B006606D50008000000020900";

    private string _folder = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _folder = Path.Combine(Path.GetTempPath(), "retroio-xmsghub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Test]
    public void Status_NoHub_AnswersNotHosted_NotAnError()
    {
        XmsgHubController controller = new XmsgHubController(null, _folder);

        ApiResponse<XmsgHubStatusResponse> response = Data(controller.GetStatus());

        Assert.That(response.Data!.IsHosted, Is.False);
        Assert.That(response.Data.State, Is.EqualTo("Stopped"));
        Assert.That(Data(controller.GetMembers()).Data!.Members, Is.Empty);
        Assert.That(Data(controller.GetFrames()).Data!.Frames, Is.Empty);
        Assert.That(Data(controller.GetLog()).Data!.Lines, Is.Empty);
    }

    [Test]
    public void Pcap_NoHub_IsNotFound()
    {
        XmsgHubController controller = new XmsgHubController(null, _folder);

        Assert.That(controller.GetFramesPcap(), Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public void Settings_WithNoFile_AreTheDefaults_HubOff()
    {
        XmsgHubController controller = new XmsgHubController(null, _folder);

        XmsgHubSettingsDto settings = Data(controller.GetSettings()).Data!;

        Assert.That(settings.Enabled, Is.False);
        Assert.That(settings.Port, Is.EqualTo(5010));
        Assert.That(settings.BindAddress, Is.EqualTo("127.0.0.1"));
        Assert.That(settings.RestartRequired, Is.False);
    }

    [Test]
    public void Settings_AreWrittenToTheFile_AndReadBack()
    {
        XmsgHubController controller = new XmsgHubController(null, _folder);

        XmsgHubSettingsDto saved = Data(controller.PutSettings(new XmsgHubSettingsDto
        {
            Enabled = true,
            Port = 5011,
            BindAddress = "0.0.0.0",
            Uplink = "hub.example:5010",
            CaptureFile = "run.pcap",
            FrameBufferSize = 100,
            LogBufferSize = 50,
            StatusIntervalSeconds = 30,
        })).Data!;

        Assert.That(saved.RestartRequired, Is.True, "switched on while not running");
        Assert.That(File.Exists(Path.Combine(_folder, "xmsghub-config.json")), Is.True);

        XmsgHubSettingsDto read = Data(new XmsgHubController(null, _folder).GetSettings()).Data!;
        Assert.That(read.Enabled, Is.True);
        Assert.That(read.Port, Is.EqualTo(5011));
        Assert.That(read.BindAddress, Is.EqualTo("0.0.0.0"));
        Assert.That(read.Uplink, Is.EqualTo("hub.example:5010"));
        Assert.That(read.CaptureFile, Is.EqualTo("run.pcap"));
        Assert.That(read.FrameBufferSize, Is.EqualTo(100));
        Assert.That(read.LogBufferSize, Is.EqualTo(50));
        Assert.That(read.StatusIntervalSeconds, Is.EqualTo(30));
    }

    [TestCase(70000, "127.0.0.1", "")]
    [TestCase(5010, "not-an-address", "")]
    [TestCase(5010, "127.0.0.1", "no-port")]
    public void Settings_ThatCannotWork_AreRefused_AndNothingIsWritten(int port, string bind, string uplink)
    {
        XmsgHubController controller = new XmsgHubController(null, _folder);

        ActionResult<ApiResponse<XmsgHubSettingsDto>> result = controller.PutSettings(
            new XmsgHubSettingsDto { Port = port, BindAddress = bind, Uplink = uplink });

        Assert.That(result.Result, Is.InstanceOf<BadRequestObjectResult>());
        Assert.That(File.Exists(Path.Combine(_folder, "xmsghub-config.json")), Is.False);
    }

    [Test]
    public async Task ARunningHub_ShowsItsMembers_ItsFrames_AndExportsThemAsPcap()
    {
        XmsgHubService service = new XmsgHubService(new XmsgHubOptions { Port = 0, BindAddress = "127.0.0.1", FrameBufferSize = 64 });
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartAsync(timeout.Token);
        try
        {
            IPEndPoint endPoint = await service.WaitUntilListeningAsync(timeout.Token);
            using TcpClient d102 = Join(endPoint.Port);
            using TcpClient d100 = Join(endPoint.Port);
            Assert.That(SpinWait.SpinUntil(() => service.GetMembers().Length == 2, 5000), Is.True);

            byte[] frame = Convert.FromHexString(CaptureFrame);
            Send(d102, frame);
            Assert.That(SpinWait.SpinUntil(() => service.Frames.LastSequence == 1, 5000), Is.True);

            XmsgHubController controller = new XmsgHubController(service, _folder);

            XmsgHubStatusResponse status = Data(controller.GetStatus()).Data!;
            Assert.That(status.IsHosted, Is.True);
            Assert.That(status.State, Is.EqualTo("Running"));
            Assert.That(status.Members, Is.EqualTo(2));
            Assert.That(status.Machines, Is.EqualTo(2));
            Assert.That(status.FramesIn, Is.EqualTo(1));
            Assert.That(status.LastFrame, Is.EqualTo(1));

            XmsgHubMembersResponse members = Data(controller.GetMembers()).Data!;
            Assert.That(members.Members, Has.Count.EqualTo(2));
            Assert.That(members.Members[0].Kind, Is.EqualTo("Machine"));

            XmsgHubFramesResponse frames = Data(controller.GetFrames()).Data!;
            Assert.That(frames.Frames, Has.Count.EqualTo(1));
            XmsgHubFrameInfo info = frames.Frames[0];
            Assert.That(info.Hex, Is.EqualTo(CaptureFrame));
            Assert.That(info.SourceSystem, Is.EqualTo(102));
            Assert.That(info.DestinationSystem, Is.EqualTo(100));
            Assert.That(info.Outcome, Is.EqualTo("Forwarded"));
            Assert.That(info.CopiesSent, Is.EqualTo(1));
            Assert.That(frames.Gap, Is.False);

            Assert.That(Data(controller.GetFrames(after: 1)).Data!.Frames, Is.Empty);
            Assert.That(Data(controller.GetFrames(system: 102)).Data!.Frames, Has.Count.EqualTo(1));
            Assert.That(Data(controller.GetFrames(system: 103)).Data!.Frames, Is.Empty);

            FileStreamResult pcap = (FileStreamResult)controller.GetFramesPcap();
            using MemoryStream bytes = new MemoryStream();
            await pcap.FileStream.CopyToAsync(bytes);
            byte[] file = bytes.ToArray();
            Assert.That(pcap.ContentType, Is.EqualTo("application/vnd.tcpdump.pcap"));
            Assert.That(BitConverter.ToUInt32(file, 0), Is.EqualTo(0xA1B2C3D4u));
            Assert.That(file.Length, Is.EqualTo(24 + 16 + frame.Length));
            Assert.That(file.AsSpan(40).ToArray(), Is.EqualTo(frame));

            XmsgHubLogResponse log = Data(controller.GetLog()).Data!;
            Assert.That(log.Lines.Exists(l => l.Message.Contains("member joined", StringComparison.Ordinal)), Is.True);
            Assert.That(Data(controller.GetLog(after: log.LastLine)).Data!.Lines, Is.Empty);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public void Frames_ThatLeftTheBuffer_AreReportedAsAGap()
    {
        XmsgHubService service = new XmsgHubService(new XmsgHubOptions { FrameBufferSize = 2 });
        for (int i = 0; i < 5; i++)
        {
            service.Frames.Add(DateTime.UtcNow, "m", new byte[] { (byte)i }, HubFrameOutcome.Forwarded, 0, 0);
        }

        XmsgHubFramesResponse frames = Data(new XmsgHubController(service, _folder).GetFrames(after: 1)).Data!;

        Assert.That(frames.Gap, Is.True);
        Assert.That(frames.Frames[0].Sequence, Is.EqualTo(4));
    }

    private static ApiResponse<T> Data<T>(ActionResult<ApiResponse<T>> result)
    {
        OkObjectResult? ok = result.Result as OkObjectResult;
        Assert.That(ok, Is.Not.Null);
        return (ApiResponse<T>)ok!.Value!;
    }

    private static TcpClient Join(int port)
    {
        TcpClient client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        NetworkStream stream = client.GetStream();
        stream.Write(new byte[] { 0x52, 0x45, 0x54, 0x48, HubServer.RoleMachine });
        byte[] hello = new byte[5];
        int got = 0;
        while (got < 5)
        {
            got += stream.Read(hello, got, 5 - got);
        }

        return client;
    }

    private static void Send(TcpClient client, byte[] frame)
    {
        byte[] buffer = new byte[2 + frame.Length];
        buffer[0] = (byte)(frame.Length >> 8);
        buffer[1] = (byte)frame.Length;
        frame.CopyTo(buffer, 2);
        client.GetStream().Write(buffer);
    }
}
