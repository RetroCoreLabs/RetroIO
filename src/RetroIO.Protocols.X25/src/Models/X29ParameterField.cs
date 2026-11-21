//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.X25.Models
{
    /// <summary>
    /// Builds and reads the parameter field of X.29 set, read, set and read, and parameter indication PAD messages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one place both X.29 message readers (<see cref="X29Messages"/> and the X29 protocol class) take the field
    /// apart, so the two cannot read it differently again. Until 2026-11-21 each had its own loop: one dropped every
    /// pair whose value it judged invalid, neither knew the error bit, the extension or the separator, and both read a
    /// read message as bare references (RetroIO BUGS 45).
    /// </para>
    /// <para>
    /// The rules, X.29 (12/97) 4.4.5:
    /// </para>
    ///  - A parameter field is a reference octet and a value octet.
    ///  - 4.4.5.1: the reference is in bits 7 to 1; 127 means another octet follows, holding "the parameter reference of
    ///    Recommendation X.3 minus 127".
    ///  - 4.4.5.2: "In PAD messages received by the PAD, bit 8 of each octet will be ignored. In parameter indication PAD
    ///    messages, bit 8 of each reference field set to 1 will indicate an invalid access to the referred parameter".
    ///  - 4.4.5.3: "Value fields in read PAD messages are coded as all binary 0s"; after an error bit the value is the
    ///    reason, Table 3.
    ///  - 4.4.5.4: reference 0 with value 0 is the parameter separator; what follows is national or local. No national
    ///    parameter is known here, so the reading stops there.
    /// </remarks>
    public static class X29ParameterField
    {
        /// <summary>
        /// The separator reference and value, X.29 4.4.5.4: both octets 00000000.
        /// </summary>
        private const byte Separator = 0x00;

        /// <summary>
        /// The reference that says the reference goes on in the next octet, X.29 4.4.5.1.
        /// </summary>
        private const int ExtensionReference = 127;

        /// <summary>
        /// Builds a read PAD message: the code, then each reference with a value of zero.
        /// </summary>
        /// <remarks>
        /// X.29 4.4.5.3: "Value fields in read PAD messages are coded as all binary 0s". No reference reads every
        /// parameter (Table 1: a read with no parameter field). A reference above 126 is written with the extension
        /// octet (4.4.5.1).
        /// </remarks>
        /// <param name="messageCode">
        /// The read message code, 0x04.
        /// </param>
        /// <param name="references">
        /// The X.3 references to read, in the order given.
        /// </param>
        /// <returns>
        /// The whole PAD message.
        /// </returns>
        public static byte[] BuildRead(byte messageCode, ReadOnlySpan<byte> references)
        {
            List<byte> message = new List<byte>(1 + (references.Length * 3)) { messageCode };
            for (int i = 0; i < references.Length; i++)
            {
                AddReference(message, references[i]);
                message.Add(0);
            }

            return message.ToArray();
        }

        /// <summary>
        /// Reads the parameter fields of a message from an offset to its end.
        /// </summary>
        /// <param name="data">
        /// The whole PAD message.
        /// </param>
        /// <param name="offset">
        /// Where the first parameter field starts: 1, after the message code.
        /// </param>
        /// <param name="isIndication">
        /// True for a parameter indication, where bit 8 of a reference is the error bit; false for the messages a PAD
        /// receives, where it is ignored (4.4.5.2).
        /// </param>
        /// <param name="references">
        /// Each reference in the order met, the error ones included.
        /// </param>
        /// <param name="values">
        /// Reference to value, for the parameters without the error bit.
        /// </param>
        /// <param name="inError">
        /// Reference to the Table 3 reason, for the parameters with the error bit.
        /// </param>
        /// <returns>
        /// True when the field was whole pairs; false when it ended in the middle of one (the last half pair is not
        /// read).
        /// </returns>
        public static bool Read(byte[] data, int offset, bool isIndication, List<byte> references, Dictionary<byte, byte> values, Dictionary<byte, byte> inError)
        {
            int at = offset;
            while (at < data.Length)
            {
                byte referenceOctet = data[at++];
                bool error = isIndication && (referenceOctet & 0x80) != 0;
                int reference = referenceOctet & 0x7F;
                if (reference == ExtensionReference)
                {
                    if (at >= data.Length)
                    {
                        return false;
                    }

                    reference = (data[at++] & 0x7F) + ExtensionReference;
                }

                if (at >= data.Length)
                {
                    return false;
                }

                byte value = data[at++];
                if (reference == Separator && value == Separator)
                {
                    // 4.4.5.4: national or local parameters follow; none is known here.
                    return true;
                }

                // Every X.3 reference fits a byte: the highest is 22, and 127 + 127 is the most the extension can say.
                byte key = (byte)Math.Min(reference, byte.MaxValue);
                references.Add(key);
                if (error)
                {
                    inError[key] = value;
                }
                else
                {
                    values[key] = value;
                }
            }

            return true;
        }

        /// <summary>
        /// Writes one reference octet, or two with the extension for a reference above 126 (4.4.5.1).
        /// </summary>
        /// <param name="message">
        /// The message being built.
        /// </param>
        /// <param name="reference">
        /// The X.3 reference.
        /// </param>
        private static void AddReference(List<byte> message, byte reference)
        {
            if (reference < ExtensionReference)
            {
                message.Add(reference);
                return;
            }

            message.Add(ExtensionReference);
            message.Add((byte)(reference - ExtensionReference));
        }
    }
}
