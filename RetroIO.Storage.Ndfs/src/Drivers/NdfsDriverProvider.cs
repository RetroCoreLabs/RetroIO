//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Text;
using RetroIO.Storage.Core;
using RetroIO.Storage.Ndfs.BlockDevices;
using RetroIO.Storage.Ndfs.Structures;

namespace RetroIO.Storage.Ndfs.Drivers
{
    /// <summary>
    /// NDFS driver provider for registration with FileSystemDriverFactory.
    /// </summary>
    public sealed class NdfsDriverProvider : FileSystemDriverProviderBase
    {
        private static readonly string[] SupportedExtensions = { ".nd", ".ndfs", ".nord", ".ndf" };

        public override string FileSystemType => "NDFS";

        public override string Description => "Nord Disk File System";

        /// <summary>
        /// NDFS requires at least one page (2048 bytes) to validate the master block.
        /// </summary>
        public override int RequiredHeaderBytes => NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;

        public override float DetectConfidence(DetectionHints hints)
        {
            if (hints == null)
                return 0.0f;

            // Quick size check - NDFS needs at least one page
            if (hints.FileSize < NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE)
                return 0.0f;

            // Check if size is a multiple of page size
            if (hints.FileSize % NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE != 0)
                return 0.0f;

            // Check extension first for confidence boost
            bool hasNdfsExtension = Array.Exists(SupportedExtensions,
                ext => string.Equals(hints.FileExtension, ext, StringComparison.OrdinalIgnoreCase));

            // Check if we have enough header data
            if (hints.HeaderBytes == null || hints.HeaderBytes.Length < NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE)
            {
                // If we have the right extension but not enough data, give low confidence
                return hasNdfsExtension ? 0.2f : 0.0f;
            }

            // Try to parse master block
            MasterBlock? masterBlock = null;
            try
            {
                masterBlock = MasterBlock.FromBytes(hints.HeaderBytes);
            }
            catch
            {
                // Failed to parse - if we have extension, give very low confidence
                return hasNdfsExtension ? 0.1f : 0.0f;
            }

            if (masterBlock == null)
                return hasNdfsExtension ? 0.3f : 0.0f;

            // Score against the kernel-verified invariants, bounding the block pointers by the real
            // device size. MasterBlock.IsValid() is deliberately NOT used here: it accepts a page
            // whose 32 bytes at offset 2016 merely hold a printable name OR any single non-zero
            // pointer-shaped word, so nearly any 2048-aligned file would score as NDFS.
            uint devicePages = (uint)(hints.FileSize / NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE);
            float score = masterBlock.DetectionScore(devicePages);

            if (score <= 0.0f)
            {
                // Structurally not NDFS. A matching extension is not evidence on its own — a
                // renamed file must not auto-mount — so keep this below any mount threshold.
                return hasNdfsExtension ? 0.2f : 0.0f;
            }

            // A matching extension is a small corroborating boost, never the deciding factor.
            if (hasNdfsExtension)
            {
                score += 0.01f;
                if (score > 1.0f)
                    score = 1.0f;
            }

            return score;
        }

        public override IFileSystemDriver CreateDriver()
        {
            return new NdfsDriver();
        }
    }
}
