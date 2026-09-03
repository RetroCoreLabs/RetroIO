using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Codec;
using RetroIO.Protocols.Xmsg.Node.Seam;
using RetroIO.Protocols.Xmsg.Packet;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// A message that arrives split must reach a subscriber as ONE whole message, never as two
    /// halves.
    /// </summary>
    /// <remarks>
    /// <para><b>The defect this pins, measured live on 2026-08-11</b></para>
    /// <para>
    /// The layer raised its message event on the RAW packet and only rejoined fragments later,
    /// inside <c>XmsgNode.HandleFrames</c>. The file-access client subscribes to that event, so a
    /// pull was handed a first fragment and a continuation separately and had no way to use
    /// either. The read ladder ran perfectly - Reserve, OpenFile, SetBlockSize, ReadFile - D100
    /// sent the file, and the content went on the floor with no error anywhere.
    /// </para>
    /// <para><b>Real bytes, not built frames</b></para>
    /// <para>
    /// Both fragments below are what a real D100 put on the wire, copied verbatim out of
    /// <c>xmsg-run-E-pull-reaches-content.log</c>. A pair we built ourselves would only prove our
    /// splitter agrees with our joiner.
    /// </para>
    /// </remarks>
    public sealed class SplitMessageDeliveryTests
    {
        /// <summary>
        /// Swallows what the layer sends; these tests only look at what arrives.
        /// </summary>
        private sealed class NullTransport : IXmsgTransport
        {
            /// <summary>
            /// Discards the bytes.
            /// </summary>
            /// <param name="bytes">
            /// The information field.
            /// </param>
            public void Send(ReadOnlySpan<byte> bytes)
            {
            }
        }

        /// <summary>
        /// The first fragment of a 1032-byte file-content message: subtype <c>0x0A</c>, Flags 2
        /// carrying the TOTAL length <c>0x0408</c>, 594 bytes of body after the 28-byte head.
        /// </summary>
        private const string FirstFragmentHex =
            "2113000A4E1F0064002704088C30210082844E1F0212006406A0040807F0000286000001203030313620414243444546"
            + "4748494A4B4C4D4E4F505152535455565758595A20303132333435363738392054484520515549434B2042524F574E20"
            + "464F580A4C494E452030303137204142434445464748494A4B4C4D4E4F505152535455565758595A2030313233343536"
            + "3738392054484520515549434B2042524F574E20464F580A4C494E452030303138204142434445464748494A4B4C4D4E"
            + "4F505152535455565758595A20303132333435363738392054484520515549434B2042524F574E20464F580A4C494E45"
            + "2030303139204142434445464748494A4B4C4D4E4F505152535455565758595A20303132333435363738392054484520"
            + "515549434B2042524F574E20464F580A4C494E452030303230204142434445464748494A4B4C4D4E4F50515253545556"
            + "5758595A20303132333435363738392054484520515549434B2042524F574E20464F580A4C494E452030303231204142"
            + "434445464748494A4B4C4D4E4F505152535455565758595A20303132333435363738392054484520515549434B204252"
            + "4F574E20464F580A4C494E452030303232204142434445464748494A4B4C4D4E4F505152535455565758595A20303132"
            + "333435363738392054484520515549434B2042524F574E20464F580A4C494E452030303233204142434445464748494A"
            + "4B4C4D4E4F505152535455565758595A20303132333435363738392054484520515549434B2042524F574E20464F580A"
            + "4C494E452030303234204142434445464748494A4B4C4D4E4F505152535455565758595A20303132333435363738";

        /// <summary>
        /// Its continuation: subtype <c>0x0C</c>, the same Flags 1, Flags 2 carrying the byte
        /// OFFSET <c>0x0252</c> = 594 where it resumes, and no sub-header.
        /// </summary>
        private const string ContinuationHex =
            "2113000C4E1F0064002702528DE4392054484520515549434B2042524F574E20464F580A4C494E452030303235204142"
            + "434445464748494A4B4C4D4E4F505152535455565758595A20303132333435363738392054484520515549434B204252"
            + "4F574E20464F580A4C494E452030303236204142434445464748494A4B4C4D4E4F505152535455565758595A20303132"
            + "333435363738392054484520515549434B2042524F574E20464F580A4C494E452030303237204142434445464748494A"
            + "4B4C4D4E4F505152535455565758595A20303132333435363738392054484520515549434B2042524F574E20464F580A"
            + "4C494E452030303238204142434445464748494A4B4C4D4E4F505152535455565758595A203031323334353637383920"
            + "54484520515549434B2042524F574E20464F580A4C494E452030303239204142434445464748494A4B4C4D4E4F505152"
            + "535455565758595A20303132333435363738392054484520515549434B2042524F574E20464F580A4C494E4520303033"
            + "30204142434445464748494A4B4C4D4E4F505152535455565758595A2030313233343536373839205448452051554943"
            + "4B2042524F574E20464F580A4C494E4520303033";

        /// <summary>
        /// The whole message reaches the subscriber once, joined, and neither half reaches it
        /// alone.
        /// </summary>
        [Fact]
        public void ASplitMessageArrivesOnceAndWhole()
        {
            XmsgCodec codec = new XmsgCodec("test", new NullTransport());
            XmsgLayer layer = new XmsgLayer(codec, 19999, 0x00);

            List<XmsgFrame> received = new List<XmsgFrame>();
            layer.MessageReceived += delegate (string id, XmsgPacketInfo packet)
            {
                received.Add(packet.Frame);
            };

            int held = 0;
            layer.FragmentHeld += delegate (string id, XmsgPacketInfo packet)
            {
                held++;
            };

            codec.ProcessBytes(Convert.FromHexString(FirstFragmentHex));

            // Nothing yet: half a message is not a message.
            Assert.Empty(received);
            Assert.Equal(1, held);

            codec.ProcessBytes(Convert.FromHexString(ContinuationHex));

            Assert.Single(received);
            Assert.Equal(1, held);

            XmsgFrame whole = received[0];

            // It arrives as an ordinary data frame - that is what everything above is gated on.
            Assert.Equal(SintranPacketSubtype.Data, whole.Header!.Subtype);
            Assert.NotNull(whole.SubHeader);

            // 1032 bytes is one file-content message: 594 from the first fragment, 438 from the
            // continuation. Flags 2 on the first fragment declared exactly that total.
            byte[] body = whole.GetBodyBytes();
            Assert.Equal(1032, body.Length);

            byte[] firstBytes = Convert.FromHexString(FirstFragmentHex);
            byte[] contBytes = Convert.FromHexString(ContinuationHex);

            // The join is in the right order and loses nothing: the head is the first fragment's
            // body (everything past its 28-byte head) and the tail is the continuation's
            // (everything past its 14-byte header, which carries no sub-header).
            for (int i = 0; i < 594; i++)
            {
                Assert.Equal(firstBytes[28 + i], body[i]);
            }

            for (int i = 0; i < 438; i++)
            {
                Assert.Equal(contBytes[14 + i], body[594 + i]);
            }
        }

        /// <summary>
        /// The joined message still carries the file content it was sent for, readable as text.
        /// </summary>
        /// <remarks>
        /// The live log shows this exact message carrying the pushed test file, whose every line
        /// reads <c>LINE nnnn ABCDEFGHIJKLMNOPQRSTUVWXYZ 0123456789 THE QUICK BROWN FOX</c>. If the
        /// join were off by a fragment boundary the seam would land inside one of those lines.
        /// </remarks>
        [Fact]
        public void TheJoinedMessageCarriesTheFileContentAcrossTheSeam()
        {
            XmsgCodec codec = new XmsgCodec("test", new NullTransport());
            XmsgLayer layer = new XmsgLayer(codec, 19999, 0x00);

            byte[] body = Array.Empty<byte>();
            layer.MessageReceived += delegate (string id, XmsgPacketInfo packet)
            {
                body = packet.Frame.GetBodyBytes();
            };

            codec.ProcessBytes(Convert.FromHexString(FirstFragmentHex));
            codec.ProcessBytes(Convert.FromHexString(ContinuationHex));

            // Read the body as plain text. The content is ND ASCII with the parity bit clear here,
            // so a straight byte-to-char is enough for the check.
            char[] chars = new char[body.Length];
            for (int i = 0; i < body.Length; i++)
            {
                chars[i] = (char)body[i];
            }

            string text = new string(chars);

            // One line from each half: 0025 lands in the first fragment, 0030 in the continuation.
            // (0016 is NOT here - the message starts part-way through that line, which is why the
            // body opens with a bare "0016" and no "LINE".)
            Assert.Contains("LINE 0025", text);
            Assert.Contains("LINE 0030", text);

            // And the point of the whole exercise: a run that STRADDLES the seam. Byte 594 is the
            // last character the first fragment carried - the '9' below - so this thirty-character
            // run cannot be read from either half alone. If the join were off by so much as a byte
            // this is where it would show.
            Assert.Equal("0123456789 THE QUICK BROWN FOX", text.Substring(585, 30));
            Assert.Equal('9', text[594]);
        }
    }
}
