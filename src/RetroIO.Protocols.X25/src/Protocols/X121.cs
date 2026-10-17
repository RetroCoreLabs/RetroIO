//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Text;
using System.Text.RegularExpressions;
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
        
        /// <summary>
        /// Maximum address length according to X.121 standard (14 digits)
        /// </summary>
        public const int MAX_ADDRESS_LENGTH = 14;
        
        /// <summary>
        /// Minimum address length (1 digit for local implementations)
        /// </summary>
        public const int MIN_ADDRESS_LENGTH = 1;
        
        /// <summary>
        /// DNIC (Data Network Identification Code) length (4 digits)
        /// </summary>
        public const int DNIC_LENGTH = 4;

        /// <summary>
        /// Geographic region codes for DNIC first digit
        /// </summary>
        public static class RegionCodes
        {
            public const char EUROPE_AFRICA_MIDDLE_EAST = '2';
            public const char NORTH_AMERICA = '3';
            public const char ASIA_OCEANIA = '4';
            public const char LATIN_AMERICA_CARIBBEAN = '5';
            public const char RESERVED_6 = '6';
            public const char RESERVED_7 = '7';
        }

        public X121(ILogger? logger = null)
        {
            _logger = logger;
        }

        /// <summary>
        /// Validate X.121 address format and content
        /// </summary>
        /// <param name="address">
        /// Address string to validate
        /// </param>
        /// <returns>
        /// True if valid X.121 address
        /// </returns>
        public bool IsValidAddress(string address)
        {
            if (string.IsNullOrEmpty(address))
            {
                _logger?.LogWarning("X.121: Empty address provided");
                return false;
            }

            // Check length constraints
            if (address.Length < MIN_ADDRESS_LENGTH || address.Length > MAX_ADDRESS_LENGTH)
            {
                _logger?.LogWarning("X.121: Address length {Length} outside valid range {Min}-{Max}", 
                    address.Length, MIN_ADDRESS_LENGTH, MAX_ADDRESS_LENGTH);
                return false;
            }

            // Check that all characters are digits
            if (!Regex.IsMatch(address, @"^\d+$"))
            {
                _logger?.LogWarning("X.121: Address contains non-numeric characters: {Address}", address);
                return false;
            }

            // Validate DNIC if address is long enough for international format
            if (address.Length >= DNIC_LENGTH)
            {
                char regionCode = address[0];
                if (!IsValidRegionCode(regionCode))
                {
                    _logger?.LogWarning("X.121: Invalid region code {RegionCode} in address {Address}", 
                        regionCode, address);
                    return false;
                }
            }

            _logger?.LogTrace("X.121: Address {Address} is valid", address);
            return true;
        }

        /// <summary>
        /// Check if region code is valid according to X.121
        /// </summary>
        /// <param name="regionCode">
        /// First digit of DNIC
        /// </param>
        /// <returns>
        /// True if valid region code
        /// </returns>
        public bool IsValidRegionCode(char regionCode)
        {
            return regionCode >= '2' && regionCode <= '7';
        }

        /// <summary>
        /// Extract DNIC from X.121 address
        /// </summary>
        /// <param name="address">
        /// X.121 address
        /// </param>
        /// <returns>
        /// DNIC or null if address too short
        /// </returns>
        public string? ExtractDNIC(string address)
        {
            if (!IsValidAddress(address) || address.Length < DNIC_LENGTH)
                return null;

            return address.Substring(0, DNIC_LENGTH);
        }

        /// <summary>
        /// Extract national number from X.121 address
        /// </summary>
        /// <param name="address">
        /// X.121 address
        /// </param>
        /// <returns>
        /// National number or original address if no DNIC
        /// </returns>
        public string ExtractNationalNumber(string address)
        {
            if (!IsValidAddress(address))
                return address;

            if (address.Length > DNIC_LENGTH)
            {
                return address.Substring(DNIC_LENGTH);
            }

            return address; // Entire address is national number
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

            if (!IsValidAddress(fullAddress))
            {
                throw new ArgumentException($"Generated address is invalid: {fullAddress}");
            }

            return fullAddress;
        }

        /// <summary>
        /// Check if address is local format (shorter than DNIC length)
        /// </summary>
        /// <param name="address">
        /// X.121 address
        /// </param>
        /// <returns>
        /// True if local format address
        /// </returns>
        public bool IsLocalFormat(string address)
        {
            return IsValidAddress(address) && address.Length < DNIC_LENGTH;
        }

        /// <summary>
        /// Check if address is international format (includes DNIC)
        /// </summary>
        /// <param name="address">
        /// X.121 address
        /// </param>
        /// <returns>
        /// True if international format address
        /// </returns>
        public bool IsInternationalFormat(string address)
        {
            return IsValidAddress(address) && address.Length >= DNIC_LENGTH;
        }

        /// <summary>
        /// Get region name from region code
        /// </summary>
        /// <param name="regionCode">
        /// First digit of DNIC
        /// </param>
        /// <returns>
        /// Region name or "Unknown"
        /// </returns>
        public string GetRegionName(char regionCode)
        {
            return regionCode switch
            {
                RegionCodes.EUROPE_AFRICA_MIDDLE_EAST => "Europe/Africa/Middle East",
                RegionCodes.NORTH_AMERICA => "North America",
                RegionCodes.ASIA_OCEANIA => "Asia/Oceania",
                RegionCodes.LATIN_AMERICA_CARIBBEAN => "Latin America/Caribbean",
                RegionCodes.RESERVED_6 => "Reserved (6)",
                RegionCodes.RESERVED_7 => "Reserved (7)",
                _ => "Unknown"
            };
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
                IsLocal = IsLocalFormat(address),
                IsInternational = IsInternationalFormat(address)
            };

            if (info.IsValid && info.IsInternational)
            {
                info.DNIC = ExtractDNIC(address);
                info.NationalNumber = ExtractNationalNumber(address);
                
                if (!string.IsNullOrEmpty(info.DNIC))
                {
                    info.RegionCode = info.DNIC[0];
                    info.RegionName = GetRegionName(info.RegionCode);
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
        public string OriginalAddress { get; set; } = "";
        public bool IsValid { get; set; }
        public bool IsLocal { get; set; }
        public bool IsInternational { get; set; }
        public string? DNIC { get; set; }
        public string? NationalNumber { get; set; }
        public char RegionCode { get; set; }
        public string RegionName { get; set; } = "";

        public override string ToString()
        {
            if (!IsValid)
                return $"Invalid: {OriginalAddress}";

            if (IsInternational)
                return $"{OriginalAddress} (DNIC: {DNIC}, Region: {RegionName}, National: {NationalNumber})";

            return $"{OriginalAddress} (Local format)";
        }
    }
}