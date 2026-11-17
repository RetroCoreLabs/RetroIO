//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// PVCs on a real HDLC line: two packet layers over LAPB over TCP (RetroX25 PLAN C2, the part loopback ports cannot
    /// show). The PVC channels 100..104 lie outside the pair's call channels 1..16 (X.25 Annex A).
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class HdlcPvcTests
    {
        private const ushort FirstPvc = 100;
        private const int PvcCount = 5;

        private static void AddPvcs(TrunkPair pair)
        {
            for (int i = 0; i < PvcCount; i++)
            {
                Assert.That(pair.Dce.AddPvc((ushort)(FirstPvc + i)), Is.True);
                Assert.That(pair.Dte.AddPvc((ushort)(FirstPvc + i)), Is.True);
            }
        }

        private static int CountOn(List<ReceivedData> list, ushort lci)
        {
            int n = 0;
            lock (list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].Lci == lci)
                    {
                        n++;
                    }
                }
            }

            return n;
        }

        [Test]
        public void FivePvcsAndACall_ShareOneLine_EachCarryingItsOwnData()
        {
            using TrunkPair pair = TrunkPair.Create();
            AddPvcs(pair);
            pair.WaitBothUp();

            ushort call = pair.Call(pair.Dte);

            // X.25 4.2: no call set-up on a PVC; data can be sent at once, both ways.
            for (int i = 0; i < PvcCount; i++)
            {
                ushort lci = (ushort)(FirstPvc + i);
                TrunkPair.SendAll(pair.Dte, lci, new byte[] { (byte)i, 1 });
                TrunkPair.SendAll(pair.Dce, lci, new byte[] { (byte)i, 2 });
            }

            TrunkPair.SendAll(pair.Dte, call, new byte[] { 0xCA });

            for (int i = 0; i < PvcCount; i++)
            {
                ushort lci = (ushort)(FirstPvc + i);
                Assert.That(SpinWait.SpinUntil(() => CountOn(pair.AtDce, lci) == 1 && CountOn(pair.AtDte, lci) == 1, 5000),
                    Is.True, "PVC " + lci + " carried one packet each way");
            }

            Assert.That(SpinWait.SpinUntil(() => CountOn(pair.AtDce, call) == 1, 5000), Is.True, "the call works beside them");
            Assert.That(TrunkPair.Count(pair.DceOut, X25PacketType.ResetRequest) + TrunkPair.Count(pair.DteOut, X25PacketType.ResetRequest),
                Is.EqualTo(0), "nothing was reset");
        }

        [Test]
        public void ALostLine_ResetsThePvcsAndKeepsThem_AndTheyCarryDataAfterwards()
        {
            using TrunkPair pair = TrunkPair.Create(rule: f => FrameFate.Pass);
            AddPvcs(pair);
            pair.WaitBothUp();
            TrunkPair.SendAll(pair.Dte, FirstPvc, new byte[] { 1 });
            Assert.That(SpinWait.SpinUntil(() => CountOn(pair.AtDce, FirstPvc) == 1, 5000), Is.True);

            pair.Proxy!.DropConnections();
            Assert.That(SpinWait.SpinUntil(() => !pair.Dce.PeerConnected && !pair.Dte.PeerConnected, 5000), Is.True);

            // The line comes back by itself (the DTE redials); a restart resets every PVC (X.25 4.5) and keeps it.
            pair.WaitBothUp(20000);
            Assert.That(pair.ClearedAtDce.Count + pair.ClearedAtDte.Count, Is.EqualTo(0), "a PVC is never cleared");

            TrunkPair.SendAll(pair.Dte, FirstPvc, new byte[] { 2 });
            TrunkPair.SendAll(pair.Dce, (ushort)(FirstPvc + 4), new byte[] { 3 });
            Assert.That(SpinWait.SpinUntil(() => CountOn(pair.AtDce, FirstPvc) == 2, 5000), Is.True,
                "the PVC carries data again, its sequence numbers back at zero on both sides");
            Assert.That(SpinWait.SpinUntil(() => CountOn(pair.AtDte, (ushort)(FirstPvc + 4)) == 1, 5000), Is.True);
        }
    }
}
