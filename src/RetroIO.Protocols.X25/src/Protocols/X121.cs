//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RetroIO.Protocols.X25.Protocols
{
    /// <summary>
    /// X.121 International Numbering Plan for Public Data Networks
    /// Handles address validation, encoding, and parsing according to ITU-T X.121
    /// </summary>
    public class X121
    {
        private readonly ILogger? _logger;
        
        // Sources for every rule below: ITU-T X.121 (10/2000), file T-REC-X.121-200010-I!!PDF-E.txt, and
        // ITU-T X.25 (10/96), file T-REC-X.25-199610-I!!PDF-E.txt, both in the specification folder kept beside
        // this repository (text made with pdftotext -layout). "line N" means a line in those text files.
        //
        // What an X.25 called address can hold (and so what this class may refuse):
        //  - X.25 5.2.1 (X.25 lines 6243-6246): the non-TOA/NPI address field carries an X.121 or X.301 address
        //    "whose length (including possible prefixes and/or escape codes) is not greater than 15 digits".
        //  - X.121 7.6.2 (lines 509-514): inside its own network a terminal is called by its Network Terminal
        //    Number (NTN) or National Number (NN) alone. X.121 says nothing about the digits of such a local
        //    number, so ANY first digit is allowed (SINTRAN's local number 110006 starts with 1).
        //  - X.121 7.8 (lines 557-573): a prefix choosing national or international format is "a national
        //    matter", is not part of the number, and in the non-TOA/NPI X.25 format can be only one digit. The
        //    network decides it, so this class cannot refuse a prefix (02402001487 = prefix 0 + 2402001487).
        //  - X.121 7.9 and Table 2 (lines 579-599, 628-641): escape codes 8, 9 and 0 in front of an F.69 or
        //    E.164 number. These are allowed too.
        //  - X.25 5.2.1 NOTE 1 (X.25 lines 6258-6260): an "alternative address" does not follow X.121 at all; it
        //    is carried only in the TOA/NPI format, which this class does not handle.
        // So the only checks X.121 and X.25 allow on a non-TOA/NPI address without the network's own rules are:
        // decimal digits only, at most 15 of them. Everything finer (which prefix, which DNIC is really
        // allocated, how long a national number is) is left to the network and NOT refused here.

        /// <summary>
        /// The most digits a non-TOA/NPI X.25 address field holds, prefix and escape code included
        /// (X.25 5.2.1, X.25 lines 6243-6246; X.121 7.8 NOTE, lines 571-573).
        /// </summary>
        public const int MAX_ADDRESS_LENGTH = 15;

        /// <summary>
        /// The fewest digits this class accepts as a called address. X.121 sets no lower limit for a number used
        /// inside one network (7.6.2, lines 509-514); one digit is the least a call can be made to.
        /// </summary>
        public const int MIN_ADDRESS_LENGTH = 1;

        /// <summary>
        /// Longest international data number, prefix and escape code not counted (X.121 7.7, line 546).
        /// </summary>
        public const int MAX_INTERNATIONAL_DATA_NUMBER_LENGTH = 14;

        /// <summary>
        /// Shortest international data number (X.121 7.7, line 546).
        /// </summary>
        public const int MIN_INTERNATIONAL_DATA_NUMBER_LENGTH = 5;

        /// <summary>
        /// DNIC (Data Network Identification Code) length: always four digits (X.121 7.2.3, line 410; 7.7, line 548).
        /// </summary>
        public const int DNIC_LENGTH = 4;

        /// <summary>
        /// DCC (Data Country Code) length: the first three digits of a DNIC whose first digit is 2-7
        /// (X.121 7.2.3, lines 413-415; 7.7, lines 548-549).
        /// </summary>
        public const int DCC_LENGTH = 3;

        // The old RegionCodes class named the first DNIC digits "Europe/Africa/Middle East", "North America" ...
        // and called 6 and 7 "reserved". X.121 gives no such names: Table 1 (lines 395-409) says only that 1 is
        // for mobile satellite systems and global networks and 2-7 are "country or geographic specific", and
        // Annex J lists zones 6 and 7 with allocated country codes (for example 602 Egypt, line 1703). The class
        // and its names were removed.

        /// <summary>
        /// Creates the X.121 helper.
        /// </summary>
        /// <param name="logger">
        /// Optional diagnostics logger; may be null.
        /// </param>
        public X121(ILogger? logger = null)
        {
            _logger = logger;
        }

        /// <summary>
        /// Checks an address as it goes in the non-TOA/NPI called or calling address field of an X.25 packet:
        /// 1 to 15 decimal digits (X.25 5.2.1, X.25 lines 6243-6246). It does NOT require the address to be an
        /// international data number: a local number inside one network (X.121 7.6.2), a national or international
        /// prefix (X.121 7.8, a national matter) and an escape code (X.121 7.9) are all allowed, because only the
        /// network knows which of these it uses.
        /// </summary>
        /// <param name="address">
        /// Address string to validate
        /// </param>
        /// <returns>
        /// True if the address can be carried in the address field.
        /// </returns>
        public bool IsValidAddress(string address)
        {
            if (string.IsNullOrEmpty(address))
            {
                _logger?.LogWarning("X.121: Empty address provided");
                return false;
            }

            if (address.Length > MAX_ADDRESS_LENGTH)
            {
                _logger?.LogWarning("X.121: Address length {Length} outside valid range {Min}-{Max}",
                    address.Length, MIN_ADDRESS_LENGTH, MAX_ADDRESS_LENGTH);
                return false;
            }

            // The field holds decimal digits, one per semi-octet (X.25 5.2.1). ASCII '0'-'9' only, so a
            // non-ASCII Unicode digit is refused too.
            if (!AllDecimalDigits(address))
            {
                _logger?.LogWarning("X.121: Address contains non-numeric characters: {Address}", address);
                return false;
            }

            _logger?.LogTrace("X.121: Address {Address} is valid", address);
            return true;
        }

        /// <summary>
        /// Checks whether the digits, taken with no prefix and no escape code, form an international data number:
        /// 5 to 14 digits (X.121 7.7, line 546) beginning with a DNIC whose first digit is 1-7 (Table 1,
        /// lines 395-409). 8, 9 and 0 are escape codes and never begin a DNIC (7.2.2 NOTE 2, line 392).
        /// This does not say that the DNIC is allocated (Annex J is a list that changes, 7.3, lines 460-464), and
        /// a number that passes may still be a local number of the same length; only the network's prefix rule
        /// (7.8.2) tells the two apart.
        /// </summary>
        /// <param name="address">The digits to check.</param>
        /// <returns>True if the digits have the form of an international data number.</returns>
        public bool IsInternationalDataNumber(string address)
        {
            if (string.IsNullOrEmpty(address)
                || address.Length < MIN_INTERNATIONAL_DATA_NUMBER_LENGTH
                || address.Length > MAX_INTERNATIONAL_DATA_NUMBER_LENGTH
                || !AllDecimalDigits(address))
            {
                return false;
            }

            return IsDnicFirstDigit(address[0]);
        }

        /// <summary>
        /// Checks whether a digit can begin a DNIC: 1 (public mobile satellite systems and global public data
        /// networks) or 2-7 (country or geographic DNICs), X.121 Table 1, lines 395-409, and 7.2.3,
        /// lines 410-414. 8, 9 and 0 are escape codes (7.2.2 NOTE 2, line 392).
        /// </summary>
        /// <param name="digit">
        /// First digit of the DNIC
        /// </param>
        /// <returns>
        /// True if the digit can begin a DNIC.
        /// </returns>
        public bool IsDnicFirstDigit(char digit)
        {
            return digit >= '1' && digit <= '7';
        }

        private static bool AllDecimalDigits(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] < '0' || text[i] > '9')
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Extract DNIC from X.121 address. Only an address that has the form of an international data number
        /// (<see cref="IsInternationalDataNumber"/>) has a DNIC in its first four digits (X.121 7.6.1, line 508);
        /// a local number, or one with a prefix or escape code in front, does not.
        /// </summary>
        /// <param name="address">
        /// X.121 address
        /// </param>
        /// <returns>
        /// DNIC, or null if the address is not an international data number
        /// </returns>
        public string? ExtractDNIC(string address)
        {
            if (!IsInternationalDataNumber(address))
                return null;

            return address.Substring(0, DNIC_LENGTH);
        }

        /// <summary>
        /// Returns the digits after the DNIC, that is the Network Terminal Number (X.121 7.6.1, line 508), of an
        /// international data number. For any other address (a local number, or one with a prefix or escape code)
        /// the address itself is returned, since X.121 gives no way to split it without the network's rules.
        /// </summary>
        /// <param name="address">
        /// X.121 address
        /// </param>
        /// <returns>
        /// The digits after the DNIC, or the original address if it has no DNIC
        /// </returns>
        public string ExtractNationalNumber(string address)
        {
            if (!IsInternationalDataNumber(address))
                return address;

            return address.Substring(DNIC_LENGTH);
        }

        /// <summary>
        /// Encode X.121 address in BCD format for X.25 packets
        /// </summary>
        /// <param name="address">
        /// X.121 address string
        /// </param>
        /// <returns>
        /// BCD encoded bytes
        /// </returns>
        public byte[] EncodeBCD(string address)
        {
            if (!IsValidAddress(address))
            {
                throw new ArgumentException($"Invalid X.121 address: {address}");
            }

            // Pad to even length for BCD encoding
            string paddedAddress = address.Length % 2 == 0 ? address : address + "0";
            byte[] encoded = new byte[paddedAddress.Length / 2];

            for (int i = 0; i < paddedAddress.Length; i += 2)
            {
                byte highNibble = (byte)((paddedAddress[i] - '0') << 4);
                byte lowNibble = (byte)(paddedAddress[i + 1] - '0');
                encoded[i / 2] = (byte)(highNibble | lowNibble);
            }

            _logger?.LogTrace("X.121: Encoded address {Address} to {EncodedLength} BCD bytes", 
                address, encoded.Length);
            return encoded;
        }

        /// <summary>
        /// Decode BCD format to X.121 address string
        /// </summary>
        /// <param name="bcdData">
        /// BCD encoded address
        /// </param>
        /// <param name="addressLength">
        /// Actual address length (to handle padding)
        /// </param>
        /// <returns>
        /// Decoded X.121 address
        /// </returns>
        public string DecodeBCD(byte[] bcdData, int addressLength)
        {
            if (bcdData == null || bcdData.Length == 0)
            {
                throw new ArgumentException("BCD data cannot be null or empty");
            }

            if (addressLength < MIN_ADDRESS_LENGTH || addressLength > MAX_ADDRESS_LENGTH)
            {
                throw new ArgumentException($"Address length {addressLength} outside valid range");
            }

            var decoded = new StringBuilder();

            for (int i = 0; i < bcdData.Length && decoded.Length < addressLength; i++)
            {
                byte highNibble = (byte)((bcdData[i] & 0xF0) >> 4);
                byte lowNibble = (byte)(bcdData[i] & 0x0F);

                if (highNibble <= 9 && decoded.Length < addressLength)
                {
                    decoded.Append((char)('0' + highNibble));
                }

                if (lowNibble <= 9 && decoded.Length < addressLength)
                {
                    decoded.Append((char)('0' + lowNibble));
                }
            }

            string result = decoded.ToString();
            _logger?.LogTrace("X.121: Decoded BCD to address {Address}", result);
            return result;
        }

        /// <summary>
        /// Calculate required BCD byte length for address
        /// </summary>
        /// <param name="addressLength">
        /// Address string length
        /// </param>
        /// <returns>
        /// Required BCD byte array length
        /// </returns>
        public int CalculateBCDLength(int addressLength)
        {
            return (addressLength + 1) / 2; // Round up to nearest byte
        }

        /// <summary>
        /// Create X.121 address from DNIC and national number
        /// </summary>
        /// <param name="dnic">
        /// 4-digit DNIC
        /// </param>
        /// <param name="nationalNumber">
        /// National number (up to 10 digits)
        /// </param>
        /// <returns>
        /// Complete X.121 address
        /// </returns>
        public string CreateAddress(string dnic, string nationalNumber)
        {
            if (string.IsNullOrEmpty(dnic) || dnic.Length != DNIC_LENGTH)
            {
                throw new ArgumentException($"DNIC must be exactly {DNIC_LENGTH} digits");
            }

            if (string.IsNullOrEmpty(nationalNumber))
            {
                throw new ArgumentException("National number cannot be empty");
            }

            string fullAddress = dnic + nationalNumber;

            // DNIC + NTN is an international data number (X.121 7.6.1, line 508): at most 14 digits, so an NTN of
            // at most 10 (7.7, lines 546-551), and the DNIC must begin with 1-7 (Table 1).
            if (!IsInternationalDataNumber(fullAddress))
            {
                throw new ArgumentException($"Generated address is invalid: {fullAddress}");
            }

            return fullAddress;
        }

        // IsLocalFormat ("shorter than 4 digits") and GetRegionName were removed. X.121 does not tell a local
        // number from an international one by its length: a local NTN can be up to 10 digits (7.7, line 550),
        // and which format a call uses is chosen by a prefix that is a national matter (7.8.2, lines 562-568).

        /// <summary>
        /// Check if address has the form of an international data number (see
        /// <see cref="IsInternationalDataNumber"/>). This is a reading of the digits only; a local number of the
        /// same form passes too, and only the network's prefix rule (X.121 7.8.2) can tell them apart.
        /// </summary>
        /// <param name="address">
        /// X.121 address
        /// </param>
        /// <returns>
        /// True if the digits have the form of an international data number
        /// </returns>
        public bool IsInternationalFormat(string address)
        {
            return IsInternationalDataNumber(address);
        }

        /// <summary>
        /// Parse address components for display/debugging
        /// </summary>
        /// <param name="address">
        /// X.121 address
        /// </param>
        /// <returns>
        /// Address information structure
        /// </returns>
        public X121AddressInfo ParseAddress(string address)
        {
            var info = new X121AddressInfo
            {
                OriginalAddress = address,
                IsValid = IsValidAddress(address),
                IsInternational = IsInternationalFormat(address)
            };

            if (info.IsInternational)
            {
                info.DNIC = ExtractDNIC(address);
                info.NationalNumber = ExtractNationalNumber(address);
                if (!string.IsNullOrEmpty(info.DNIC))
                {
                    info.DnicFirstDigit = info.DNIC[0];
                }
            }
            else if (info.IsValid)
            {
                info.NationalNumber = address;
            }

            return info;
        }
    }

    /// <summary>
    /// X.121 address information structure
    /// </summary>
    public class X121AddressInfo
    {
        /// <summary>
        /// Gets or sets the address text as given.
        /// </summary>
        public string OriginalAddress { get; set; } = "";
        /// <summary>
        /// Gets or sets whether the address passed validation.
        /// </summary>
        public bool IsValid { get; set; }
        /// <summary>
        /// Gets or sets whether the digits have the form of an international data number (DNIC + NTN,
        /// X.121 7.6.1). A local number of the same form also sets it; see <see cref="X121.IsInternationalFormat"/>.
        /// </summary>
        public bool IsInternational { get; set; }
        /// <summary>
        /// Gets or sets the data network identification code, or null when the address has none.
        /// </summary>
        public string? DNIC { get; set; }
        /// <summary>
        /// Gets or sets the digits after the DNIC (the NTN), or the whole address when it has no DNIC; null when
        /// the address is not valid.
        /// </summary>
        public string? NationalNumber { get; set; }
        /// <summary>
        /// Gets or sets the first digit of the DNIC: 1 for mobile satellite systems and global networks, 2-7 for
        /// a country or geographic zone (X.121 Table 1, lines 395-409). X.121 gives the zones no names.
        /// </summary>
        public char DnicFirstDigit { get; set; }

        /// <summary>
        /// Returns a one-line description of the address.
        /// </summary>
        /// <returns>
        /// One of these:
        ///  - <c>Invalid:</c> followed by the original address, when the address is not valid.
        ///  - for an address with the form of an international data number, the original address followed by
        ///    its DNIC and the digits after it in brackets.
        ///  - otherwise the original address followed by <c>(no DNIC)</c>.
        /// </returns>
        public override string ToString()
        {
            if (!IsValid)
                return $"Invalid: {OriginalAddress}";

            if (IsInternational)
                return $"{OriginalAddress} (DNIC: {DNIC}, NTN: {NationalNumber})";

            return $"{OriginalAddress} (no DNIC)";
        }
    }
}
