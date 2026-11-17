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
    /// The X.28 NUI ON and NUI OFF PAD command signals (X.28 5.2) and the NUI string rules (3.5.15.1.1).
    /// </summary>
    [TestFixture]
    public class X28NuiTests
    {
        private static (X28 Pad, StringBuilder Echoed, StringBuilder Replies) NewPad()
        {
            X28 pad = new X28(new X3());
            StringBuilder echoed = new StringBuilder();
            StringBuilder replies = new StringBuilder();
            pad.CharacterEcho += (s, e) => echoed.Append((char)e.Character);
            pad.CommandResponse += (s, e) => replies.Append(e.Response);
            return (pad, echoed, replies);
        }

        private static void Type(X28 pad, string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                pad.ProcessCharacter(text[i]);
            }
        }

        [Test]
        public void NuiOn_KeepsTheNui_AndTheNuiIsNotEchoed()
        {
            (X28 pad, StringBuilder echoed, StringBuilder replies) = NewPad();

            Type(pad, "IDsecret7\r");

            // 5.2 NOTE 1: "The characters that follow the characters <ID> ... should not be echoed."
            Assert.That(echoed.ToString(), Is.EqualTo("ID\r\n"));
            Assert.That(pad.Nui, Is.EqualTo("secret7"), "the NUI keeps its case");
            Assert.That(replies.ToString(), Is.Empty, "X.28 defines no service signal in answer");
        }

        [Test]
        public void NuiOff_EndsIt()
        {
            (X28 pad, _, _) = NewPad();
            Type(pad, "ID1234\r");
            Type(pad, "IDOFF\r");
            Assert.That(pad.Nui, Is.Null);
        }

        [Test]
        public void ANuiGivenAtStart_ActsAsIfIdWasTyped_AndIdOffEndsIt()
        {
            (X28 pad, _, _) = NewPad();
            Assert.That(pad.SetNui("card 7"), Is.True);
            Assert.That(pad.Nui, Is.EqualTo("card7"), "a space is discarded, as when typed");

            Assert.That(pad.SetNui("a-b"), Is.False, "'-' may not be in a NUI");
            Assert.That(pad.SetNui("OFF"), Is.False, "X.28 5.2 NOTE 2: OFF is not a NUI");
            Assert.That(pad.SetNui(""), Is.False);
            Assert.That(pad.Nui, Is.EqualTo("card7"), "a refused one leaves the NUI as it was");

            Type(pad, "IDOFF\r");
            Assert.That(pad.Nui, Is.Null);
        }

        [Test]
        public void SpacesAndControlCharacters_AreDiscardedFromTheNui()
        {
            // 3.5.15.1.1 NOTE 3: columns 0 and 1 (except CR), DEL and SP "will not form part of the NUI string".
            Assert.That(X28.TryReadNui(" 12\u000134 5", out string nui), Is.True);
            Assert.That(nui, Is.EqualTo("12345"));
        }

        [TestCase("12-34")]
        [TestCase("12,34")]
        [TestCase("12+34")]
        public void ACharacterNotAllowedInANui_IsAnError(string typed)
        {
            (X28 pad, _, StringBuilder replies) = NewPad();
            Type(pad, "ID" + typed + "\r");
            Assert.That(pad.Nui, Is.Null);
            Assert.That(replies.ToString(), Is.EqualTo("ERR\r\n"), "ERR: a PAD command signal is in error");
        }

        private sealed class FacilityTransport : IPadTransport
        {
            public byte[]? Facilities;

            public bool PlaceCall(string calledAddress, string callingAddress, byte[]? callUserData)
            {
                Facilities = System.Array.Empty<byte>();
                return true;
            }

            public bool PlaceCall(string calledAddress, string callingAddress, byte[]? callUserData, byte[] facilities)
            {
                Facilities = facilities;
                return true;
            }

            public bool SendData(ushort lcn, byte[] data, bool mBit, bool qBit, bool dBit) => true;
            public void ForwardData(ushort lcn, byte[] data, int length) { }
            public void ClearCall(ushort lcn, byte cause, byte diagnostic) { }
        }

        private sealed class OldTransport : IPadTransport
        {
            public bool PlaceCall(string calledAddress, string callingAddress, byte[]? callUserData) => true;
            public bool SendData(ushort lcn, byte[] data, bool mBit, bool qBit, bool dBit) => true;
            public void ForwardData(ushort lcn, byte[] data, int length) { }
            public void ClearCall(ushort lcn, byte cause, byte diagnostic) { }
        }

        [Test]
        public void ACallAfterId_CarriesTheNuiSelectionFacility()
        {
            FacilityTransport t = new FacilityTransport();
            PadClient pad = new PadClient(NullLogger.Instance, t);
            Type(pad.X28Protocol, "IDab1\r");

            Assert.That(pad.Connect("23420001"), Is.True);

            // X.25 7.1: 0xC6 is a class D code, so a length octet, then the identifier "ab1".
            Assert.That(t.Facilities, Is.EqualTo(new byte[] { 0xC6, 3, (byte)'a', (byte)'b', (byte)'1' }));
        }

        [Test]
        public void ACallWithNoNui_CarriesNoFacility()
        {
            FacilityTransport t = new FacilityTransport();
            PadClient pad = new PadClient(NullLogger.Instance, t);
            Assert.That(pad.Connect("23420001"), Is.True);
            Assert.That(t.Facilities, Is.Empty);
        }

        [Test]
        public void ATransportThatCannotSendFacilities_RefusesLoudly_RatherThanDropTheNui()
        {
            PadClient pad = new PadClient(NullLogger.Instance, new OldTransport());
            Type(pad.X28Protocol, "ID99\r");
            Assert.Throws<System.NotSupportedException>(() => pad.Connect("23420001"));
        }

        [Test]
        public void NuiFacilityRequest_InTheSelection_IsNotEchoed_AndGoesInTheCall()
        {
            FacilityTransport t = new FacilityTransport();
            PadClient pad = new PadClient(NullLogger.Instance, t);
            StringBuilder echoed = new StringBuilder();
            pad.X28Protocol.CharacterEcho += (s, e) => echoed.Append((char)e.Character);

            Type(pad.X28Protocol, "Nxy9-23420001\r");

            // 3.5.15.1.1 NOTE 1: echo off "for the duration of the NUI facility request"; N and the dash are echoed.
            Assert.That(echoed.ToString(), Does.StartWith("N-23420001"));
            Assert.That(t.Facilities, Is.EqualTo(new byte[] { 0xC6, 3, (byte)'x', (byte)'y', (byte)'9' }));
            Assert.That(pad.X28Protocol.Nui, Is.Null, "a selection NUI is for that call only");
        }

        [Test]
        public void SelectionNui_IsUsedInsteadOfTheIdNui()
        {
            FacilityTransport t = new FacilityTransport();
            PadClient pad = new PadClient(NullLogger.Instance, t);
            Type(pad.X28Protocol, "IDaaa\r");
            Type(pad.X28Protocol, "CALL Nbb-23420001\r");
            Assert.That(t.Facilities, Is.EqualTo(new byte[] { 0xC6, 2, (byte)'b', (byte)'b' }));
        }

        [Test]
        public void AnUnknownFacilityRequest_IsAnError_AndNoCallIsMade()
        {
            FacilityTransport t = new FacilityTransport();
            PadClient pad = new PadClient(NullLogger.Instance, t);
            StringBuilder replies = new StringBuilder();
            pad.X28Protocol.CommandResponse += (s, e) => replies.Append(e.Response);
            Type(pad.X28Protocol, "Nab,R-23420001\r");
            Assert.That(replies.ToString(), Is.EqualTo("ERR\r\n"));
            Assert.That(t.Facilities, Is.Null);
        }

        [Test]
        public void IdAlone_IsAnError_ThisPadDoesNotPrompt()
        {
            (X28 pad, _, StringBuilder replies) = NewPad();
            Type(pad, "ID\r");
            Assert.That(replies.ToString(), Is.EqualTo("ERR\r\n"));
        }
    }
}
