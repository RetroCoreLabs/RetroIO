//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Diagnostics;
using System.Threading;

using NUnit.Framework;

using SharpPcap;

namespace RetroIO.Network.Pcap.Tests
{
    /// <summary>
    /// A hand measurement on a real host: how long an answer from this computer takes to come
    /// back through Npcap on the ND-Loopback adapter, with Npcap's "minimum bytes to copy"
    /// left at the driver's own value and with it set to 0. Sends ARP requests for
    /// 192.168.199.1 (the adapter's own address) from a made-up guest address and times each
    /// reply. Changes nothing on the host: the capture setting belongs to this test's own
    /// capture handle.
    /// </summary>
    [TestFixture]
    public sealed class PcapMinToCopyMeasurementTests
    {
        [Test]
        [Explicit("hand measurement: needs Npcap and the ND-Loopback adapter (192.168.199.1)")]
        public void ArpReplyTime_OnNdLoopback_DriverDefault_Versus_MinToCopyZero()
        {
            CaptureDeviceList devices = CaptureDeviceList.Instance;
            PcapAdapter[] adapters = new PcapAdapter[devices.Count];
            System.Net.NetworkInformation.NetworkInterface[] nics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            string[] ids = new string[nics.Length];
            string[] names = new string[nics.Length];
            for (int i = 0; i < nics.Length; i++)
            {
                ids[i] = nics[i].Id;
                names[i] = nics[i].Name;
            }
            for (int i = 0; i < devices.Count; i++)
            {
                adapters[i] = new PcapAdapter(devices[i].Name, devices[i].Description, PcapAdapterChoice.ConnectionNameFor(devices[i].Name, ids, names), Array.Empty<string>());
            }
            int index = PcapAdapterChoice.Choose(adapters, PcapAdapterChoice.DefaultConnectionName, out string reason);
            Assert.That(index, Is.GreaterThanOrEqualTo(0), reason);
            ILiveDevice device = devices[index];

            double before = Measure(device, null);
            double after = Measure(device, 0);
            TestContext.Out.WriteLine("ARP reply through Npcap on ND-Loopback, average of 20: driver default "
                + before.ToString("F2") + " ms, mintocopy 0 " + after.ToString("F2") + " ms");
        }

        [Test]
        [Explicit("hand check: needs Npcap and the ND-Loopback adapter")]
        public void Backend_WithNoAdapterNamed_TakesNdLoopback_AndSaysWhy()
        {
            System.Collections.Generic.IReadOnlyList<string> labels = PcapEthernetBackend.ListDeviceLabels();
            for (int i = 0; i < labels.Count; i++)
            {
                TestContext.Out.WriteLine("label: " + labels[i]);
            }
            PcapEthernetBackend backend = new PcapEthernetBackend();
            backend.Start();
            try
            {
                string[] status = backend.DescribeStatus();
                for (int i = 0; i < status.Length; i++)
                {
                    TestContext.Out.WriteLine(status[i]);
                }
                Assert.That(status[1], Does.Contain("ND-Loopback"));
                Assert.That(status[0], Does.Contain("KM-TEST"));
            }
            finally
            {
                backend.Stop();
            }
        }

        // Average milliseconds from sending an ARP request to capturing its reply, over 20.
        private static double Measure(ILiveDevice device, int? minToCopy)
        {
            DeviceConfiguration config = new DeviceConfiguration { Mode = DeviceModes.Promiscuous, ReadTimeout = 100 };
            if (minToCopy.HasValue)
            {
                config.MinToCopy = minToCopy.Value;
            }
            device.Open(config);
            device.Filter = "arp and ether dst 02:00:00:00:00:77";
            long replyAt = 0;
            using AutoResetEvent got = new AutoResetEvent(false);
            device.OnPacketArrival += Arrival;
            device.StartCapture();
            try
            {
                byte[] request = ArpRequest();
                double total = 0;
                int n = 0;
                for (int i = 0; i < 20; i++)
                {
                    long sent = Stopwatch.GetTimestamp();
                    device.SendPacket(request);
                    if (got.WaitOne(1000))
                    {
                        total += (Interlocked.Read(ref replyAt) - sent) * 1000.0 / Stopwatch.Frequency;
                        n++;
                    }
                    Thread.Sleep(50);
                }
                Assert.That(n, Is.GreaterThan(0), "no ARP reply came back");
                return total / n;
            }
            finally
            {
                device.StopCapture();
                device.OnPacketArrival -= Arrival;
                device.Close();
            }

            void Arrival(object sender, PacketCapture e)
            {
                Interlocked.Exchange(ref replyAt, Stopwatch.GetTimestamp());
                got.Set();
            }
        }

        // ARP request: who has 192.168.199.1, tell 192.168.199.77 (02:00:00:00:00:77).
        private static byte[] ArpRequest()
        {
            byte[] f = new byte[60];
            for (int i = 0; i < 6; i++) f[i] = 0xFF;
            byte[] mac = { 0x02, 0x00, 0x00, 0x00, 0x00, 0x77 };
            Buffer.BlockCopy(mac, 0, f, 6, 6);
            f[12] = 0x08; f[13] = 0x06;
            f[14] = 0x00; f[15] = 0x01; f[16] = 0x08; f[17] = 0x00; f[18] = 6; f[19] = 4; f[20] = 0x00; f[21] = 0x01;
            Buffer.BlockCopy(mac, 0, f, 22, 6);
            f[28] = 192; f[29] = 168; f[30] = 199; f[31] = 77;
            f[38] = 192; f[39] = 168; f[40] = 199; f[41] = 1;
            return f;
        }
    }
}
