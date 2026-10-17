//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using System.Text;
using RetroIO.Storage.Core;

namespace RetroIO.Storage.Ndfs.Boot
{
    /// <summary>
    /// Boot sector format detected in NDFS disk
    /// </summary>
    public enum NdfsBootFormat
    {
        /// <summary>
        /// No valid boot sector found
        /// </summary>
        None,

        /// <summary>
        /// Binary boot code (raw machine code)
        /// </summary>
        Binary,

        /// <summary>
        /// BPUN format with ASCII preamble and binary data sections
        /// </summary>
        BPUN,

        /// <summary>
        /// FLOMON floppy boot format (simplified BPUN variant)
        /// </summary>
        FloMon
    }

    /// <summary>
    /// Hard-disk controller family identified from the IOX/IOXT instructions in a
    /// raw-binary bootstrap (block 0). Only meaningful when <see cref="NdfsBootFormat"/>
    /// is <see cref="NdfsBootFormat.Binary"/>; BPUN/FLOMON boots are always floppy media
    /// and do not go through this classification.
    /// </summary>
    public enum NdfsBootControllerType
    {
        /// <summary>
        /// No recognizable IOX/IOXT device pattern found in the bootstrap.
        /// </summary>
        Unknown,

        /// <summary>
        /// SMD/ECC disk controller (ND632), literal IOX base 1540/1550/540/550 octal.
        /// </summary>
        SmdEcc,

        /// <summary>
        /// Winchester (ST506) controller (ND-3041/ND-3038), literal IOX base 500/510 octal.
        /// </summary>
        Winchester,

        /// <summary>
        /// SCSI disk controller (NCR-5386), identified by the fixed indirect IOXT opcode (device number in T).
        /// </summary>
        Scsi,

        /// <summary>
        /// Floppy controller, literal IOX base 1560/1570 octal.
        /// </summary>
        Floppy
    }

    /// <summary>
    /// How the hard-disk boot sector addresses the medium. Derived from the 9-word geometry block
    /// inside body A of a real page-0 relocator (DISC-BOOT-SECTOR-ANATOMY.md §5.4): a CHS disk
    /// (SMD, Winchester) carries non-zero sectors-per-track / sectors-per-cylinder, whereas SCSI
    /// leaves geometry slots +1..+7 ALL ZERO — a clean LBA-vs-CHS discriminator that is
    /// independent of the device number.
    /// </summary>
    public enum NdfsBootMediaAddressing
    {
        /// <summary>
        /// Geometry block not found / not a recognised hard-disk relocator.
        /// </summary>
        Unknown,

        /// <summary>
        /// Cylinder/head/sector geometry present (SMD, Winchester).
        /// </summary>
        Chs,

        /// <summary>
        /// Linear block addressing — geometry slots +1..+7 all zero (SCSI).
        /// </summary>
        Lba
    }

    /// <summary>
    /// The 9-word geometry block that a real ND hard-disk page-0 relocator embeds inside body A,
    /// anchored by the constant 0o1000 (= 512 words/sector). All fields are read directly from
    /// page 0; see DISC-BOOT-SECTOR-ANATOMY.md §5.4. Verified values:
    /// SMD 512/18/90/822/821, Winchester 512/9/72/1021/1011, SCSI 512/0/0/0/0 (zero geometry).
    /// The block is NOT at a fixed offset (SMD/SCSI anchor page word 0o255, Winchester 0o132), so
    /// it is located by scanning body A for the 0o1000 anchor, exactly as the anatomy doc prescribes.
    /// </summary>
    public readonly struct NdfsBootGeometry
    {
        /// <summary>
        /// True when a 0o1000-anchored geometry block was located in body A.
        /// </summary>
        public bool Found { get; }

        /// <summary>
        /// Page-word index at which the 0o1000 anchor was found (geometry slot +0).
        /// </summary>
        public int AnchorWord { get; }

        /// <summary>
        /// Slot +0: words per sector (512 on every specimen; 1024 bytes/sector).
        /// </summary>
        public int WordsPerSector { get; }

        /// <summary>
        /// Slot +1: sectors per track (18 SMD, 9 Winchester, 0 SCSI).
        /// </summary>
        public int SectorsPerTrack { get; }

        /// <summary>
        /// Slot +2: sectors per cylinder (90 SMD, 72 Winchester, 0 SCSI).
        /// </summary>
        public int SectorsPerCylinder { get; }

        /// <summary>
        /// Slot +3: cylinder count (822 SMD, 1021 Winchester, 0 SCSI). Role vs +4 is [INFERRED].
        /// </summary>
        public int Cylinders { get; }

        /// <summary>
        /// Slot +4: alternate cylinder count (821 SMD, 1011 Winchester, 0 SCSI). [INFERRED].
        /// </summary>
        public int CylindersAlt { get; }

        /// <summary>
        /// True when the CHS geometry slots (+1..+4) are all zero — the SCSI/LBA signature.
        /// </summary>
        public bool IsZeroGeometry => Found && SectorsPerTrack == 0 && SectorsPerCylinder == 0 && Cylinders == 0 && CylindersAlt == 0;

        /// <summary>
        /// Creates a geometry record.
        /// </summary>
        public NdfsBootGeometry(bool found, int anchorWord, int wordsPerSector, int sectorsPerTrack, int sectorsPerCylinder, int cylinders, int cylindersAlt)
        {
            Found = found;
            AnchorWord = anchorWord;
            WordsPerSector = wordsPerSector;
            SectorsPerTrack = sectorsPerTrack;
            SectorsPerCylinder = sectorsPerCylinder;
            Cylinders = cylinders;
            CylindersAlt = cylindersAlt;
        }
    }

    /// <summary>
    /// Represents loaded boot code from NDFS block 0
    /// </summary>
    public class NdfsBootCode
    {
        /// <summary>
        /// Boot format detected
        /// </summary>
        public NdfsBootFormat Format { get; set; }

        /// <summary>
        /// Hard-disk controller family (SMD/ECC, Winchester, SCSI, Floppy), detected from
        /// IOX/IOXT instructions when <see cref="Format"/> is <see cref="NdfsBootFormat.Binary"/>.
        /// Always <see cref="NdfsBootControllerType.Unknown"/> for BPUN/FLOMON/None.
        /// </summary>
        public NdfsBootControllerType ControllerType { get; set; }

        /// <summary>
        /// Start address (octal from BPUN preamble, or 0 for binary/FLOMON)
        /// </summary>
        public ushort StartAddress { get; set; }

        /// <summary>
        /// Bootstrap loader address (octal from BPUN preamble)
        /// </summary>
        public ushort BootAddress { get; set; }

        /// <summary>
        /// Load address for binary data (big-endian word from BPUN)
        /// </summary>
        public ushort LoadAddress { get; set; }

        /// <summary>
        /// Action field: 0 = execute at StartAddress, non-zero = remain in OPCOM
        /// </summary>
        public ushort Action { get; set; }

        /// <summary>
        /// Boot code data (ready to load into memory)
        /// </summary>
        public byte[] CodeData { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Checksum from file (for BPUN)
        /// </summary>
        public ushort Checksum { get; set; }

        /// <summary>
        /// Calculated checksum (for validation)
        /// </summary>
        public ushort CalculatedChecksum { get; set; }

        /// <summary>
        /// Is checksum valid?
        /// </summary>
        public bool IsChecksumValid => Checksum == CalculatedChecksum;

        /// <summary>
        /// Raw boot sector data (first 2048 bytes)
        /// </summary>
        public byte[] RawData { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Media addressing (CHS vs LBA) inferred from the geometry block; only meaningful for a
        /// raw-binary hard-disk bootstrap. See DISC-BOOT-SECTOR-ANATOMY.md §5.4.
        /// </summary>
        public NdfsBootMediaAddressing MediaAddressing { get; set; } = NdfsBootMediaAddressing.Unknown;

        /// <summary>
        /// The 9-word geometry block read from body A of the page-0 relocator, when found.
        /// </summary>
        public NdfsBootGeometry Geometry { get; set; }

        /// <summary>
        /// The device-class selector word read from body A (SMD = 1, Winchester = 2, SCSI = 3),
        /// or 0 when it could not be read. Cross-checks the IOX/IOXT controller classification.
        /// See DISC-BOOT-SECTOR-ANATOMY.md §5.3.
        /// </summary>
        public int DeviceClass { get; set; }
    }

    /// <summary>
    /// NDFS Boot Loader - Detects and loads boot code from NDFS block 0.
    /// Supports BPUN, FLOMON, and raw binary formats.
    /// </summary>
    /// <remarks>
    /// NDFS disks may contain bootable code in the first page (block 0, 2048 bytes).
    /// This can be in several formats:
    ///
    /// 1. BPUN Format (http://www.ndwiki.org/wiki/BPUN_File_Format):
    ///    - ASCII preamble with start/boot addresses (octal, CR-terminated)
    ///    - '!' delimiter
    ///    - Binary section: Address(2) + Count(2) + Data + Checksum(2) + Action(2)
    ///    - All multi-byte values are big-endian
    ///
    /// 2. FLOMON Format (Floppy Monitor - simplified BPUN):
    ///    - Same preamble as BPUN
    ///    - After checksum check, if Address=0, Count=0, Checksum=0:
    ///      Next byte is word count (single byte, NOT a word)
    ///      Data follows as: 00 HI 00 LO (4 bytes per word, padding with 00)
    ///
    ///    IMPORTANT - Word Count = 0:
    ///    When FLOMON word count = 0, it means:
    ///      1. The FLOMON format marker is present (Address=0, Count=0, Checksum=0)
    ///      2. The next byte (single byte) indicates how many 16-bit words follow
    ///      3. That byte value is 0 = Zero words in FLOMON format
    ///      4. This is VALID - it means no boot code in FLOMON format
    ///      5. Raw machine code may exist in different encoding after this marker
    ///
    /// 3. Raw Binary:
    ///    - Direct machine code starting at offset 0
    ///    - Load address is implicit (0x0000 or 0x0020)
    /// </remarks>
    public static class NdfsBootLoader
    {
        private const int NDFS_PAGE_SIZE = 2048;

        // The bootstrap occupies page-0 bytes 0..1999 (words 0..999). Bytes 2000..2047 (words
        // 1000..1023) are the NDFS VOLUME LABEL, not code — they contain ASCII such as "PACK-ONE"
        // (DISC-BOOT-SECTOR-ANATOMY.md §1.2). Any instruction scan MUST stop here: on scsi-1.img
        // the label word 1007 is 0167154, which matches the literal-IOX opcode mask and reads as a
        // phantom "IOX 3154" (§5.1). Capping at 1000 words removes that false positive at the source.
        private const int BOOT_REGION_WORDS = 1000;

        // Body A of the relocator (which carries the geometry block and the device-class word) lives
        // between the prologue end (page word 0o36 = 30) and roughly page word 0o300 (192). The
        // 0o1000 geometry anchor sits at page word 0o255 (SMD/SCSI) or 0o132 (Winchester); both fall
        // inside this window. See DISC-BOOT-SECTOR-ANATOMY.md §3, §5.4.
        private const int BODY_A_SCAN_START = 30;   // 0o36
        private const int BODY_A_SCAN_END = 200;    // just past 0o300, exclusive
        private const ushort GEOMETRY_ANCHOR = 0x0200; // 0o1000 = 512 words/sector

        // Device-class selector word offsets (DISC-BOOT-SECTOR-ANATOMY.md §5.3). SMD and SCSI share
        // the body-A layout and read the class word at page word 0o67; the (8-word-longer) Winchester
        // body A reads it at page word 0o77.
        private const int DEVICE_CLASS_WORD_SMD_SCSI = 0x37; // 0o67 = 55
        private const int DEVICE_CLASS_WORD_WINCHESTER = 0x3F; // 0o77 = 63
        private const byte BPUN_DELIMITER = 0x21; // '!' character
        private const byte CR = 0x0D;
        private const byte LF = 0x0A;

        /// <summary>
        /// Loads boot code from NDFS block device
        /// </summary>
        /// <param name="device">
        /// Block device containing NDFS filesystem
        /// </param>
        /// <returns>
        /// Boot code information, or null if no valid boot code found
        /// </returns>
        public static NdfsBootCode? LoadBootCode(IBlockDevice device)
        {
            if (device == null || !device.IsMounted)
                return null;

            // Read first page (block 0)
            byte[] pageBuffer = new byte[NDFS_PAGE_SIZE];
            int bytesRead = device.ReadBlock(BlockAddress.FromLBA(0), pageBuffer);

            if (bytesRead < NDFS_PAGE_SIZE)
                return null;

            return LoadBootCodeFromBytes(pageBuffer);
        }

        /// <summary>
        /// Loads boot code from raw bytes
        /// </summary>
        /// <param name="data">
        /// First 2048 bytes from NDFS disk
        /// </param>
        /// <returns>
        /// Boot code information, or null if no valid boot code found
        /// </returns>
        public static NdfsBootCode? LoadBootCodeFromBytes(byte[] data)
        {
            if (data == null || data.Length < NDFS_PAGE_SIZE)
                return null;

            // Try BPUN/FLOMON format first
            NdfsBootCode? boot = TryLoadBPUN(data);
            if (boot != null)
                return boot;

            // Check for raw binary hard-disk bootstrap (SMD/ECC, Winchester, SCSI - not BPUN/FLOMON)
            return TryLoadRawBinary(data);
        }

        /// <summary>
        /// Attempts to load BPUN or FLOMON format boot code
        /// </summary>
        private static NdfsBootCode? TryLoadBPUN(byte[] data)
        {
            try
            {
                using (MemoryStream ms = new MemoryStream(data))
                {
                    return LoadBPUNStream(ms, data);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Loads BPUN format from stream (based on working C code from ndwiki and C# from Emulated.Utilities)
        /// </summary>
        private static NdfsBootCode? LoadBPUNStream(Stream stream, byte[] originalData)
        {
            NdfsBootCode boot = new NdfsBootCode
            {
                RawData = originalData,
                Format = NdfsBootFormat.BPUN
            };

            // Find '!' delimiters and try each one. The BPUN/FLOMON preamble is a short ASCII
            // header at the very front of page 0, so the delimiter is only searched in the FIRST
            // 512 BYTES (boot-sector.md §6: "'!' (0x21) in first 512 bytes?"). Scanning the whole
            // page would let a stray 0x21 byte inside a raw machine-code bootstrap (e.g. SMD0.IMG,
            // whose boot code runs to byte 1999) get misparsed as a BPUN load record.
            const int BPUN_DELIMITER_SEARCH_LIMIT = 512;
            List<long> delimiterPositions = new List<long>();

            int searchLimit = (int)Math.Min(BPUN_DELIMITER_SEARCH_LIMIT, stream.Length);
            while (stream.Position < searchLimit)
            {
                int b = stream.ReadByte();
                if (b < 0)
                    break;

                if (b == BPUN_DELIMITER) // Found potential '!' delimiter
                {
                    delimiterPositions.Add(stream.Position - 1);
                }
            }

            // Try each delimiter position
            foreach (long delimPos in delimiterPositions)
            {
                stream.Position = delimPos + 1; // Position after '!'

                NdfsBootCode? result = TryParseBPUNBinarySection(stream, boot, originalData, delimPos);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        /// <summary>
        /// Try to parse BPUN binary section starting after a '!' delimiter
        /// </summary>
        private static NdfsBootCode? TryParseBPUNBinarySection(Stream stream, NdfsBootCode boot, byte[] originalData, long delimiterPos)
        {
            // Read binary section
            // Address (2 bytes, big-endian)
            int addr1 = stream.ReadByte();
            int addr2 = stream.ReadByte();
            if (addr1 < 0 || addr2 < 0)
                return null;

            ushort address = (ushort)((addr1 << 8) | addr2);

            // Count (2 bytes, big-endian)
            int cnt1 = stream.ReadByte();
            int cnt2 = stream.ReadByte();
            if (cnt1 < 0 || cnt2 < 0)
                return null;

            ushort count = (ushort)((cnt1 << 8) | cnt2);

            // Validation: Count should be reasonable for a 2048-byte page
            // If count * 2 + current position > page size, this is likely a false '!' in binary code
            int dataBytes = count * 2;
            long totalBytesNeeded = stream.Position + dataBytes + 4; // +4 for checksum and action
            if (totalBytesNeeded > NDFS_PAGE_SIZE)
            {
                // This '!' is part of machine code, not the real delimiter
                return null;
            }
            byte[] codeData = new byte[dataBytes];
            ushort checksum = 0;

            for (int i = 0; i < dataBytes; i += 2)
            {
                int hi = stream.ReadByte();
                int lo = stream.ReadByte();
                if (hi < 0 || lo < 0)
                    return null;

                codeData[i] = (byte)hi;
                codeData[i + 1] = (byte)lo;

                // Calculate checksum (sum of 16-bit words)
                ushort word = (ushort)((hi << 8) | lo);
                checksum = (ushort)(checksum + word);
            }

            // Read checksum (2 bytes, big-endian)
            int cs1 = stream.ReadByte();
            int cs2 = stream.ReadByte();
            if (cs1 < 0 || cs2 < 0)
                return null;

            ushort fileChecksum = (ushort)((cs1 << 8) | cs2);

            // Check for FLOMON format
            if (address == 0 && count == 0 && fileChecksum == 0)
            {
                return LoadFloMonFormat(stream, boot);
            }

            // Standard BPUN format
            boot.LoadAddress = address;
            boot.CodeData = codeData;
            boot.Checksum = fileChecksum;
            boot.CalculatedChecksum = checksum;

            // Read action field (2 bytes)
            int act1 = stream.ReadByte();
            int act2 = stream.ReadByte();
            if (act1 >= 0 && act2 >= 0)
            {
                boot.Action = (ushort)((act1 << 8) | act2);
            }

            return boot;
        }

        /// <summary>
        /// Loads FLOMON format (special BPUN variant for floppy boot)
        ///
        /// FLOMON Word Count = 0 is VALID and means:
        ///   - FLOMON format marker detected (Address=0, Count=0, Checksum=0)
        ///   - Next byte (single byte, NOT word) is word count
        ///   - Word count = 0 means NO boot code in FLOMON format
        ///   - Raw machine code may exist after in different encoding
        ///   - This is seen in real Nord floppy images (e.g., FLOPPY-360KB.img)
        /// </summary>
        private static NdfsBootCode LoadFloMonFormat(Stream stream, NdfsBootCode boot)
        {
            boot.Format = NdfsBootFormat.FloMon;

            // Next byte is word count
            int wordCount = stream.ReadByte();
            if (wordCount < 0)
                return boot;

            // FLOMON format: Each word is stored as 4 bytes: 00 HI 00 LO
            byte[] codeData = new byte[wordCount * 2];
            int codeIndex = 0;

            for (int i = 0; i < wordCount; i++)
            {
                // Read 00 HI 00 LO pattern
                int pad1 = stream.ReadByte();
                if (pad1 != 0)
                    break; // Invalid format

                int hi = stream.ReadByte();
                if (hi < 0)
                    break;

                int pad2 = stream.ReadByte();
                if (pad2 != 0)
                    break; // Invalid format

                int lo = stream.ReadByte();
                if (lo < 0)
                    break;

                // Store as big-endian word
                codeData[codeIndex++] = (byte)hi;
                codeData[codeIndex++] = (byte)lo;
            }

            // Trim to actual size
            if (codeIndex < codeData.Length)
            {
                byte[] trimmed = new byte[codeIndex];
                Array.Copy(codeData, 0, trimmed, 0, codeIndex);
                boot.CodeData = trimmed;
            }
            else
            {
                boot.CodeData = codeData;
            }

            boot.LoadAddress = 0; // FLOMON typically loads at address 0 or 2
            return boot;
        }

        /// <summary>
        /// Parses octal string to integer
        /// </summary>
        private static bool TryParseOctal(string octalStr, out int result)
        {
            result = 0;
            if (string.IsNullOrWhiteSpace(octalStr))
                return false;

            try
            {
                // Convert octal to decimal
                result = Convert.ToInt32(octalStr, 8);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ND-100 CPU opcodes (16-bit, big-endian on disk). A real bootstrap always begins by
        // disabling interrupts (and usually paging) before touching hardware - these are the
        // ONLY two words a genuine boot sector can start with. Verified against the assembler
        // source (norsk_data/nd100-as/instr.h: PIOF=0150405, IOF=0150401) and cross-checked
        // against nd100x/asm/DISK-IMAGE-INVENTORY.md, which uses the same two values to
        // classify real disk images.
        private const ushort OPCODE_PIOF = 0xD105; // octal 150405 - disable interrupts + paging
        private const ushort OPCODE_IOF = 0xD101;  // octal 150401 - disable interrupts only

        // Literal IOX instruction word = base opcode 0164000 (octal) OR'd with an 11-bit device
        // address in bits 10-0 (norsk_data/nd100-as/instr.h: OPC("iox",A_IOX,0164000), bounds-
        // checked there to 03777 octal = 11 bits). IOXT (device address taken from the T
        // register at runtime - used by SCSI/NCR-5386) is the single fixed word 0150415 octal;
        // no device address is visible in the instruction stream for it.
        private const ushort IOX_OPCODE_BASE = 0xE800; // octal 164000
        private const ushort IOX_OPCODE_MASK = 0xF800; // top 5 bits select the IOX instruction class
        private const ushort IOX_DEVICE_MASK = 0x07FF; // low 11 bits carry the literal device address
        private const ushort IOXT_OPCODE = 0xD10D;      // octal 150415 (SCSI/NCR-5386, indirect)

        // Hard-disk controller IOX device bases (octal, converted to decimal), each covering an
        // 8-word register window. Source: nd100-dis/DEVICE-BASES.md (thumbwheel-selectable base
        // addresses extracted from the RetroCore NDBus controller drivers).
        private static readonly ushort[] SmdEccBases = { 0x360, 0x368, 0x160, 0x168 };       // 1540, 1550, 540, 550 octal
        private static readonly ushort[] WinchesterBases = { 0x140, 0x148 };                  // 500, 510 octal
        private static readonly ushort[] FloppyBases = { 0x370, 0x378 };                      // 1560, 1570 octal

        /// <summary>
        /// Checks whether a 16-bit word is the genuine ND-100 bootstrap prologue (disable
        /// interrupts and/or paging before any hardware access).
        /// </summary>
        private static bool IsPrologueOpcode(ushort word)
        {
            return word == OPCODE_PIOF || word == OPCODE_IOF;
        }

        /// <summary>
        /// Attempts to load a raw hard-disk bootstrap (SMD/ECC, Winchester, SCSI) from block 0.
        /// Unlike BPUN/FLOMON, raw bootstraps carry no ASCII preamble or delimiter, so the only
        /// reliable signature is the CPU opcode itself: real boot code always starts with PIOF
        /// or IOF (see <see cref="IsPrologueOpcode"/>). Anything else - including data that
        /// merely "looks non-uniform" - is not bootable.
        /// </summary>
        private static NdfsBootCode? TryLoadRawBinary(byte[] data)
        {
            if (data == null || data.Length < 2)
                return null;

            ushort word0 = (ushort)((data[0] << 8) | data[1]);
            if (!IsPrologueOpcode(word0))
                return null;

            NdfsBootControllerType controller = DetectControllerType(data);

            // Read the verified media signals from the page-0 relocator's body A: the 9-word
            // geometry block (anchored by 0o1000) and the device-class selector word. These are
            // independent of the device number and let us confirm the medium even when the IOX
            // scan is ambiguous (DISC-BOOT-SECTOR-ANATOMY.md §5.3, §5.4).
            NdfsBootGeometry geometry = ReadGeometry(data);
            NdfsBootMediaAddressing addressing = NdfsBootMediaAddressing.Unknown;
            if (geometry.Found)
                addressing = geometry.IsZeroGeometry ? NdfsBootMediaAddressing.Lba : NdfsBootMediaAddressing.Chs;

            return new NdfsBootCode
            {
                Format = NdfsBootFormat.Binary,
                ControllerType = controller,
                StartAddress = 0,
                LoadAddress = 0,
                CodeData = data,
                RawData = data,
                Geometry = geometry,
                MediaAddressing = addressing,
                DeviceClass = ReadDeviceClass(data, controller)
            };
        }

        /// <summary>
        /// Reads a big-endian 16-bit word at the given page-word index, or 0 if out of range.
        /// </summary>
        private static ushort ReadWord(byte[] data, int wordIndex)
        {
            int byteIndex = wordIndex * 2;
            if (byteIndex < 0 || byteIndex + 1 >= data.Length)
                return 0;
            return (ushort)((data[byteIndex] << 8) | data[byteIndex + 1]);
        }

        /// <summary>
        /// Locates and reads the 9-word geometry block inside body A of a page-0 relocator. The
        /// block is NOT at a fixed offset, so it is found by scanning body A for the 0o1000 anchor
        /// (words per sector), exactly as DISC-BOOT-SECTOR-ANATOMY.md §5.4 prescribes. The first
        /// anchor in the body-A window is taken; on all three real specimens it is unique there.
        /// </summary>
        public static NdfsBootGeometry ReadGeometry(byte[] data)
        {
            if (data == null)
                return default;

            int lastAnchor = Math.Min(BODY_A_SCAN_END, data.Length / 2);
            for (int i = BODY_A_SCAN_START; i < lastAnchor; i++)
            {
                if (ReadWord(data, i) != GEOMETRY_ANCHOR)
                    continue;

                // Slots +0..+4 carry the words-per-sector anchor and the CHS geometry. Higher
                // slots (+5..+8) are not geometry on every medium (SCSI keeps +8 non-zero), so we
                // read only the load-bearing five.
                int wordsPerSector = ReadWord(data, i);          // +0 = 512 on every specimen
                int sectorsPerTrack = ReadWord(data, i + 1);     // +1
                int sectorsPerCylinder = ReadWord(data, i + 2);  // +2
                int cylinders = ReadWord(data, i + 3);           // +3
                int cylindersAlt = ReadWord(data, i + 4);        // +4
                return new NdfsBootGeometry(true, i, wordsPerSector, sectorsPerTrack, sectorsPerCylinder, cylinders, cylindersAlt);
            }

            return default; // Found == false
        }

        /// <summary>
        /// Reads the device-class selector word from body A (SMD = 1, Winchester = 2, SCSI = 3),
        /// choosing the offset that matches the detected controller's body-A layout. Returns 0 when
        /// the controller is unknown or the word is out of range. See DISC-BOOT-SECTOR-ANATOMY.md §5.3.
        /// </summary>
        private static int ReadDeviceClass(byte[] data, NdfsBootControllerType controller)
        {
            // The Winchester body A is 8 words longer than SMD/SCSI, so its class word sits at a
            // different page word. Keying the offset off the already-detected controller avoids the
            // circularity of reading a class word to detect the controller.
            switch (controller)
            {
                case NdfsBootControllerType.Winchester:
                    return ReadWord(data, DEVICE_CLASS_WORD_WINCHESTER);
                case NdfsBootControllerType.SmdEcc:
                case NdfsBootControllerType.Scsi:
                    return ReadWord(data, DEVICE_CLASS_WORD_SMD_SCSI);
                default:
                    return 0;
            }
        }

        /// <summary>
        /// Scans block 0 for a literal IOX (SMD/ECC, Winchester, Floppy) or indirect IOXT
        /// (SCSI/NCR-5386) instruction to classify which hard-disk controller the bootstrap
        /// targets. Only meaningful once <see cref="IsPrologueOpcode"/> has already confirmed
        /// the block is a genuine bootstrap.
        /// </summary>
        private static NdfsBootControllerType DetectControllerType(byte[] data)
        {
            // Never scan past the bootstrap region into the volume label (see BOOT_REGION_WORDS).
            int wordCount = Math.Min(data.Length / 2, BOOT_REGION_WORDS);

            // Scan the page in order and take the FIRST controller signature we meet.
            //
            // IMPORTANT — do NOT "improve" this by scanning the whole page for IOXT first and
            // treating it as an overriding SCSI signature. Page 0 holds boot CODE followed by
            // DATA, and this scan cannot tell them apart: any data word that happens to equal
            // 0150415 (0xD10D) looks exactly like an IOXT instruction.
            //
            // Proven on two real disks:
            //   * 1325.img — a Micropolis 1325, i.e. an ST-506/MFM drive on the WINCHESTER
            //     controller. It issues 23 literal IOX instructions in the Winchester window
            //     (octal 500-507) across words 79-570 — its real code — and then has 17 words
            //     equal to 0xD10D at words 714-929, i.e. AFTER the code ends. Those are data.
            //     An IOXT-first scan misclassifies this Winchester disk as SCSI.
            //   * scsi-1.img — a genuine SCSI disk. It has NO literal IOX in any controller
            //     window at all, and its IOXT instructions begin at word 290, inside the code.
            //
            // First-match-in-order therefore classifies BOTH correctly: the Winchester disk hits
            // its real IOX at word 79 before reaching the data, and the SCSI disk has no window
            // IOX to hit, so it falls through to its genuine early IOXT.
            for (int i = 0; i < wordCount; i++)
            {
                ushort word = (ushort)((data[i * 2] << 8) | data[i * 2 + 1]);

                if (word == IOXT_OPCODE)
                    return NdfsBootControllerType.Scsi;

                if ((word & IOX_OPCODE_MASK) == IOX_OPCODE_BASE)
                {
                    ushort deviceAddress = (ushort)(word & IOX_DEVICE_MASK);

                    if (IsInDeviceWindow(deviceAddress, SmdEccBases))
                        return NdfsBootControllerType.SmdEcc;
                    if (IsInDeviceWindow(deviceAddress, WinchesterBases))
                        return NdfsBootControllerType.Winchester;
                    if (IsInDeviceWindow(deviceAddress, FloppyBases))
                        return NdfsBootControllerType.Floppy;
                }
            }

            return NdfsBootControllerType.Unknown;
        }

        /// <summary>
        /// Checks whether a device address falls within any of the given controllers' 8-word
        /// register windows (base .. base+7).
        /// </summary>
        private static bool IsInDeviceWindow(ushort address, ushort[] bases)
        {
            for (int i = 0; i < bases.Length; i++)
            {
                if (address >= bases[i] && address <= bases[i] + 7)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Detects boot format without loading full code
        /// </summary>
        /// <param name="device">
        /// Block device to check
        /// </param>
        /// <returns>
        /// Boot format detected
        /// </returns>
        public static NdfsBootFormat DetectBootFormat(IBlockDevice device)
        {
            NdfsBootCode? boot = LoadBootCode(device);
            return boot?.Format ?? NdfsBootFormat.None;
        }

        /// <summary>
        /// Checks if NDFS disk is bootable
        /// </summary>
        /// <param name="device">
        /// Block device to check
        /// </param>
        /// <returns>
        /// True if valid boot code exists
        /// </returns>
        public static bool IsBootable(IBlockDevice device)
        {
            return DetectBootFormat(device) != NdfsBootFormat.None;
        }

        /// <summary>
        /// Detects the hard-disk controller family (SMD/ECC, Winchester, SCSI) targeted by a
        /// raw-binary bootstrap. Returns <see cref="NdfsBootControllerType.Unknown"/> for
        /// BPUN/FLOMON boots (floppy media, identified separately) or non-bootable disks.
        /// </summary>
        /// <param name="device">
        /// Block device to check
        /// </param>
        /// <returns>
        /// Controller type detected from the bootstrap's IOX/IOXT instructions
        /// </returns>
        public static NdfsBootControllerType DetectControllerType(IBlockDevice device)
        {
            NdfsBootCode? boot = LoadBootCode(device);
            return boot?.ControllerType ?? NdfsBootControllerType.Unknown;
        }
    }
}
