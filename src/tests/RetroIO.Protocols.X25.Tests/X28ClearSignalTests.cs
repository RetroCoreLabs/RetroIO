//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Protocols;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The X.28 clear signal text (X.28 12/97 3.5.17.1 and Table 6): <c>CLR mnemonic C:cause D:diagnostic</c>.
    /// </summary>
    [TestFixture]
    public class X28ClearSignalTests
    {
        /// <summary>
        /// Every cause with a Table 6 mnemonic prints that mnemonic, the decimal cause and the decimal diagnostic.
        /// </summary>
        [TestCase((byte)0, "CLR DTE C:0 D:7")]
        [TestCase((byte)1, "CLR OCC C:1 D:7")]
        [TestCase((byte)3, "CLR INV C:3 D:7")]
        [TestCase((byte)5, "CLR NC C:5 D:7")]
        [TestCase((byte)9, "CLR DER C:9 D:7")]
        [TestCase((byte)11, "CLR NA C:11 D:7")]
        [TestCase((byte)13, "CLR NP C:13 D:7")]
        [TestCase((byte)17, "CLR RPE C:17 D:7")]
        [TestCase((byte)19, "CLR ERR C:19 D:7")]
        [TestCase((byte)25, "CLR RNA C:25 D:7")]
        [TestCase((byte)33, "CLR ID C:33 D:7")]
        [TestCase((byte)41, "CLR FNA C:41 D:7")]
        [TestCase((byte)57, "CLR SA C:57 D:7")]
        public void ClearSignal_KnownCause_HasMnemonic(byte cause, string expected)
        {
            Assert.That(X28.ClearSignal(cause, 7), Is.EqualTo(expected));
        }

        /// <summary>
        /// A cause with bit 8 set is "DTE originated" (the DTE's own code in the low bits): printed as DTE, cause 0.
        /// </summary>
        [Test]
        public void ClearSignal_DteOriginatedHighBit_IsDte()
        {
            Assert.That(X28.ClearSignal(0x85, 0), Is.EqualTo("CLR DTE C:0 D:0"));
        }

        /// <summary>
        /// A cause with no matching mnemonic prints only the numbers, never an invented mnemonic.
        /// </summary>
        [Test]
        public void ClearSignal_UnknownCause_HasNoMnemonic()
        {
            Assert.That(X28.ClearSignal(21, 3), Is.EqualTo("CLR C:21 D:3"));
            Assert.That(X28.ClearSignal(99, 0), Is.EqualTo("CLR C:99 D:0"));
        }

        /// <summary>
        /// Extended dialogue mode (X.28 (12/97) 3.5.17.1, Table 6): " - " and the cause's text; none for a cause
        /// without a mnemonic.
        /// </summary>
        [TestCase((byte)0x01, "CLR OCC C:1 D:0 - Call cleared, number busy")]
        [TestCase((byte)0x0D, "CLR NP C:13 D:0 - Call cleared, number not assigned")]
        [TestCase((byte)0x00, "CLR DTE C:0 D:0 - Call cleared, by remote device, data may be lost")]
        [TestCase((byte)0x05, "CLR NC C:5 D:0 - Call cleared, temporary network problem")]
        [TestCase((byte)21, "CLR C:21 D:0")]
        public void ClearSignal_Extended_AddsTheTable6Text(byte cause, string expected)
        {
            Assert.That(X28.ClearSignal(cause, 0, true), Is.EqualTo(expected));
        }

        /// <summary>X.28 3.5: extended dialogue mode when the high 4 bits of parameter 6 are not all zero; Table 7.</summary>
        [Test]
        public void ExtendedDialogue_IsTheHighFourBitsOfParameter6_AndConfirmationHasTheTable7Text()
        {
            Assert.That(X28.IsExtendedDialogue(1), Is.False);
            Assert.That(X28.IsExtendedDialogue(15), Is.False);
            Assert.That(X28.IsExtendedDialogue(16), Is.True);
            Assert.That(X28.IsExtendedDialogue(17), Is.True);
            Assert.That(X28.ClearConfirmation(false), Is.EqualTo("CLR CONF"));
            Assert.That(X28.ClearConfirmation(true), Is.EqualTo("CLR CONF - Call cleared, confirmed"));
        }

        /// <summary>
        /// STAT (X.28 3.5.11): FREE with no call, ENGAGED with one (also at the prompt after the escape, where the
        /// call stays), with the extended dialogue text when parameter 6 is 16 or more.
        /// </summary>
        [Test]
        public void Status_IsFreeOrEngaged_WithTheExtendedText()
        {
            var x3 = new X3();
            var pad = new X28(x3);
            Assert.That(pad.StatusSignal(), Is.EqualTo("FREE"));
            pad.SwitchToDataTransferMode();
            Assert.That(pad.StatusSignal(), Is.EqualTo("ENGAGED"));
            Assert.That(x3.SetParameter(6, 17), Is.True);
            Assert.That(pad.StatusSignal(), Is.EqualTo("ENGAGED - Call Established"));
            pad.SwitchToCommandMode();
            Assert.That(pad.StatusSignal(), Is.EqualTo("FREE - No Call Established"));
        }

        /// <summary>
        /// A remote clear delivered to the PAD with cause and diagnostic shows the signal on the terminal.
        /// </summary>
        [Test]
        public void PadClient_DeliverClear_WithCause_PrintsSignal()
        {
            PadClient pad = new PadClient(NullLogger.Instance, new NullTransport());
            StringBuilder seen = new StringBuilder();
            pad.DataReceived += (s, e) => seen.Append(e.Data);

            pad.DeliverClear(1, 9, 0);

            Assert.That(seen.ToString(), Does.Contain("\r\nCLR DER C:9 D:0\r\n"));
        }

        private sealed class NullTransport : IPadTransport
        {
            public bool PlaceCall(string calledAddress, string callingAddress, byte[]? callUserData) => true;
            public bool SendData(ushort lcn, byte[] data, bool mBit, bool qBit, bool dBit) => true;
            public void ForwardData(ushort lcn, byte[] data, int length) { }
            public void ClearCall(ushort lcn, byte cause, byte diagnostic) { }
        }
    }
}
