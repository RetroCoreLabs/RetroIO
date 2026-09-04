//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using System.Text;
using RetroIO.Protocols.Tnfs.Protocol;

namespace RetroIO.Protocols.Tnfs.Common
{
    /// <summary>
    /// TNFS protocol payload builders and parsers for specific commands.
    /// Shared between client and server implementations.
    /// </summary>
    public static class TnfsPayloads
    {
        #region Mount Command

        /// <summary>
        /// Build MOUNT request payload.
        /// </summary>
        /// <param name="path">Mount path (usually "/")</param>
        /// <returns>Payload bytes</returns>
        public static byte[] BuildMountRequest(string path)
        {
            byte[] pathBytes = Encoding.ASCII.GetBytes(path + '\0');
            byte[] payload = new byte[2 + pathBytes.Length];

            // Protocol version (little-endian: LSB, MSB)
            payload[0] = TnfsConstants.PROTOVERSION_LSB; // 0x02
            payload[1] = TnfsConstants.PROTOVERSION_MSB; // 0x01

            // Mount path (null-terminated)
            Array.Copy(pathBytes, 0, payload, 2, pathBytes.Length);

            return payload;
        }

        /// <summary>
        /// Parse MOUNT request payload.
        /// </summary>
        /// <param name="payload">Payload bytes</param>
        /// <param name="versionLSB">Protocol version LSB</param>
        /// <param name="versionMSB">Protocol version MSB</param>
        /// <param name="path">Mount path</param>
        /// <returns>True if parsed successfully</returns>
        public static bool ParseMountRequest(byte[] payload, out byte versionLSB, out byte versionMSB, out string path)
        {
            versionLSB = 0;
            versionMSB = 0;
            path = string.Empty;

            if (payload == null || payload.Length < 3)
            {
                return false;
            }

            versionLSB = payload[0];
            versionMSB = payload[1];

            // Read null-terminated path
            path = ReadNullTerminatedString(payload, 2);
            return true;
        }

        /// <summary>
        /// Build MOUNT response payload.
        /// </summary>
        /// <param name="versionLSB">Server protocol version LSB</param>
        /// <param name="versionMSB">Server protocol version MSB</param>
        /// <returns>Payload bytes</returns>
        public static byte[] BuildMountResponse(byte versionLSB, byte versionMSB)
        {
            return new byte[] { versionLSB, versionMSB };
        }

        /// <summary>
        /// Parse MOUNT response payload.
        /// </summary>
        /// <param name="payload">Payload bytes</param>
        /// <param name="versionLSB">Server protocol version LSB</param>
        /// <param name="versionMSB">Server protocol version MSB</param>
        /// <returns>True if parsed successfully</returns>
        public static bool ParseMountResponse(byte[] payload, out byte versionLSB, out byte versionMSB)
        {
            versionLSB = 0;
            versionMSB = 0;

            if (payload == null || payload.Length < 2)
            {
                return false;
            }

            versionLSB = payload[0];
            versionMSB = payload[1];
            return true;
        }

        #endregion

        #region Directory Commands

        /// <summary>
        /// Build OPENDIR request payload.
        /// </summary>
        /// <param name="path">Directory path</param>
        /// <returns>Payload bytes</returns>
        public static byte[] BuildOpenDirRequest(string path)
        {
            return Encoding.ASCII.GetBytes(path + '\0');
        }

        /// <summary>
        /// Parse OPENDIR request payload.
        /// </summary>
        /// <param name="payload">Payload bytes</param>
        /// <returns>Directory path</returns>
        public static string ParseOpenDirRequest(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
            {
                return string.Empty;
            }

            return ReadNullTerminatedString(payload, 0);
        }

        /// <summary>
        /// Build OPENDIR response payload.
        /// </summary>
        /// <param name="dirHandle">Directory handle</param>
        /// <returns>Payload bytes</returns>
        public static byte[] BuildOpenDirResponse(ushort dirHandle)
        {
            byte[] payload = new byte[2];
            TnfsEndian.WriteUInt16(payload, 0, dirHandle);
            return payload;
        }

        /// <summary>
        /// Parse OPENDIR response payload.
        /// </summary>
        /// <param name="payload">Payload bytes</param>
        /// <returns>Directory handle or null if invalid</returns>
        public static ushort? ParseOpenDirResponse(byte[] payload)
        {
            if (payload == null || payload.Length < 2)
            {
                return null;
            }

            return TnfsEndian.ReadUInt16(payload, 0);
        }

        /// <summary>
        /// Build READDIR request payload.
        /// </summary>
        /// <param name="dirHandle">Directory handle</param>
        /// <returns>Payload bytes</returns>
        public static byte[] BuildReadDirRequest(ushort dirHandle)
        {
            byte[] payload = new byte[2];
            TnfsEndian.WriteUInt16(payload, 0, dirHandle);
            return payload;
        }

        /// <summary>
        /// Parse READDIR request payload.
        /// </summary>
        /// <param name="payload">Payload bytes</param>
        /// <returns>Directory handle or null if invalid</returns>
        public static ushort? ParseReadDirRequest(byte[] payload)
        {
            if (payload == null || payload.Length < 2)
            {
                return null;
            }

            return TnfsEndian.ReadUInt16(payload, 0);
        }

        /// <summary>
        /// Build READDIR response payload.
        /// </summary>
        /// <param name="entry">Directory entry</param>
        /// <returns>Payload bytes</returns>
        public static byte[] BuildReadDirResponse(TnfsDirectoryEntry entry)
        {
            using (System.IO.MemoryStream ms = new System.IO.MemoryStream())
            {
                // Attributes (1 byte)
                ms.WriteByte((byte)entry.Attributes);

                // Filename (null-terminated)
                byte[] nameBytes = Encoding.ASCII.GetBytes(entry.Name);
                ms.Write(nameBytes, 0, nameBytes.Length);
                ms.WriteByte(0);

                // Size (4 bytes, little-endian)
                byte[] sizeBytes = new byte[4];
                TnfsEndian.WriteUInt32(sizeBytes, 0, (uint)entry.Size);
                ms.Write(sizeBytes, 0, 4);

                // Modified time (4 bytes, little-endian)
                byte[] timeBytes = new byte[4];
                TnfsEndian.WriteUInt32(timeBytes, 0, entry.ModifiedTime);
                ms.Write(timeBytes, 0, 4);

                return ms.ToArray();
            }
        }

        /// <summary>
        /// Parse READDIR response payload.
        /// </summary>
        /// <param name="payload">Payload bytes</param>
        /// <returns>Directory entry or null if invalid</returns>
        public static TnfsDirectoryEntry? ParseReadDirResponse(byte[] payload)
        {
            if (payload == null || payload.Length < 1)
            {
                return null;
            }

            int offset = 0;
            TnfsDirectoryEntry entry = new TnfsDirectoryEntry();

            // Attributes (1 byte)
            entry.Attributes = (TnfsFileAttributes)payload[offset];
            offset++;

            // Filename (null-terminated)
            entry.Name = ReadNullTerminatedString(payload, offset);
            offset += entry.Name.Length + 1; // +1 for null terminator

            // Size (4 bytes) if present
            if (offset + 4 <= payload.Length)
            {
                entry.Size = TnfsEndian.ReadUInt32(payload, offset);
                offset += 4;
            }

            // Modified time (4 bytes) if present
            if (offset + 4 <= payload.Length)
            {
                entry.ModifiedTime = TnfsEndian.ReadUInt32(payload, offset);
            }

            return entry;
        }

        /// <summary>
        /// Build CLOSEDIR request payload.
        /// </summary>
        /// <param name="dirHandle">Directory handle</param>
        /// <returns>Payload bytes</returns>
        public static byte[] BuildCloseDirRequest(ushort dirHandle)
        {
            byte[] payload = new byte[2];
            TnfsEndian.WriteUInt16(payload, 0, dirHandle);
            return payload;
        }

        /// <summary>
        /// Parse CLOSEDIR request payload.
        /// </summary>
        /// <param name="payload">Payload bytes</param>
        /// <returns>Directory handle or null if invalid</returns>
        public static ushort? ParseCloseDirRequest(byte[] payload)
        {
            if (payload == null || payload.Length < 2)
            {
                return null;
            }

            return TnfsEndian.ReadUInt16(payload, 0);
        }

        #endregion

        #region File Commands

        /// <summary>
        /// Build OPEN request payload.
        /// </summary>
        /// <param name="path">File path</param>
        /// <param name="flags">Open flags</param>
        /// <param name="mode">Unix file permissions</param>
        /// <returns>Payload bytes</returns>
        public static byte[] BuildOpenRequest(string path, TnfsOpenFlags flags, ushort mode)
        {
            using (System.IO.MemoryStream ms = new System.IO.MemoryStream())
            {
                // Flags (2 bytes, little-endian)
                byte[] flagsBytes = new byte[2];
                TnfsEndian.WriteUInt16(flagsBytes, 0, (ushort)flags);
                ms.Write(flagsBytes, 0, 2);

                // Mode (2 bytes, little-endian)
                byte[] modeBytes = new byte[2];
                TnfsEndian.WriteUInt16(modeBytes, 0, mode);
                ms.Write(modeBytes, 0, 2);

                // Filename (null-terminated)
                byte[] pathBytes = Encoding.ASCII.GetBytes(path);
                ms.Write(pathBytes, 0, pathBytes.Length);
                ms.WriteByte(0);

                return ms.ToArray();
            }
        }

        /// <summary>
        /// Parse OPEN request payload.
        /// </summary>
        /// <param name="payload">Payload bytes</param>
        /// <param name="flags">Open flags</param>
        /// <param name="mode">Unix file permissions</param>
        /// <param name="path">File path</param>
        /// <returns>True if parsed successfully</returns>
        public static bool ParseOpenRequest(byte[] payload, out TnfsOpenFlags flags, out ushort mode, out string path)
        {
            flags = TnfsOpenFlags.O_RDONLY;
            mode = 0;
            path = string.Empty;

            if (payload == null || payload.Length < 5)
            {
                return false;
            }

            // Flags (2 bytes, little-endian)
            flags = (TnfsOpenFlags)TnfsEndian.ReadUInt16(payload, 0);

            // Mode (2 bytes, little-endian)
            mode = TnfsEndian.ReadUInt16(payload, 2);

            // Filename (null-terminated)
            path = ReadNullTerminatedString(payload, 4);

            return true;
        }

        /// <summary>
        /// Build OPEN response payload.
        /// </summary>
        /// <param name="fileHandle">File handle</param>
        /// <returns>Payload bytes</returns>
        public static byte[] BuildOpenResponse(ushort fileHandle)
        {
            byte[] payload = new byte[2];
            TnfsEndian.WriteUInt16(payload, 0, fileHandle);
            return payload;
        }

        /// <summary>
        /// Parse OPEN response payload.
        /// </summary>
        /// <param name="payload">Payload bytes</param>
        /// <returns>File handle or null if invalid</returns>
        public static ushort? ParseOpenResponse(byte[] payload)
        {
            if (payload == null || payload.Length < 2)
            {
                return null;
            }

            return TnfsEndian.ReadUInt16(payload, 0);
        }

        /// <summary>
        /// Build READ request payload.
        /// </summary>
        /// <param name="fileHandle">File handle</param>
        /// <param name="length">Number of bytes to read</param>
        /// <returns>Payload bytes</returns>
        public static byte[] BuildReadRequest(ushort fileHandle, ushort length)
        {
            byte[] payload = new byte[4];
            TnfsEndian.WriteUInt16(payload, 0, fileHandle);
            TnfsEndian.WriteUInt16(payload, 2, length);
            return payload;
        }

        /// <summary>
        /// Parse READ request payload.
        /// </summary>
        /// <param name="payload">Payload bytes</param>
        /// <param name="fileHandle">File handle</param>
        /// <param name="length">Number of bytes to read</param>
        /// <returns>True if parsed successfully</returns>
        public static bool ParseReadRequest(byte[] payload, out ushort fileHandle, out ushort length)
        {
            fileHandle = 0;
            length = 0;

            if (payload == null || payload.Length < 4)
            {
                return false;
            }

            fileHandle = TnfsEndian.ReadUInt16(payload, 0);
            length = TnfsEndian.ReadUInt16(payload, 2);
            return true;
        }

        /// <summary>
        /// Build READ response payload.
        /// </summary>
        /// <param name="data">File data</param>
        /// <returns>Payload bytes</returns>
        public static byte[] BuildReadResponse(byte[] data)
        {
            ushort bytesRead = (ushort)data.Length;
            byte[] payload = new byte[2 + bytesRead];

            // Bytes read (2 bytes, little-endian)
            TnfsEndian.WriteUInt16(payload, 0, bytesRead);

            // File data
            Array.Copy(data, 0, payload, 2, bytesRead);

            return payload;
        }

        /// <summary>
        /// Parse READ response payload.
        /// </summary>
        /// <param name="payload">Payload bytes</param>
        /// <returns>File data or null if invalid</returns>
        public static byte[]? ParseReadResponse(byte[] payload)
        {
            if (payload == null || payload.Length < 2)
            {
                return null;
            }

            ushort bytesRead = TnfsEndian.ReadUInt16(payload, 0);

            if (payload.Length < 2 + bytesRead)
            {
                return null;
            }

            byte[] data = new byte[bytesRead];
            Array.Copy(payload, 2, data, 0, bytesRead);
            return data;
        }

        /// <summary>
        /// Build CLOSE request payload.
        /// </summary>
        /// <param name="fileHandle">File handle</param>
        /// <returns>Payload bytes</returns>
        public static byte[] BuildCloseRequest(ushort fileHandle)
        {
            byte[] payload = new byte[2];
            TnfsEndian.WriteUInt16(payload, 0, fileHandle);
            return payload;
        }

        /// <summary>
        /// Parse CLOSE request payload.
        /// </summary>
        /// <param name="payload">Payload bytes</param>
        /// <returns>File handle or null if invalid</returns>
        public static ushort? ParseCloseRequest(byte[] payload)
        {
            if (payload == null || payload.Length < 2)
            {
                return null;
            }

            return TnfsEndian.ReadUInt16(payload, 0);
        }

        /// <summary>
        /// Build STAT request payload.
        /// </summary>
        /// <param name="path">File or directory path</param>
        /// <returns>Payload bytes</returns>
        public static byte[] BuildStatRequest(string path)
        {
            return Encoding.ASCII.GetBytes(path + '\0');
        }

        /// <summary>
        /// Parse STAT request payload.
        /// </summary>
        /// <param name="payload">Payload bytes</param>
        /// <returns>File path</returns>
        public static string ParseStatRequest(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
            {
                return string.Empty;
            }

            return ReadNullTerminatedString(payload, 0);
        }

        /// <summary>
        /// Build STAT response payload.
        /// </summary>
        /// <param name="stat">File stat information</param>
        /// <returns>Payload bytes</returns>
        public static byte[] BuildStatResponse(TnfsFileStat stat)
        {
            byte[] payload = new byte[19]; // 4+4+4+4+1+2

            int offset = 0;

            // Size (4 bytes, little-endian)
            TnfsEndian.WriteUInt32(payload, offset, (uint)stat.Size);
            offset += 4;

            // Modified time (4 bytes, little-endian)
            TnfsEndian.WriteUInt32(payload, offset, stat.ModifiedTime);
            offset += 4;

            // Creation time (4 bytes, little-endian)
            TnfsEndian.WriteUInt32(payload, offset, stat.CreationTime);
            offset += 4;

            // Access time (4 bytes, little-endian)
            TnfsEndian.WriteUInt32(payload, offset, stat.AccessTime);
            offset += 4;

            // Attributes (1 byte)
            payload[offset] = (byte)stat.Attributes;
            offset++;

            // Permissions (2 bytes, little-endian)
            TnfsEndian.WriteUInt16(payload, offset, stat.Permissions);

            return payload;
        }

        /// <summary>
        /// Parse STAT response payload.
        /// </summary>
        /// <param name="payload">Payload bytes</param>
        /// <returns>File stat or null if invalid</returns>
        public static TnfsFileStat? ParseStatResponse(byte[] payload)
        {
            if (payload == null || payload.Length < 17)
            {
                return null;
            }

            TnfsFileStat stat = new TnfsFileStat();
            int offset = 0;

            // Size (4 bytes)
            stat.Size = TnfsEndian.ReadUInt32(payload, offset);
            offset += 4;

            // Modified time (4 bytes)
            stat.ModifiedTime = TnfsEndian.ReadUInt32(payload, offset);
            offset += 4;

            // Creation time (4 bytes)
            stat.CreationTime = TnfsEndian.ReadUInt32(payload, offset);
            offset += 4;

            // Access time (4 bytes)
            stat.AccessTime = TnfsEndian.ReadUInt32(payload, offset);
            offset += 4;

            // Attributes (1 byte)
            stat.Attributes = (TnfsFileAttributes)payload[offset];
            offset++;

            // Permissions (2 bytes) if present
            if (offset + 2 <= payload.Length)
            {
                stat.Permissions = TnfsEndian.ReadUInt16(payload, offset);
            }

            return stat;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Read null-terminated ASCII string from byte array.
        /// </summary>
        /// <param name="data">Byte array</param>
        /// <param name="offset">Start offset</param>
        /// <returns>String (without null terminator)</returns>
        private static string ReadNullTerminatedString(byte[] data, int offset)
        {
            List<byte> bytes = new List<byte>();

            while (offset < data.Length && data[offset] != 0)
            {
                bytes.Add(data[offset]);
                offset++;
            }

            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        #endregion
    }
}
