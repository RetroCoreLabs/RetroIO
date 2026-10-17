//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Storage.Core
{
    /// <summary>
    /// Produces a <see cref="VolumeInfo"/> for any driver.
    ///
    /// If the driver implements <see cref="IVolumeInfoProvider"/> its own richer description
    /// is used. Otherwise a generic one is assembled from what every IFileSystemDriver must
    /// expose — type, label, capabilities, and block geometry when it offers block access.
    /// That guarantees the disk-info UI always has something to show, so a driver that has
    /// not been taught about volume info yet degrades to a useful minimum instead of an
    /// error message.
    /// </summary>
    public static class VolumeInfoBuilder
    {
        /// <summary>
        /// Builds volume info for a mounted driver.
        /// </summary>
        /// <param name="driver">The mounted driver.</param>
        /// <param name="imagePath">Backing image path, when known.</param>
        /// <param name="isReadOnly">Whether the volume was mounted read-only.</param>
        /// <returns>Volume info; never null for a non-null driver.</returns>
        public static VolumeInfo Build(IFileSystemDriver driver, string imagePath, bool isReadOnly)
        {
            if (driver == null)
            {
                throw new ArgumentNullException(nameof(driver));
            }

            // Prefer the driver's own description when it has one.
            if (driver is IVolumeInfoProvider provider)
            {
                VolumeInfo? own = provider.GetVolumeInfo();
                if (own != null)
                {
                    // The driver may not know its own image path or mount flags - fill them in.
                    if (string.IsNullOrEmpty(own.ImagePath))
                    {
                        own.ImagePath = imagePath;
                    }

                    own.IsReadOnly = isReadOnly;
                    AppendCapabilities(own, driver);
                    return own;
                }
            }

            return BuildGeneric(driver, imagePath, isReadOnly);
        }

        /// <summary>
        /// Assembles the fallback description used when a driver has no volume-info support.
        /// </summary>
        private static VolumeInfo BuildGeneric(IFileSystemDriver driver, string imagePath, bool isReadOnly)
        {
            var info = new VolumeInfo
            {
                FileSystemType = driver.FileSystemType,
                VolumeLabel = driver.VolumeLabel,
                ImagePath = imagePath,
                IsReadOnly = isReadOnly
            };

            var volume = info.AddSection("Volume", "\U0001F4C0");
            volume.Add("Filesystem", driver.FileSystemType);
            volume.Add("Volume label", string.IsNullOrEmpty(driver.VolumeLabel) ? "(none)" : driver.VolumeLabel);
            volume.Add("Access", isReadOnly ? "Read-only" : "Read/write");

            if (!string.IsNullOrEmpty(imagePath))
            {
                volume.Add("Image path", imagePath);
            }

            // Block geometry, when the driver exposes a block device.
            if (driver.HasBlockAccess)
            {
                BlockDeviceInfo? blockInfo = null;
                try
                {
                    blockInfo = driver.GetBlockDeviceInfo();
                }
                catch
                {
                    // A driver that advertises block access but cannot describe it is not an
                    // error worth surfacing here - just omit the section.
                }

                if (blockInfo != null)
                {
                    var geometry = info.AddSection("Geometry", "\U0001F4D0");
                    geometry.Add("Addressing", blockInfo.AddressMode.ToString());
                    geometry.Add("Block size", $"{blockInfo.BlockSize:N0} bytes");

                    if (blockInfo.TotalBlocks > 0)
                    {
                        geometry.Add("Total blocks", $"{blockInfo.TotalBlocks:N0}");
                        geometry.Add("Capacity",
                            $"{(long)blockInfo.TotalBlocks * blockInfo.BlockSize:N0} bytes");
                    }

                    if (blockInfo.TrackCount > 0)
                    {
                        geometry.Add("Tracks", blockInfo.TrackCount.ToString());
                    }

                    if (blockInfo.CylinderCount > 0)
                    {
                        geometry.Add("Cylinders", blockInfo.CylinderCount.ToString());
                    }

                    if (blockInfo.HeadCount > 0)
                    {
                        geometry.Add("Heads", blockInfo.HeadCount.ToString());
                    }
                }
            }

            AppendCapabilities(info, driver);
            return info;
        }

        /// <summary>
        /// Adds the driver capability summary. Applied to both provider-supplied and generic
        /// info so the UI always shows what the driver can actually do with this volume.
        /// </summary>
        private static void AppendCapabilities(VolumeInfo info, IFileSystemDriver driver)
        {
            var caps = info.AddSection("Capabilities", "\U0001F6E0");
            caps.Add("Read", driver.CanRead ? "Yes" : "No");
            caps.Add("Write", driver.CanWrite ? "Yes" : "No");
            caps.Add("Block access", driver.HasBlockAccess ? "Yes" : "No");
            caps.Add("Flags", driver.Capabilities.ToString());
        }
    }
}
