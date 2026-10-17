//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BSD.Structures;
using RetroIO.Storage.Local.V7.Structures;

namespace RetroIO.Storage.Local.V7.Boot
{
    /// <summary>
    /// Handles reading and extraction of V7 boot blocks.
    /// Boot block is always at sector 0 and is always 512 bytes.
    /// </summary>
    public class V7BootBlock
    {
        /// <summary>
        /// Boot block binary data (512 bytes).
        /// </summary>
        public byte[] Data { get; private set; }

        public V7BootBlock(byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            if (data.Length != V7Constants.BOOT_SIZE)
                throw new ArgumentException($"Boot block must be exactly {V7Constants.BOOT_SIZE} bytes");

            Data = data;
        }

        /// <summary>
        /// Reads boot block from a block device.
        /// </summary>
        public static V7BootBlock Read(IBlockDevice device)
        {
            if (device == null)
                throw new ArgumentNullException(nameof(device));

            int physicalSectorSize = device.BlockSize;
            byte[] bootData = new byte[V7Constants.BOOT_SIZE];

            if (physicalSectorSize == 512)
            {
                // Read sector 0 directly
                device.ReadBlock(BlockAddress.FromLBA(0), bootData);
            }
            else if (physicalSectorSize == 1024)
            {
                // Read first 512 bytes of block 0
                byte[] block = new byte[1024];
                device.ReadBlock(BlockAddress.FromLBA(0), block);
                Array.Copy(block, 0, bootData, 0, V7Constants.BOOT_SIZE);
            }
            else
            {
                throw new NotSupportedException($"Unsupported sector size: {physicalSectorSize}");
            }

            return new V7BootBlock(bootData);
        }

        /// <summary>
        /// Extracts boot block to a file.
        /// </summary>
        public void SaveToFile(string path)
        {
            System.IO.File.WriteAllBytes(path, Data);
        }

        /// <summary>
        /// Checks if boot block appears to contain code (heuristic check).
        /// </summary>
        public bool AppearsValid()
        {
            // Very basic heuristic: check if it's not all zeros
            int nonZeroBytes = 0;

            for (int i = 0; i < Data.Length; i++)
            {
                if (Data[i] != 0)
                {
                    nonZeroBytes++;
                    if (nonZeroBytes > 10)
                        return true;
                }
            }

            return false;
        }

        public override string ToString()
        {
            int nonZeroBytes = 0;
            for (int i = 0; i < Data.Length; i++)
            {
                if (Data[i] != 0)
                    nonZeroBytes++;
            }

            return $"Boot Block: {V7Constants.BOOT_SIZE} bytes, {nonZeroBytes} non-zero bytes, Valid={AppearsValid()}";
        }
    }
}

