//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;
using System.Globalization;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;

namespace RetroIO.Protocols.X25.Monitor
{
    /// <summary>
    /// Turns a parsed X.25 packet into a <see cref="MonitorLayer"/> whose fields carry byte offsets into the
    /// raw frame, so a viewer can highlight the corresponding hex when a field is selected.
    /// </summary>
    /// <remarks>
    /// This is the "describe" step of the decode (see docs/MONITOR_PROTOCOL_V1.md section 12). It reuses the
    /// authoritative <see cref="X25PacketParser"/> for the field VALUES so the viewer decode can never drift
    /// from the codec, and computes the field byte RANGES here from the ITU-T X.25 packet layout. Each packet
    /// family has its own describe method, mirroring the typed per-family structure in the analysis.
    /// </remarks>
    public static class X25Dissector
    {
        /// <summary>
        /// Builds the X.25 layer (summary plus offset-annotated fields) for a packet.
        /// </summary>
        /// <param name="packet">
        /// The raw X.25 packet bytes.
        /// </param>
        /// <param name="info">
        /// The packet already parsed by <see cref="X25PacketParser"/> (the source of field values).
        /// </param>
        /// <returns>
        /// The decoded X.25 layer.
        /// </returns>
        public static MonitorLayer Dissect(byte[] packet, X25PacketInfo info)
        {
            MonitorLayer layer = new MonitorLayer
            {
                Name = "X.25",
                Summary = BuildSummary(info)
            };

            // Header: the logical channel spans the GFI byte and the LCN byte; the packet-type identifier
            // is the third byte. These are present on every packet.
            AddField(layer, "LCI", info.LCN.ToString(CultureInfo.InvariantCulture), 0, 2);
            AddField(layer, "Type", info.PacketType + " (0x" + info.RawPTI.ToString("X2", CultureInfo.InvariantCulture) + ")", 2, 1);

            if (info.QBit || info.DBit)
            {
                // Q and D live in the GFI (byte 0); show them when set so their origin is visible.
                AddField(layer, "Q", info.QBit ? "1" : "0", 0, 1);
                AddField(layer, "D", info.DBit ? "1" : "0", 0, 1);
            }

            switch (info.PacketType)
            {
                case X25PacketType.CallRequest:
                    DescribeCallRequest(layer, packet, info);
                    break;

                case X25PacketType.CallAccepted:
                    DescribeCallAccepted(layer, packet, info);
                    break;

                case X25PacketType.Data:
                    DescribeData(layer, packet, info);
                    break;

                case X25PacketType.RR:
                case X25PacketType.RNR:
                case X25PacketType.REJ:
                    DescribeSupervisory(layer, info);
                    break;

                case X25PacketType.ClearRequest:
                    AddField(layer, "Cause", ByteText(info.ClearingCause), 3, 1);
                    AddField(layer, "Diagnostic", ByteText(info.DiagnosticCode), 4, 1);
                    break;

                case X25PacketType.ResetRequest:
                    AddField(layer, "Cause", ByteText(info.ResetCause), 3, 1);
                    AddField(layer, "Diagnostic", ByteText(info.DiagnosticCode), 4, 1);
                    break;

                case X25PacketType.RestartRequest:
                    AddField(layer, "Cause", ByteText(info.RestartCause), 3, 1);
                    AddField(layer, "Diagnostic", ByteText(info.DiagnosticCode), 4, 1);
                    break;

                case X25PacketType.Diagnostic:
                    AddField(layer, "Diagnostic", ByteText(info.DiagnosticCode), 3, 1);
                    break;

                default:
                    break;
            }

            return layer;
        }

        // Call Request / Incoming Call: address-length octet, packed BCD called+calling digits, then
        // facilities and call user data. The BCD stream is a contiguous nibble run (called digits first),
        // so an odd called length makes the calling address begin mid-octet; the byte ranges below are the
        // octets each address touches (conservative, good for highlighting).
        private static void DescribeCallRequest(MonitorLayer layer, byte[] packet, X25PacketInfo info)
        {
            if (packet.Length < 4)
            {
                return;
            }

            byte addressLengths = packet[3];
            int callingLen = (addressLengths >> 4) & 0x0F;
            int calledLen = addressLengths & 0x0F;
            int addrStart = 4;
            int calledBytes = (calledLen + 1) / 2;
            int callingStart = addrStart + (calledLen / 2);
            int callingBytes = (callingLen + 1) / 2;

            AddField(layer, "Addr Lengths", "called=" + calledLen + " calling=" + callingLen, 3, 1);
            if (calledLen > 0)
            {
                AddField(layer, "Called", info.CalledAddress, addrStart, calledBytes);
            }
            if (callingLen > 0)
            {
                AddField(layer, "Calling", info.CallingAddress, callingStart, callingBytes);
            }

            int totalDigits = calledLen + callingLen;
            int facLenOffset = addrStart + ((totalDigits + 1) / 2);
            if (facLenOffset < packet.Length)
            {
                int facLen = packet[facLenOffset];
                AddField(layer, "Facilities Len", facLen.ToString(CultureInfo.InvariantCulture), facLenOffset, 1);
                if (info.Facilities.Length > 0)
                {
                    AddField(layer, "Facilities", info.Facilities.Length + " bytes", facLenOffset + 1, info.Facilities.Length);
                }
            }
            if (info.CallUserData.Length > 0)
            {
                int cudOffset = packet.Length - info.CallUserData.Length;
                AddField(layer, "Call User Data", info.CallUserData.Length + " bytes", cudOffset, info.CallUserData.Length);
            }
        }

        // Call Accepted / Connected: facilities length then optional facilities and call user data.
        private static void DescribeCallAccepted(MonitorLayer layer, byte[] packet, X25PacketInfo info)
        {
            if (packet.Length <= 3)
            {
                return;
            }

            int facLen = packet[3];
            AddField(layer, "Facilities Len", facLen.ToString(CultureInfo.InvariantCulture), 3, 1);
            if (info.Facilities.Length > 0)
            {
                AddField(layer, "Facilities", info.Facilities.Length + " bytes", 4, info.Facilities.Length);
            }
            if (info.CallUserData.Length > 0)
            {
                int cudOffset = packet.Length - info.CallUserData.Length;
                AddField(layer, "Call User Data", info.CallUserData.Length + " bytes", cudOffset, info.CallUserData.Length);
            }
        }

        // Data packet: sequence numbers live in the packet-type octet (modulo 8) or in the two octets after
        // it (modulo 128); the user data follows.
        private static void DescribeData(MonitorLayer layer, byte[] packet, X25PacketInfo info)
        {
            if (info.Modulo128)
            {
                AddField(layer, "P(S)", info.PS.ToString(CultureInfo.InvariantCulture), 3, 1);
                AddField(layer, "P(R)", info.PR.ToString(CultureInfo.InvariantCulture), 4, 1);
                AddField(layer, "M", info.MBit ? "1" : "0", 0, 1);
                if (info.UserData.Length > 0)
                {
                    AddField(layer, "User Data", info.UserData.Length + " bytes", 5, info.UserData.Length);
                }
            }
            else
            {
                AddField(layer, "P(S)", info.PS.ToString(CultureInfo.InvariantCulture), 2, 1);
                AddField(layer, "P(R)", info.PR.ToString(CultureInfo.InvariantCulture), 2, 1);
                AddField(layer, "M", info.MBit ? "1" : "0", 2, 1);
                if (info.UserData.Length > 0)
                {
                    AddField(layer, "User Data", info.UserData.Length + " bytes", 3, info.UserData.Length);
                }
            }
        }

        // Supervisory (RR/RNR/REJ): carries only a receive sequence number.
        private static void DescribeSupervisory(MonitorLayer layer, X25PacketInfo info)
        {
            if (info.Modulo128)
            {
                AddField(layer, "P(R)", info.PR.ToString(CultureInfo.InvariantCulture), 3, 1);
            }
            else
            {
                AddField(layer, "P(R)", info.PR.ToString(CultureInfo.InvariantCulture), 2, 1);
            }
        }

        private static string BuildSummary(X25PacketInfo info)
        {
            switch (info.PacketType)
            {
                case X25PacketType.CallRequest:
                    return "Call Request " + info.CalledAddress + "←" + info.CallingAddress;
                case X25PacketType.CallAccepted:
                    return "Call Accepted";
                case X25PacketType.Data:
                    return "Data P(S)=" + info.PS + " P(R)=" + info.PR;
                case X25PacketType.RR:
                case X25PacketType.RNR:
                case X25PacketType.REJ:
                    return info.PacketType + " P(R)=" + info.PR;
                case X25PacketType.ClearRequest:
                    return "Clear Request cause=" + info.ClearingCause;
                case X25PacketType.ClearConfirmation:
                    return "Clear Confirmation";
                case X25PacketType.ResetRequest:
                    return "Reset Request cause=" + info.ResetCause;
                case X25PacketType.RestartRequest:
                    return "Restart Request cause=" + info.RestartCause;
                default:
                    return info.PacketType.ToString();
            }
        }

        private static void AddField(MonitorLayer layer, string name, string value, int offset, int length)
        {
            layer.Fields.Add(new MonitorField { Name = name, Value = value, Offset = offset, Length = length });
        }

        private static string ByteText(byte value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
