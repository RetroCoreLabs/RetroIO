//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg;

namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// Builds and takes apart a letter (XSLET) as XROUT forwards it: the letter header first, then
    /// the sender's own data.
    /// </summary>
    /// <remarks>
    /// <para><b>XROUT forwards the whole message, header included</b></para>
    /// ND-60.164.3 EN COSMOS Programmer Guide, appendix B, "XROUT Message Format": byte 0 is the
    /// serial number, byte 1 the service (XSLET = 65), bytes 2-3 the length of the parameter
    /// blocks that follow. "The remainder of the message will not be looked at by XROUT", so the
    /// sender's own data starts at <c>4 + that length</c>, and the server that receives the letter
    /// finds it there.
    /// <para><b>Measured</b></para>
    /// The SINTRAN chat server (NDInsight <c>SINTRAN/XMSG/SINTRAN-CHAT/CHATSV.PLNC</c>, receive
    /// loop) reads its data at exactly that offset. Its 25-byte join, measured on D100 on
    /// 2026-08-18: <c>123 65 0 12 | 255 10 'CHAT-LOBBY' | 1 5 'RONNY' 0 0</c>, data at 16.
    /// Until 2026-11-10 the in-process <see cref="XroutDirectory"/> handed the server the data
    /// alone, which no SINTRAN server ever receives.
    /// </remarks>
    public static class XroutLetter
    {
        /// <summary>
        /// Builds a letter to a name: the XSLET header with the name as string parameter 1, then
        /// the data.
        /// </summary>
        /// <param name="serial">
        /// The sender's serial number, echoed back by XROUT on a reply.
        /// </param>
        /// <param name="name">
        /// The port or connection name the letter is for.
        /// </param>
        /// <param name="userData">
        /// The sender's own data, which XROUT does not look at.
        /// </param>
        /// <returns>
        /// The whole letter, as the receiving server reads it.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="name"/> is null.
        /// </exception>
        public static byte[] Build(byte serial, string name, ReadOnlySpan<byte> userData)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            byte[] header = XroutRequests.SendLetter(serial, name, null, null)
                .ToArray(XroutMessageFraming.WithHeader);

            byte[] letter = new byte[header.Length + userData.Length];
            header.CopyTo(letter, 0);
            userData.CopyTo(new Span<byte>(letter, header.Length, userData.Length));
            return letter;
        }

        /// <summary>
        /// Finds where the sender's data starts in a letter that XROUT forwarded.
        /// </summary>
        /// <param name="letter">
        /// The letter as received, header first.
        /// </param>
        /// <returns>
        /// The offset of the data: <c>4 + the header's length word</c>. It equals or passes the
        /// letter's length when the letter carries no data. -1 when the letter is shorter than the
        /// four header bytes.
        /// </returns>
        public static int UserDataOffset(ReadOnlySpan<byte> letter)
        {
            if (letter.Length < XroutMessage.HeaderSize)
            {
                return -1;
            }

            return XroutMessage.HeaderSize + ((letter[2] << 8) | letter[3]);
        }

        /// <summary>
        /// Reads the name a letter is addressed to: string parameter 1 of its header.
        /// </summary>
        /// <param name="letter">
        /// The letter, header first.
        /// </param>
        /// <param name="name">
        /// The name, or an empty string when there is none.
        /// </param>
        /// <returns>
        /// True when the letter has a string parameter 1.
        /// </returns>
        public static bool TryReadName(ReadOnlySpan<byte> letter, out string name)
        {
            name = string.Empty;
            if (letter.Length < XroutMessage.HeaderSize)
            {
                return false;
            }

            XroutMessage message = XroutMessage.Parse(letter, XroutMessageFraming.WithHeader);
            for (int i = 0; i < message.Parameters.Count; i++)
            {
                XroutParameter parameter = message.Parameters[i];
                if (parameter.ParameterNumber == 1 && parameter.IsString)
                {
                    name = parameter.AsText();
                    return true;
                }
            }

            return false;
        }
    }
}
