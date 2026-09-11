//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;

namespace RetroIO.Storage.Ndfs.Elements
{
    /// <summary>
    /// NDFS file type flags.
    /// These flags indicate special characteristics of files in the filesystem.
    /// A file can have multiple flags set (hence the [Flags] attribute).
    /// </summary>
    [Flags]
    public enum FileTypes : ushort
    {
        /// <summary>
        /// No special file type.
        /// </summary>
        None = 0,

        /// <summary>
        /// Terminal file - special terminal device file.
        /// </summary>
        TerminalFile = 1 << 0,  // 0x0001

        /// <summary>
        /// Peripheral file - represents peripheral device.
        /// </summary>
        PeripheralFile = 1 << 1,  // 0x0002

        /// <summary>
        /// Spooling file - used for print spooling.
        /// </summary>
        SpoolingFile = 1 << 2,  // 0x0004

        /// <summary>
        /// Indexed file - file uses indexed block allocation (most common).
        /// Data accessed through index blocks pointing to data blocks.
        /// </summary>
        IndexedFile = 1 << 3,  // 0x0008

        /// <summary>
        /// Contiguous file - file uses contiguous block allocation.
        /// All data blocks are sequential for faster access.
        /// </summary>
        ContiguousFile = 1 << 4,  // 0x0010

        /// <summary>
        /// Allocated file - file has pre-allocated space.
        /// </summary>
        AllocatedFile = 1 << 5,  // 0x0020

        /// <summary>
        /// Magnetic tape file - special tape device file.
        /// </summary>
        MagneticTapeFile = 1 << 6,  // 0x0040

        /// <summary>
        /// Library file - code library or shared object.
        /// </summary>
        LibraryFile = 1 << 7,  // 0x0080
    }

    /// <summary>
    /// Helper methods for FileTypes enum.
    /// </summary>
    public static class FileTypesExtensions
    {
        /// <summary>
        /// Converts FileTypes flags to a readable string.
        /// </summary>
        /// <param name="fileType">File type flags</param>
        /// <returns>String representation (e.g., "IC" for Indexed Contiguous)</returns>
        public static string ToShortString(this FileTypes fileType)
        {
            if (fileType == FileTypes.None)
                return "-";

            string result = "";
            if ((fileType & FileTypes.TerminalFile) != 0) result += "T";
            if ((fileType & FileTypes.PeripheralFile) != 0) result += "P";
            if ((fileType & FileTypes.SpoolingFile) != 0) result += "S";
            if ((fileType & FileTypes.IndexedFile) != 0) result += "I";
            if ((fileType & FileTypes.ContiguousFile) != 0) result += "C";
            if ((fileType & FileTypes.AllocatedFile) != 0) result += "A";
            if ((fileType & FileTypes.MagneticTapeFile) != 0) result += "M";
            if ((fileType & FileTypes.LibraryFile) != 0) result += "L";

            return result;
        }

        /// <summary>
        /// Converts FileTypes flags to a long descriptive string.
        /// </summary>
        /// <param name="fileType">File type flags</param>
        /// <returns>Descriptive string</returns>
        public static string ToLongString(this FileTypes fileType)
        {
            if (fileType == FileTypes.None)
                return "Standard file";

            var flags = new System.Collections.Generic.List<string>();

            if ((fileType & FileTypes.TerminalFile) != 0) flags.Add("Terminal");
            if ((fileType & FileTypes.PeripheralFile) != 0) flags.Add("Peripheral");
            if ((fileType & FileTypes.SpoolingFile) != 0) flags.Add("Spooling");
            if ((fileType & FileTypes.IndexedFile) != 0) flags.Add("Indexed");
            if ((fileType & FileTypes.ContiguousFile) != 0) flags.Add("Contiguous");
            if ((fileType & FileTypes.AllocatedFile) != 0) flags.Add("Allocated");
            if ((fileType & FileTypes.MagneticTapeFile) != 0) flags.Add("MagneticTape");
            if ((fileType & FileTypes.LibraryFile) != 0) flags.Add("Library");

            return string.Join(", ", flags);
        }

        /// <summary>
        /// Checks if file is an indexed file.
        /// </summary>
        public static bool IsIndexed(this FileTypes fileType)
        {
            return (fileType & FileTypes.IndexedFile) != 0;
        }

        /// <summary>
        /// Checks if file is contiguous.
        /// </summary>
        public static bool IsContiguous(this FileTypes fileType)
        {
            return (fileType & FileTypes.ContiguousFile) != 0;
        }

        /// <summary>
        /// Checks if file is a device file (terminal, peripheral, or tape).
        /// </summary>
        public static bool IsDevice(this FileTypes fileType)
        {
            return (fileType & (FileTypes.TerminalFile | FileTypes.PeripheralFile | FileTypes.MagneticTapeFile)) != 0;
        }

        /// <summary>
        /// Checks if file is a regular data file.
        /// </summary>
        public static bool IsRegularFile(this FileTypes fileType)
        {
            return !IsDevice(fileType);
        }
    }
}
