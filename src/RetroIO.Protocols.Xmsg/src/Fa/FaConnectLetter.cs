//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using System.Text;

namespace RetroIO.Protocols.Xmsg.Fa
{
    /// <summary>
    /// Builds the XSLET letter a client sends to open a conversation with <c>*FA-SERVER</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>The first thing a push has to send</b></para>
    /// <para>
    /// Everything else in the write ladder happens inside a conversation. This letter is what
    /// opens it, and the server answers with the <c>07D2</c> confirmation whose echoed word is
    /// then stamped on everything the server sends - see
    /// <see cref="FaServerConversation.ResponderConversation"/>.
    /// </para>
    /// <para><b>Captured shape</b></para>
    /// <para>
    /// From node 103's letter to node 102, <c>DOC/captures/ND-TO-ND-2026-08-08/nd-to-nd.pcapng</c>,
    /// and identical in shape in every other recording we hold:
    /// </para>
    /// <code>
    /// 1B        serial, echoed by the reply so a client can match it
    /// 41        service 0x41 = XSLET
    /// 0012      declared length of the parameter area, 18
    /// FF 0A     "*FA-SERVER"      string parameter 1, the server name
    /// FE 04     "D102"            string parameter 2, the DESTINATION system's name
    /// 07E2 0000 wwww 6400 A200 FF00    EXTRAS, past the declared length
    ///                                  wwww = the word the server echoes
    /// </code>
    /// <para><b>The system name is the DESTINATION, not the sender</b></para>
    /// <para>
    /// Every letter in every capture names the system the letter is going TO, which is what
    /// XROUT needs in order to find the server: 102 to 100 says <c>"D100"</c>, 100 to 102 says
    /// <c>"D102"</c>, 103 to 102 says <c>"D102"</c>, and 100 to 19999 says <c>"D19999"</c>.
    /// Reading the single 102-to-100 recording as "the sender names itself" fits that one frame
    /// and nothing else; a push built that way asks the remote for a server on OUR machine, and
    /// D100 answered it with a network error. Measured 2026-08-09.
    /// </para>
    /// <para><b>The extras are past the declared length, and that is not a mistake</b></para>
    /// <para>
    /// The XROUT length covers only the two string parameters. The bytes after it are carried
    /// anyway, and the server reads the third word of them - it is what comes back echoed in the
    /// confirmation. A builder that stops at the declared length produces a letter the server
    /// cannot answer properly.
    /// </para>
    /// <para>
    /// The trailing <c>6400</c> is the same constant that closes the confirmation; it is NOT a
    /// system number. See <see cref="FaExchangeCodec.ConfirmTrailingWord"/>.
    /// </para>
    /// </remarks>
    public static class FaConnectLetter
    {
        /// <summary>
        /// The XROUT service byte for a letter: XSLET.
        /// </summary>
        public const byte XsletService = 0x41;

        /// <summary>
        /// String parameter 1 is the server name.
        /// </summary>
        private const byte ServerNameTag = 0xFF;

        /// <summary>
        /// String parameter 2 is the system name.
        /// </summary>
        private const byte SystemNameTag = 0xFE;

        /// <summary>
        /// How many bytes the extras occupy after the declared length.
        /// </summary>
        /// <remarks>
        /// Six 16-bit words: <c>07E2 0000 wwww 6400 A200 FF00</c>. TWELVE bytes, not eleven -
        /// the sub-header's length word proves it, because it counts the whole body: a letter
        /// naming <c>"D102"</c> declares 0x0022 = 34 = 4 header + 18 strings + 12 extras, and one
        /// naming <c>"D19999"</c> declares 0x0024 = 36. We used to stop after the <c>FF</c>,
        /// which left the body an odd number of bytes on a machine that counts in words.
        /// </remarks>
        private const int ExtrasLength = 12;

        /// <summary>
        /// The byte that pads the extras out to a whole word.
        /// </summary>
        /// <remarks>
        /// Its value is NOT fixed on the wire: node 103 sent <c>FF00</c> and node 100 sent
        /// <c>FF92</c> for otherwise identical letters, so the low byte is whatever happened to
        /// be in the sender's buffer. We send zero.
        /// </remarks>
        private const byte ExtrasPadByte = 0x00;

        /// <summary>
        /// Builds the letter body: the XROUT header, the two names, and the extras.
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number. The reply echoes it, which is how a client matches an
        /// answer to its request.
        /// </param>
        /// <param name="serverName">
        /// The server to reach, normally <c>*FA-SERVER</c>.
        /// </param>
        /// <param name="systemName">
        /// The DESTINATION system's name - the machine the server runs on, for example
        /// <c>D100</c> when the letter is addressed to node 100. This is what XROUT looks the
        /// server up under. It is NOT our own name; see the remarks on the class.
        /// </param>
        /// <param name="requestWord">
        /// The word the server ECHOES in its confirmation, and therefore the word it will stamp
        /// on everything it sends afterwards. The client chooses it; it is not always
        /// <c>0x0002</c>, and treating it as a constant hung a live terminal.
        /// </param>
        /// <returns>
        /// The message body, ready to place in a datagram addressed to XROUT's port 0.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="serverName"/> or <paramref name="systemName"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when either name is empty or longer than 255 bytes.
        /// </exception>
        public static byte[] BuildBody(
            byte serial, string serverName, string systemName, ushort requestWord)
        {
            if (serverName == null)
            {
                throw new ArgumentNullException(nameof(serverName));
            }

            if (systemName == null)
            {
                throw new ArgumentNullException(nameof(systemName));
            }

            byte[] server = Encoding.ASCII.GetBytes(serverName);
            byte[] system = Encoding.ASCII.GetBytes(systemName);
            Check(server, nameof(serverName));
            Check(system, nameof(systemName));

            // The declared length covers ONLY the two tagged strings.
            int declared = 2 + server.Length + 2 + system.Length;

            byte[] body = new byte[XroutMessage.HeaderSize + declared + ExtrasLength];
            int at = 0;

            body[at++] = serial;
            body[at++] = XsletService;

            // Every 16-bit value goes through NdEndian.PutBe16, which names the byte order at
            // the call site and is a plain buffer/offset/value function that transcribes straight
            // to C. Open-coding the shift and mask would be a fourth copy in this method alone,
            // and it is what made the trailing constant a compile-time overflow: a cast to byte
            // folds before it truncates.
            NdEndian.PutBe16(body, at, (ushort)declared);
            at += 2;

            at = AppendString(body, at, ServerNameTag, server);
            at = AppendString(body, at, SystemNameTag, system);

            // The extras, carried PAST the declared length: 07E2, a zero word, the word the
            // server echoes back, the trailing constant, then A200 FF.
            NdEndian.PutBe16(body, at, 0x07E2);
            at += 2;
            NdEndian.PutBe16(body, at, 0x0000);
            at += 2;
            NdEndian.PutBe16(body, at, requestWord);
            at += 2;
            NdEndian.PutBe16(body, at, FaExchangeCodec.ConfirmTrailingWord);
            at += 2;
            NdEndian.PutBe16(body, at, 0xA200);
            at += 2;
            body[at++] = 0xFF;
            body[at++] = ExtrasPadByte;

            return body;
        }

        /// <summary>
        /// Writes one tagged string: the tag, a one-byte length, then the characters.
        /// </summary>
        /// <param name="body">
        /// The buffer being filled.
        /// </param>
        /// <param name="at">
        /// Where to write.
        /// </param>
        /// <param name="tag">
        /// The parameter tag.
        /// </param>
        /// <param name="value">
        /// The already-encoded characters.
        /// </param>
        /// <returns>
        /// The position after the string.
        /// </returns>
        /// <remarks>
        /// The length here is genuinely ONE byte, not a word - so there is no byte order to get
        /// wrong and nothing for <c>NdEndian</c> to do.
        /// </remarks>
        private static int AppendString(byte[] body, int at, byte tag, byte[] value)
        {
            body[at++] = tag;
            body[at++] = (byte)value.Length;

            for (int i = 0; i < value.Length; i++)
            {
                body[at++] = value[i];
            }

            return at;
        }

        /// <summary>
        /// Rejects a name that cannot be carried.
        /// </summary>
        /// <param name="value">
        /// The encoded name.
        /// </param>
        /// <param name="parameterName">
        /// The argument to blame.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when the name is empty or will not fit a one-byte length.
        /// </exception>
        private static void Check(byte[] value, string parameterName)
        {
            if (value.Length == 0)
            {
                throw new ArgumentException("A name cannot be empty.", parameterName);
            }

            if (value.Length > 0xFF)
            {
                throw new ArgumentException(
                    "A name of " + value.Length + " bytes does not fit one length byte.",
                    parameterName);
            }
        }
    }
}
