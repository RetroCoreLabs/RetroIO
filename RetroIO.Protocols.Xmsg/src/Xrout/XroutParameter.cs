using System;
using System.Text;

namespace RetroIO.Protocols.Xmsg
{
    /// <summary>
    /// A single TLV parameter block of an XROUT standard message.
    /// </summary>
    /// <remarks>
    /// <para><b>Type byte encoding</b></para>
    /// The first byte of a parameter block combines the parameter number and its
    /// type (XMSG-API.md section 4.2):
    ///  - <c>0</c> is a fill/skip byte used for even-byte alignment, not a parameter.
    ///  - a positive value is an integer parameter whose number is that value.
    ///  - a negative value is a string parameter whose number is the two's-complement
    ///    of the byte (for example <c>0xFF</c> = -1 means string parameter number 1).
    /// </remarks>
    public sealed class XroutParameter
    {
        /// <summary>
        /// Gets the one-based parameter number.
        /// </summary>
        public int ParameterNumber { get; }

        /// <summary>
        /// Gets a value indicating whether this parameter is a string (negative type byte).
        /// </summary>
        public bool IsString { get; }

        /// <summary>
        /// Gets the raw parameter data bytes.
        /// </summary>
        public byte[] Data { get; }

        /// <summary>
        /// Initialises a new parameter.
        /// </summary>
        /// <param name="parameterNumber">
        /// The one-based parameter number (1-127).
        /// </param>
        /// <param name="isString">
        /// <c>true</c> for a string parameter (encoded with a negative type byte);
        /// <c>false</c> for an integer parameter.
        /// </param>
        /// <param name="data">
        /// The parameter data bytes.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="parameterNumber"/> is outside 1-127.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="data"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="data"/> is longer than 255 bytes.
        /// </exception>
        public XroutParameter(int parameterNumber, bool isString, byte[] data)
        {
            if (parameterNumber < 1 || parameterNumber > 127)
            {
                throw new ArgumentOutOfRangeException(nameof(parameterNumber), "Parameter number must be 1-127.");
            }

            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            if (data.Length > 255)
            {
                throw new ArgumentException("Parameter data cannot exceed 255 bytes.", nameof(data));
            }

            ParameterNumber = parameterNumber;
            IsString = isString;
            Data = data;
        }

        /// <summary>
        /// Gets the encoded type byte for this parameter.
        /// </summary>
        /// <remarks>
        /// For an integer parameter this is the parameter number; for a string
        /// parameter it is the two's-complement of the parameter number.
        /// </remarks>
        public byte TypeByte
        {
            get
            {
                // String parameters use the negated (two's-complement) parameter number.
                return IsString ? (byte)(sbyte)(-ParameterNumber) : (byte)ParameterNumber;
            }
        }

        /// <summary>
        /// Gets the parameter data length in bytes.
        /// </summary>
        public int Length
        {
            get { return Data.Length; }
        }

        /// <summary>
        /// Creates an integer parameter from raw bytes.
        /// </summary>
        /// <param name="parameterNumber">
        /// The one-based parameter number.
        /// </param>
        /// <param name="data">
        /// The integer parameter data bytes.
        /// </param>
        /// <returns>
        /// A new integer <see cref="XroutParameter"/>.
        /// </returns>
        public static XroutParameter Integer(int parameterNumber, byte[] data)
        {
            return new XroutParameter(parameterNumber, false, data);
        }

        /// <summary>
        /// Creates an integer parameter carrying a single big-endian 16-bit value.
        /// </summary>
        /// <param name="parameterNumber">
        /// The one-based parameter number.
        /// </param>
        /// <param name="value">
        /// The 16-bit value to store, most significant byte first.
        /// </param>
        /// <returns>
        /// A new integer <see cref="XroutParameter"/> with two data bytes.
        /// </returns>
        public static XroutParameter Integer16(int parameterNumber, ushort value)
        {
            byte[] data = new byte[2];
            NdEndian.PutBe16(data, 0, value);
            return new XroutParameter(parameterNumber, false, data);
        }

        /// <summary>
        /// Creates an integer parameter carrying a single big-endian 32-bit value.
        /// </summary>
        /// <param name="parameterNumber">
        /// The one-based parameter number.
        /// </param>
        /// <param name="value">
        /// The 32-bit value to store, most significant byte first.
        /// </param>
        /// <returns>
        /// A new integer <see cref="XroutParameter"/> with four data bytes.
        /// </returns>
        /// <remarks>
        /// Used for the double-word parameters of the XROUT services, above all the 32-bit magic
        /// number carried by XSGNM, XSGN1, XSGMG and XSNET.
        /// </remarks>
        public static XroutParameter Integer32(int parameterNumber, uint value)
        {
            byte[] data = new byte[4];
            // One 32-bit write, not two 16-bit halves: the helper already puts the most
            // significant byte first, so splitting the value by hand only invited a wrong shift.
            NdEndian.PutBe32(data, 0, value);
            return new XroutParameter(parameterNumber, false, data);
        }

        /// <summary>
        /// Decodes this parameter's data as a big-endian unsigned integer.
        /// </summary>
        /// <param name="value">
        /// On return, the decoded value; zero when decoding was not possible.
        /// </param>
        /// <returns>
        /// True when the parameter holds one, two or four data bytes and was decoded.
        /// </returns>
        /// <remarks>
        /// XROUT integer parameters are not fixed width - a system number arrives as two bytes and
        /// a magic number as four - so callers should use this rather than assume a size.
        /// </remarks>
        public bool TryGetUInt32(out uint value)
        {
            value = 0;

            if (IsString)
            {
                return false;
            }

            if (Data.Length == 1)
            {
                value = Data[0];
                return true;
            }

            if (Data.Length == 2)
            {
                value = NdEndian.GetBe16(Data, 0);
                return true;
            }

            if (Data.Length == 4)
            {
                value = NdEndian.GetBe32(Data, 0);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Creates a string parameter from an ASCII string.
        /// </summary>
        /// <param name="parameterNumber">
        /// The one-based parameter number.
        /// </param>
        /// <param name="text">
        /// The ASCII text to store.
        /// </param>
        /// <returns>
        /// A new string <see cref="XroutParameter"/>.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="text"/> is null.
        /// </exception>
        public static XroutParameter Text(int parameterNumber, string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            return new XroutParameter(parameterNumber, true, Encoding.ASCII.GetBytes(text));
        }

        /// <summary>
        /// Decodes this parameter's data as an ASCII string.
        /// </summary>
        /// <returns>
        /// The parameter data interpreted as ASCII text.
        /// </returns>
        public string AsText()
        {
            return Encoding.ASCII.GetString(Data);
        }
    }
}
