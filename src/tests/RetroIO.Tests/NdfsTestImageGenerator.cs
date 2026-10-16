//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using System.Text;
using RetroIO.Storage.Ndfs.BlockDevices;

namespace RetroIO.Tests
{
    /// <summary>
    /// Generates NDFS test images with proper BlockPointer structure.
    /// </summary>
    public static class NdfsTestImageGenerator
    {
        private const int PAGE_SIZE = NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;

        /// <summary>
        /// Creates an empty NDFS filesystem image.
        /// </summary>
        public static byte[] CreateEmptyFilesystem(string directoryName = "TESTDISK", int totalPages = 100)
        {
            var imageSize = PAGE_SIZE * totalPages;
            var image = new byte[imageSize];

            // Page 0: Master block (at offset 2016).
            // Layout matches the shape every REAL ND image has (verified across 53 of them):
            // object/user index blocks are Indexed (type 01) and exactly 2 blocks apart, the bit
            // file is Contiguous (type 00), and all three sit at or above the block-7 floor
            // (blocks 0-6 are system-reserved). The old fixture used pages 2/5/8 — below the floor
            // and 3 apart — a layout no real disk has, which the hardened detector rejects.
            WriteMasterBlock(image, directoryName, totalPages,
                objectFilePtr: 0x40000008,  // Indexed pointer to page 8
                userFilePtr: 0x4000000A,     // Indexed pointer to page 10 (= object + 2)
                bitFilePtr: 0x0000000C);     // Contiguous pointer to page 12

            // Page 8:  ObjectFile index block (empty - all zeros)
            // Page 10: UserFile index block (empty - all zeros)
            // Page 12: BitFile (bitmap)
            InitializeBitmap(image, 12, totalPages);

            return image;
        }

        /// <summary>
        /// Creates an NDFS filesystem with sample files and users.
        /// </summary>
        public static byte[] CreateFilesystemWithFiles()
        {
            var totalPages = 200;
            var imageSize = PAGE_SIZE * totalPages;
            var image = new byte[imageSize];

            // Page 0: Master block. Same realistic layout as CreateEmptyFilesystem — object/user
            // Indexed and exactly 2 apart, bit file Contiguous, all at/above the block-7 floor.
            WriteMasterBlock(image, "NORDISK", totalPages,
                objectFilePtr: 0x40000008,   // Indexed pointer to page 8
                userFilePtr: 0x4000000A,      // Indexed pointer to page 10 (= object + 2)
                bitFilePtr: 0x0000000C);      // Contiguous pointer to page 12

            // Page 8: ObjectFile index block with pointers to object data blocks
            // Pointer 0 -> page 16 (Contiguous)
            WriteBlockPointer(image, 8 * PAGE_SIZE + 0, 0x00000010);  // Contiguous to page 16

            // Page 10: UserFile index block with pointers to user data blocks
            // Pointer 0 -> page 18 (Contiguous)
            WriteBlockPointer(image, 10 * PAGE_SIZE + 0, 0x00000012); // Contiguous to page 18

            // Page 18: User data block with 3 users
            AddUserEntry(image, 18, 0, "SYSTEM", pagesReserved: 50, pagesUsed: 10);
            AddUserEntry(image, 18, 1, "USER1", pagesReserved: 30, pagesUsed: 15);
            AddUserEntry(image, 18, 2, "GUEST", pagesReserved: 20, pagesUsed: 0);

            // Page 16: Object data block with 3 files
            AddObjectEntry(image, 16, 0, "README", "TXT", 0, 0x00000014, 512);      // User 0, page 20
            AddObjectEntry(image, 16, 1, "PROGRAM", "BIN", 1, 0x00000015, 6144);   // User 1, page 21
            AddObjectEntry(image, 16, 2, "DATA", "DAT", 1, 0x00000018, 3072);      // User 1, page 24

            // Write file content
            WriteFileContent(image, 20, "This is a test NDFS filesystem.\nCreated by RetroFS test suite.\n");
            WriteFileContent(image, 21, GenerateBinaryData(6144));
            WriteFileContent(image, 24, GenerateBinaryData(3072));

            // Page 12: Bitmap
            InitializeBitmap(image, 12, totalPages);
            MarkPagesUsed(image, 12, 0, 30);  // Mark system and file pages as used

            return image;
        }

        private static void WriteMasterBlock(byte[] image, string directoryName, int totalPages,
                                            uint objectFilePtr, uint userFilePtr, uint bitFilePtr)
        {
            int offset = NdfsBlockDeviceDescriptor.MASTER_BLOCK_OFFSET;

            // Directory name (16 bytes, terminated with 0x27)
            var nameBytes = Encoding.ASCII.GetBytes(directoryName);
            Array.Copy(nameBytes, 0, image, offset, Math.Min(nameBytes.Length, 16));
            if (nameBytes.Length < 16)
                image[offset + nameBytes.Length] = 0x27;

            // Block pointers (4 bytes each, big-endian, with type in top 2 bits)
            WriteBlockPointer(image, offset + 0x10, objectFilePtr);
            WriteBlockPointer(image, offset + 0x14, userFilePtr);
            WriteBlockPointer(image, offset + 0x18, bitFilePtr);

            // Unreserved pages
            WriteBigEndian32(image, offset + 0x1C, (uint)(totalPages - 20));
        }

        private static void WriteBlockPointer(byte[] image, int offset, uint pointer)
        {
            image[offset] = (byte)(pointer >> 24);
            image[offset + 1] = (byte)(pointer >> 16);
            image[offset + 2] = (byte)(pointer >> 8);
            image[offset + 3] = (byte)pointer;
        }

        private static void AddUserEntry(byte[] image, int pageNum, int entryIndex, string userName,
                                         uint pagesReserved = 0, uint pagesUsed = 0)
        {
            int offset = (pageNum * PAGE_SIZE) + (entryIndex * 64);

            // Header byte: 0x81 = user entry in use
            image[offset] = 0x81;

            // User name (16 bytes at offset 2, terminated with 0x27)
            var nameBytes = Encoding.ASCII.GetBytes(userName);
            Array.Copy(nameBytes, 0, image, offset + 2, Math.Min(nameBytes.Length, 16));
            if (nameBytes.Length < 16)
                image[offset + 2 + nameBytes.Length] = 0x27;

            // Pages reserved (bytes 28-31, big-endian)
            image[offset + 28] = (byte)(pagesReserved >> 24);
            image[offset + 29] = (byte)(pagesReserved >> 16);
            image[offset + 30] = (byte)(pagesReserved >> 8);
            image[offset + 31] = (byte)pagesReserved;

            // Pages used (bytes 32-35, big-endian)
            image[offset + 32] = (byte)(pagesUsed >> 24);
            image[offset + 33] = (byte)(pagesUsed >> 16);
            image[offset + 34] = (byte)(pagesUsed >> 8);
            image[offset + 35] = (byte)pagesUsed;

            // User index (byte 37)
            image[offset + 37] = (byte)entryIndex;
        }

        private static void AddObjectEntry(byte[] image, int pageNum, int entryIndex,
                                           string objectName, string type, int userIndex,
                                           uint filePointer, uint byteCount)
        {
            int offset = (pageNum * PAGE_SIZE) + (entryIndex * 64);

            // Header byte: 0x80 = object entry in use
            image[offset] = 0x80;

            // Object name (16 bytes at offset 2, terminated with 0x27)
            var nameBytes = Encoding.ASCII.GetBytes(objectName);
            Array.Copy(nameBytes, 0, image, offset + 2, Math.Min(nameBytes.Length, 16));
            if (nameBytes.Length < 16)
                image[offset + 2 + nameBytes.Length] = 0x27;

            // Type (4 bytes at offset 18, terminated with 0x27)
            var typeBytes = Encoding.ASCII.GetBytes(type);
            Array.Copy(typeBytes, 0, image, offset + 18, Math.Min(typeBytes.Length, 4));
            if (typeBytes.Length < 4)
                image[offset + 18 + typeBytes.Length] = 0x27;

            // User index (byte 34)
            image[offset + 34] = (byte)userIndex;

            // Pages in file (bytes 52-55, big-endian)
            uint pages = (byteCount + 2047) / 2048;
            WriteBigEndian32(image, offset + 52, pages);

            // Bytes in file (bytes 56-59, big-endian) - stored as count-1
            WriteBigEndian32(image, offset + 56, byteCount - 1);

            // File pointer (bytes 60-63, big-endian, includes type)
            WriteBlockPointer(image, offset + 60, filePointer);
        }

        private static void InitializeBitmap(byte[] image, int pageNum, int totalPages)
        {
            int offset = pageNum * PAGE_SIZE;

            // Mark first 10 pages as used (system pages)
            for (int i = 0; i < 10; i++)
            {
                int byteIndex = i / 8;
                int bitIndex = i % 8;
                image[offset + byteIndex] |= (byte)(1 << bitIndex);
            }
        }

        private static void MarkPagesUsed(byte[] image, int bitmapPage, int startPage, int count)
        {
            int bitmapOffset = bitmapPage * PAGE_SIZE;

            for (int i = startPage; i < startPage + count; i++)
            {
                int byteIndex = i / 8;
                int bitIndex = i % 8;
                image[bitmapOffset + byteIndex] |= (byte)(1 << bitIndex);
            }
        }

        private static void WriteFileContent(byte[] image, int startPage, string content)
        {
            var bytes = Encoding.ASCII.GetBytes(content);
            WriteFileContent(image, startPage, bytes);
        }

        private static void WriteFileContent(byte[] image, int startPage, byte[] content)
        {
            int offset = startPage * PAGE_SIZE;
            Array.Copy(content, 0, image, offset, Math.Min(content.Length, image.Length - offset));
        }

        private static byte[] GenerateBinaryData(int size)
        {
            var data = new byte[size];
            var rnd = new Random(42); // Fixed seed for reproducibility
            rnd.NextBytes(data);
            return data;
        }

        private static void WriteBigEndian16(byte[] image, int offset, ushort value)
        {
            image[offset] = (byte)(value >> 8);
            image[offset + 1] = (byte)value;
        }

        private static void WriteBigEndian32(byte[] image, int offset, uint value)
        {
            image[offset] = (byte)(value >> 24);
            image[offset + 1] = (byte)(value >> 16);
            image[offset + 2] = (byte)(value >> 8);
            image[offset + 3] = (byte)value;
        }
    }
}
