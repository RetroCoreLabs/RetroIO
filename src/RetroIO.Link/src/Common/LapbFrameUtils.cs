using System;
using RetroIO.Link.Models;

using RetroIO.Protocols.X25.Common;
using RetroIO.Link.Lapb.DeleteMe;
namespace RetroIO.Link.Lapb
{
    /// <summary>
    /// Utility methods for LAPB frame handling.
    /// Consolidates duplicate frame manipulation code from LapbLayer and LapbInstance.
    /// </summary>
    public static class LapbFrameUtils
    {
        /// <summary>
        /// Detect frame type from control byte.
        /// Replaces duplicate logic in LapbLayer.cs and LapbInstance.cs
        /// </summary>
        public static LapbFrameType DetectFrameType(byte control)
        {
            if ((control & 0x01) == 0)
                return LapbFrameType.Information;
            else if ((control & 0x03) == 0x01)
                return LapbFrameType.Supervisory;
            else
                return LapbFrameType.Unnumbered;
        }

        /// <summary>
        /// Extract N(S) send sequence number from control byte.
        /// </summary>
        public static byte ExtractNS(byte control)
        {
            return (byte)((control >> 1) & 0x07);
        }

        /// <summary>
        /// Extract N(R) receive sequence number from control byte.
        /// </summary>
        public static byte ExtractNR(byte control)
        {
            return (byte)((control >> 5) & 0x07);
        }

        /// <summary>
        /// Extract information field from LAPB frame.
        /// </summary>
        public static byte[] ExtractInformation(byte[] frameData, int length)
        {
            if (frameData == null || length <= 2)
                return Array.Empty<byte>();

            int infoLength = length - 2;
            byte[] information = new byte[infoLength];
            Array.Copy(frameData, 2, information, 0, infoLength);
            return information;
        }

        /// <summary>
        /// Build LAPB frame with address, control, and optional information field.
        /// </summary>
        public static byte[] BuildFrame(byte address, byte control, byte[]? information = null, int infoLength = -1)
        {
            int dataLength = 0;
            if (information != null)
            {
                dataLength = infoLength >= 0 ? infoLength : information.Length;
            }

            byte[] frame = new byte[2 + dataLength];
            frame[0] = address;
            frame[1] = control;

            if (dataLength > 0 && information != null)
            {
                Array.Copy(information, 0, frame, 2, dataLength);
            }

            return frame;
        }

        /// <summary>
        /// Build I-frame control byte with sequence numbers.
        /// </summary>
        public static byte BuildIFrameControl(byte ns, byte nr, bool pollFinal)
        {
            return (byte)((ns << 1) | (nr << 5) | (pollFinal ? 0x10 : 0x00));
        }

        /// <summary>
        /// Build S-frame control byte.
        /// </summary>
        public static byte BuildSFrameControl(byte sType, byte nr, bool pollFinal)
        {
            return (byte)(0x01 | (sType << 2) | (nr << 5) | (pollFinal ? 0x10 : 0x00));
        }

        /// <summary>
        /// Check if frame is valid (minimum length).
        /// </summary>
        public static bool IsValidFrame(byte[]? frameData, int length)
        {
            return frameData != null && length >= 2 && frameData.Length >= length;
        }

        /// <summary>
        /// Validate LAPB frame and return error reason if invalid, null if valid.
        /// Extracted from ProtocolRuntime for reuse.
        /// </summary>
        public static string? ValidateFrame(byte[]? frameData, int length, LapbFrameType frameType, byte? ns, byte? nr)
        {
            // Basic frame validation
            if (frameData == null || length < 2)
            {
                return "LAPB_Invalid_Length";
            }

            // Validate frame type
            if (frameType == LapbFrameType.Unknown)
            {
                return "LAPB_Unknown_Frame_Type";
            }

            // Validate sequence numbers for I-frames
            if (frameType == LapbFrameType.Information)
            {
                if (!ns.HasValue || !nr.HasValue)
                {
                    return "LAPB_Missing_Sequence_Numbers";
                }

                // Validate sequence number range (0-7 for basic LAPB)
                if (ns.Value > 7 || nr.Value > 7)
                {
                    return "LAPB_Invalid_Sequence_Range";
                }
            }

            // Validate supervisory frame N(R)
            if (frameType == LapbFrameType.Supervisory)
            {
                if (!nr.HasValue)
                {
                    return "LAPB_Missing_NR_Supervisory";
                }

                if (nr.Value > 7)
                {
                    return "LAPB_Invalid_NR_Range";
                }
            }

            return null; // No errors
        }
    }
}
