//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Storage.Core;  // IBlockFileSystem moved to Core to fix circular dependency
using RetroIO.Storage.Ndfs.Boot;
using RetroIO.Storage.Ndfs.Elements;
using RetroIO.Storage.Ndfs.Structures;

namespace RetroIO.Storage.Ndfs.FileSystems
{
    /// <summary>
    /// Extended interface providing NDFS-specific functionality.
    /// This interface exposes Nord-specific features not available in the generic filesystem interface.
    /// </summary>
    public interface INdfsFileSystem : IBlockFileSystem
    {
        // ==================== User Management ====================

        /// <summary>
        /// Gets all users defined in the filesystem.
        /// </summary>
        IReadOnlyList<UserEntry> GetUsers();

        /// <summary>
        /// Gets a specific user by index.
        /// </summary>
        UserEntry? GetUser(int userIndex);

        // ==================== Object blocks (files per user) ====================

        /// <summary>
        /// Grants a user additional object blocks, raising the number of files they may hold.
        /// </summary>
        /// <param name="userNameOrIndex">
        /// User name (case-insensitive) or user index.
        /// </param>
        /// <param name="count">
        /// Object blocks to ADD. Must be 1 or more.
        /// </param>
        /// <returns>
        /// The user's new maximum object block count, 2..16.
        /// </returns>
        /// <remarks>
        /// The library equivalent of the SINTRAN operator command
        /// <c>@GIVE-OBJECT-BLOCKS user,count</c>, which likewise ADDS to the
        /// maximum rather than setting it. One block is 256 files; the ceiling is 16 blocks,
        /// i.e. 4096 files. Blocks are allocated on demand, so this moves only the maximum.
        /// </remarks>
        int GiveObjectBlocks(string userNameOrIndex, int count = 1);

        /// <summary>
        /// Lowers a user's object-block maximum.
        /// </summary>
        /// <param name="userNameOrIndex">
        /// User name (case-insensitive) or user index.
        /// </param>
        /// <param name="count">
        /// Object blocks to remove. Must be 1 or more.
        /// </param>
        /// <returns>
        /// The user's new maximum object block count.
        /// </returns>
        /// <remarks>
        /// This has no SINTRAN equivalent — no command to remove object blocks is documented.
        /// It exists so a tool can undo its own grant, and refuses to drop the maximum below the
        /// number of blocks actually holding files.
        /// </remarks>
        int TakeObjectBlocks(string userNameOrIndex, int count = 1);

        /// <summary>
        /// Reports a user's object-block position: what they hold and what they may hold.
        /// </summary>
        /// <param name="userNameOrIndex">
        /// User name (case-insensitive) or user index.
        /// </param>
        /// <returns>
        /// The user's block counts, file counts, and whether more blocks can be granted.
        /// </returns>
        ObjectBlockInfo GetObjectBlockInfo(string userNameOrIndex);

        /// <summary>
        /// Adds a new user to the filesystem.
        /// </summary>
        bool AddUser(string userName, uint reservedPages);

        /// <summary>
        /// Removes a user from the filesystem.
        /// </summary>
        bool RemoveUser(int userIndex);

        /// <summary>
        /// Updates a user's quota and writes it to the device.
        /// </summary>
        bool UpdateUserQuota(int userIndex, uint newReservedPages);

        /// <summary>
        /// Clears (sets to 0) a user's password.
        /// </summary>
        /// <param name="userIndex">
        /// User index to clear password for
        /// </param>
        /// <returns>
        /// True if password was cleared successfully
        /// </returns>
        bool ClearUserPassword(int userIndex);

        /// <summary>
        /// Clears (sets to 0) a user's password by username.
        /// </summary>
        /// <param name="userName">
        /// Username to clear password for
        /// </param>
        /// <returns>
        /// True if user found and password cleared successfully
        /// </returns>
        bool ClearUserPassword(string userName);

        /// <summary>
        /// Gets the exact manual instructions for clearing a user's password using FILE-SYSTEM-INVESTIGATOR.
        /// </summary>
        /// <param name="userIndex">
        /// User index to get instructions for
        /// </param>
        /// <returns>
        /// Detailed instructions string, or null if user not found
        /// </returns>
        string? GetPasswordClearInstructions(int userIndex);

        /// <summary>
        /// Gets the exact manual instructions for clearing a user's password using FILE-SYSTEM-INVESTIGATOR.
        /// </summary>
        /// <param name="userName">
        /// Username to get instructions for
        /// </param>
        /// <returns>
        /// Detailed instructions string, or null if user not found
        /// </returns>
        string? GetPasswordClearInstructions(string userName);

        // ==================== Bitmap Operations ====================

        /// <summary>
        /// Gets the bit file containing the allocation bitmap.
        /// </summary>
        BitFile GetBitFile();

        /// <summary>
        /// Checks if a specific block is used.
        /// </summary>
        bool IsBlockUsed(uint blockId);

        /// <summary>
        /// Gets the number of unreserved pages.
        /// </summary>
        uint GetUnreservedPages();

        /// <summary>
        /// Gets the total number of available pages.
        /// </summary>
        uint GetPagesAvailable();

        // ==================== Object File Operations ====================

        /// <summary>
        /// Gets all object entries in the filesystem.
        /// </summary>
        IReadOnlyList<ObjectEntry> GetObjectEntries();

        /// <summary>
        /// Gets a specific object entry.
        /// </summary>
        ObjectEntry? GetObjectEntry(string objectName, string userName);

        /// <summary>
        /// Gets the object file structure.
        /// </summary>
        ObjectFile GetObjectFile();

        // ==================== Master Block Access ====================

        /// <summary>
        /// Gets the master block information.
        /// </summary>
        MasterBlock GetMasterBlock();

        /// <summary>
        /// Gets the directory name from the master block.
        /// </summary>
        string GetDirectoryName();

        /// <summary>
        /// Sets the directory name in the master block.
        /// </summary>
        bool SetDirectoryName(string name);

        /// <summary>
        /// Clears the "directory entered / in use" interlock (flag-word bit 15) in the
        /// Extended Info block and rewrites the block with a freshly recomputed additive
        /// checksum. This mirrors the SINTRAN kernel's release routine REENB (40162B),
        /// which clears bit 15 on a clean release; an image whose bit 15 is still set was
        /// left "entered" (not cleanly unmounted) by <see cref="MasterBlock.ExtLastSystemNumber"/>.
        /// No-op (returns false) when the volume has no valid Extended Info (e.g. FLOMON
        /// floppies) or the flag was already clear.
        /// </summary>
        /// <returns>
        /// True if the flag was set and has now been cleared and persisted.
        /// </returns>
        bool ClearEnteredDirectory();

        // ==================== Diagnostics ====================

        /// <summary>
        /// Generates a bitmap consistency report.
        /// </summary>
        string GenerateBitmapReport();

        /// <summary>
        /// Dumps filesystem information for debugging.
        /// </summary>
        string DumpFileSystemInfo();

        /// <summary>
        /// Verifies filesystem integrity.
        /// </summary>
        bool VerifyFileSystemIntegrity();

        // ==================== Boot Sector Operations ====================

        /// <summary>
        /// Loads boot code from NDFS block 0.
        /// Supports BPUN, FLOMON, and raw binary formats.
        /// </summary>
        /// <returns>
        /// Boot code information, or null if no valid boot code found
        /// </returns>
        NdfsBootCode? LoadBootCode();

        /// <summary>
        /// Detects the boot format without loading full code.
        /// </summary>
        /// <returns>
        /// Boot format detected (None, Binary, BPUN, or FloMon)
        /// </returns>
        NdfsBootFormat DetectBootFormat();

        /// <summary>
        /// Checks if the NDFS disk is bootable.
        /// </summary>
        /// <returns>
        /// True if valid boot code exists in block 0
        /// </returns>
        bool IsBootable();

        /// <summary>
        /// Detects the hard-disk controller family (SMD/ECC, Winchester, SCSI) targeted by a
        /// raw-binary bootstrap. Returns <see cref="NdfsBootControllerType.Unknown"/> for
        /// BPUN/FLOMON (floppy) boots or non-bootable disks.
        /// </summary>
        NdfsBootControllerType DetectBootControllerType();

        // ==================== Contiguous Files ====================

        /// <summary>
        /// Creates an empty contiguous file occupying a fixed run of <paramref name="pages"/> pages.
        /// </summary>
        /// <param name="path">
        /// The NDFS path, optionally carrying a <c>(USER)</c> prefix.
        /// </param>
        /// <param name="pages">
        /// The number of pages to reserve. Must be at least one.
        /// </param>
        /// <remarks>
        /// <para>
        /// This is SINTRAN's <c>@CREATE-FILE name pages</c>, the form that produces a file the
        /// machine reports as <c>CONTINUOUS FILE</c>. All pages are allocated up front and are
        /// consecutive on the pack, but the file is created empty.
        /// </para>
        /// <para>
        /// The reservation is fixed. Writing more than <paramref name="pages"/> pages into the file
        /// later fails instead of growing it, matching SINTRAN, which cannot extend a contiguous
        /// run once the following pages belong to other files.
        /// </para>
        /// </remarks>
        void CreateContiguousFile(string path, uint pages);

        // ==================== PropertyBag Support ====================

        /// <summary>
        /// Reads a file and populates PropertyBag with NDFS-specific metadata.
        /// </summary>
        /// <param name="path">
        /// Path to file
        /// </param>
        /// <param name="propertyBag">
        /// Dictionary to populate with properties
        /// </param>
        /// <returns>
        /// File contents
        /// </returns>
        byte[] ReadFileWithProperties(string path, IDictionary<string, object> propertyBag);

        /// <summary>
        /// Writes a file and applies NDFS-specific metadata from PropertyBag.
        /// </summary>
        /// <param name="path">
        /// Path to file
        /// </param>
        /// <param name="data">
        /// File contents
        /// </param>
        /// <param name="propertyBag">
        /// Dictionary containing properties to apply
        /// </param>
        void WriteFileWithProperties(string path, byte[] data, IDictionary<string, object> propertyBag);

        /// <summary>
        /// Gets PropertyBag for a file without reading its contents.
        /// </summary>
        /// <param name="path">
        /// Path to file
        /// </param>
        /// <returns>
        /// Dictionary containing file properties, or null if not found
        /// </returns>
        IDictionary<string, object>? GetFileProperties(string path);

        // ==================== Extended Master Block Information ====================

        /// <summary>
        /// Gets the Extended Master Block information (if available).
        /// Extended Info is only valid for hard disks (not FLOMON floppy disks).
        /// </summary>
        /// <returns>
        /// Dictionary with Extended Info fields, or null if not valid
        /// </returns>
        IDictionary<string, object>? GetExtendedMasterBlockInfo();

        /// <summary>
        /// Checks if the Extended Master Block information is valid.
        /// Valid Extended Info has: Reserved fields = 0, Checksum != 0.
        /// FLOMON floppy disks have non-zero Reserved fields and should return false.
        /// </summary>
        /// <returns>
        /// True if Extended Info is valid (hard disk), false if FLOMON (floppy)
        /// </returns>
        bool HasValidExtendedInfo();

        // ==================== SINTRAN Initial-Command Buffer ====================

        /// <summary>
        /// Locates and decodes the SINTRAN III initial-command buffer (the commands SINTRAN runs first
        /// at every restart, as printed by <c>@LIST-INITIAL-COMMANDS</c>). The buffer has no file form:
        /// it lives at kernel symbol INIBU inside the command segment stored in (SYSTEM)SEGFIL0:DATA.
        /// Returns null when the pack is not a SINTRAN system disk or the buffer cannot be located.
        /// </summary>
        SintranInitialCommandBuffer? ReadSintranInitialCommands();

        /// <summary>
        /// Replaces the SINTRAN III initial-command buffer with the supplied commands and writes it back
        /// to SEGFIL0 in place (no re-allocation of the contiguous system file). Returns false when the
        /// pack has no locatable buffer. Throws on a read-only device or when the commands overflow the
        /// buffer / contain an embedded quote.
        /// </summary>
        bool WriteSintranInitialCommands(IReadOnlyList<string> commands);

        // ==================== SINTRAN CPU number / type and segments ====================

        /// <summary>
        /// Reads the CPU number (<c>SYSNO</c>, <c>FCPUN</c>) and CPU type (<c>HWINF+2</c>) from both disk
        /// copies of the resident data page (segments <c>S3IDPIT</c> and <c>S3SDPIT</c>), the generation
        /// symbols <c>33CPN</c> / <c>33CPU</c>, and the directory's "entered by system N" stamp. Word
        /// addresses come from the pack's own symbol lists, segment numbers from the RT-loader name
        /// records, pages from the segment table. Throws <c>SintranCpuIdentityException</c> on a pack that
        /// lacks any of those.
        /// </summary>
        SintranCpuIdentityInfo ReadSintranCpuIdentity();

        /// <summary>
        /// Sets the CPU number in both copies, the CPU type when given, rewrites <c>33CPN</c> / <c>33CPU</c>,
        /// clears the directory stamp unless <paramref name="keepStamp"/>, and reads everything back.
        /// Nothing is written unless every target word is reachable.
        /// </summary>
        SintranCpuIdentityInfo WriteSintranCpuIdentity(int cpuNumber, int? cpuType, bool keepStamp);

        /// <summary>
        /// Clears the directory's "entered by system N" stamp: flag word bit 15 and the system number,
        /// with a fresh checksum. Reports what the stamp was.
        /// </summary>
        void ReleaseDirectory(out bool wasEntered, out int wasSystemNumber);

        /// <summary>
        /// Lists every SINTRAN segment on the pack with its name, segment file and first disk page.
        /// </summary>
        SintranSegmentList ListSintranSegments();

        /// <summary>
        /// The pages of one segment as they are on the pack.
        /// </summary>
        byte[] ExportSintranSegment(SintranSegmentList list, int index);

        /// <summary>
        /// Writes bytes over one segment's pages; a last partial page keeps its remaining bytes.
        /// </summary>
        void ImportSintranSegment(SintranSegmentList list, int index, ReadOnlySpan<byte> data);

        /// <summary>
        /// Gets the number of 2048-byte pages the device holds.
        /// </summary>
        uint TotalPages { get; }

        /// <summary>
        /// Reads one raw disk page.
        /// </summary>
        byte[] ReadDiskPage(uint page);

        /// <summary>
        /// Overwrites one raw disk page.
        /// </summary>
        void WriteDiskPage(uint page, ReadOnlySpan<byte> pageData);
    }
}
