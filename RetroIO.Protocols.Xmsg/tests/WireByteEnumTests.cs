using RetroIO.Protocols.Xmsg;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Proves the [Flags] wire-byte enums (role send-options, frame-flags) produce the exact on-wire hex
    /// values observed in the captures, both for individual bits and for the canonical combinations. This
    /// is the "double check the enum values actually generate the expected hex" guard.
    /// </summary>
    public sealed class WireByteEnumTests
    {
        /// <summary>
        /// Each XmsgSendOptions bit maps to its documented power-of-two value.
        /// </summary>
        [Fact]
        public void SendOptions_Bits_MatchHex()
        {
            Assert.Equal(0x00, (byte)XmsgSendOptions.None);
            Assert.Equal(0x01, (byte)XmsgSendOptions.Tcm);
            Assert.Equal(0x02, (byte)XmsgSendOptions.Secure);
            Assert.Equal(0x04, (byte)XmsgSendOptions.RoutedLetter);
            Assert.Equal(0x08, (byte)XmsgSendOptions.Forward);
            Assert.Equal(0x10, (byte)XmsgSendOptions.Bounce);
            Assert.Equal(0x20, (byte)XmsgSendOptions.HighPriority);
            Assert.Equal(0x40, (byte)XmsgSendOptions.WakeOnStatus);
            Assert.Equal(0x80, (byte)XmsgSendOptions.WaitForTransfer);
        }

        /// <summary>
        /// Every observed role byte is reproduced by OR-ing the documented option bits.
        /// </summary>
        [Fact]
        public void SendOptions_ObservedRoles_Compose()
        {
            // Connect letter 0xE4.
            Assert.Equal(0xE4, (byte)(XmsgSendOptions.WaitForTransfer | XmsgSendOptions.WakeOnStatus
                | XmsgSendOptions.HighPriority | XmsgSendOptions.RoutedLetter));
            // Asker data 0x84.
            Assert.Equal(0x84, (byte)(XmsgSendOptions.WaitForTransfer | XmsgSendOptions.RoutedLetter));
            // Asker control 0x94.
            Assert.Equal(0x94, (byte)(XmsgSendOptions.WaitForTransfer | XmsgSendOptions.Bounce
                | XmsgSendOptions.RoutedLetter));
            // Host letter 0x40.
            Assert.Equal(0x40, (byte)XmsgSendOptions.WakeOnStatus);
            // XSGSY reply 0x60.
            Assert.Equal(0x60, (byte)(XmsgSendOptions.WakeOnStatus | XmsgSendOptions.HighPriority));
            // XSGSY request 0xC4.
            Assert.Equal(0xC4, (byte)(XmsgSendOptions.WaitForTransfer | XmsgSendOptions.WakeOnStatus
                | XmsgSendOptions.RoutedLetter));
            // 0xFD notify 0x54 - the anomaly: routed+bounce+wake, NOT an asker frame.
            Assert.Equal(0x54, (byte)(XmsgSendOptions.WakeOnStatus | XmsgSendOptions.Bounce
                | XmsgSendOptions.RoutedLetter));
        }

        /// <summary>
        /// Each XmsgFrameFlags bit maps to its documented power-of-two value.
        /// </summary>
        [Fact]
        public void FrameFlags_Bits_MatchHex()
        {
            Assert.Equal(0x02, (byte)XmsgFrameFlags.Marker01);
            Assert.Equal(0x04, (byte)XmsgFrameFlags.Letter);
            Assert.Equal(0x10, (byte)XmsgFrameFlags.DataPhase);
            Assert.Equal(0x80, (byte)XmsgFrameFlags.SystemMode);
        }

        /// <summary>
        /// The canonical frame-flags combos equal the exact observed bytes.
        /// </summary>
        [Fact]
        public void FrameFlags_Combos_MatchHex()
        {
            Assert.Equal(0x86, (byte)XmsgFrameFlags.Setup);
            Assert.Equal(0x82, (byte)XmsgFrameFlags.ControlBare);
            Assert.Equal(0x96, (byte)XmsgFrameFlags.DataA);
            Assert.Equal(0x92, (byte)XmsgFrameFlags.DataB);
        }

        /// <summary>
        /// Every shared bit of the MON 200B option word and the wire role byte stays in lockstep.
        /// </summary>
        /// <remarks>
        /// XmsgOption (16-bit T-register word, generated from the official constants) and
        /// XmsgSendOptions (the sub-header role byte) describe ONE bit set in two encodings. This
        /// test is the anti-drift guard: if either enum is edited, the shift relation must still hold.
        /// XFSYS (bit 7) is deliberately excluded - it is a call-mode flag that never reaches the wire.
        /// </remarks>
        [Fact]
        public void OptionWord_And_RoleByte_StayInLockstep()
        {
            Assert.Equal(XmsgSendOptions.Tcm, XmsgOptionConversion.ToRoleByte(XmsgOption.XFTCM));
            Assert.Equal(XmsgSendOptions.Secure, XmsgOptionConversion.ToRoleByte(XmsgOption.XFSEC));
            Assert.Equal(XmsgSendOptions.RoutedLetter, XmsgOptionConversion.ToRoleByte(XmsgOption.XFROU));
            Assert.Equal(XmsgSendOptions.Forward, XmsgOptionConversion.ToRoleByte(XmsgOption.XFFWD));
            Assert.Equal(XmsgSendOptions.Bounce, XmsgOptionConversion.ToRoleByte(XmsgOption.XFBNC));
            Assert.Equal(XmsgSendOptions.HighPriority, XmsgOptionConversion.ToRoleByte(XmsgOption.XFHIP));
            Assert.Equal(XmsgSendOptions.WakeOnStatus, XmsgOptionConversion.ToRoleByte(XmsgOption.XFWAK));
            Assert.Equal(XmsgSendOptions.WaitForTransfer, XmsgOptionConversion.ToRoleByte(XmsgOption.XFWTF));

            // The observed connect-letter role 0xE4 widens back to the exact option word.
            XmsgSendOptions connect = XmsgSendOptions.WaitForTransfer | XmsgSendOptions.WakeOnStatus
                | XmsgSendOptions.HighPriority | XmsgSendOptions.RoutedLetter;
            Assert.Equal(0xE4, (byte)connect);
            Assert.Equal(
                XmsgOption.XFWTF | XmsgOption.XFWAK | XmsgOption.XFHIP | XmsgOption.XFROU,
                XmsgOptionConversion.FromRoleByte(connect));

            // XFSYS is below the role byte and is dropped on the way out.
            Assert.Equal(XmsgSendOptions.None, XmsgOptionConversion.ToRoleByte(XmsgOption.XFSYS));
        }
    }
}
