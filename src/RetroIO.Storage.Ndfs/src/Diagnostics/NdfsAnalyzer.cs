//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text;
using RetroIO.Storage.Core;
using RetroIO.Storage.Ndfs.BlockDevices;
using RetroIO.Storage.Ndfs.Elements;
using RetroIO.Storage.Ndfs.FileSystems;
using RetroIO.Storage.Ndfs.Structures;

namespace RetroIO.Storage.Ndfs.Diagnostics
{
    /// <summary>
    /// Deep analysis and validation tool for NDFS filesystems.
    /// Analyzes block usage, pointer chains, quota tracking, and detects inconsistencies.
    /// </summary>
    public class NdfsAnalyzer
    {
        private readonly NdfsFileSystem _fileSystem;
        private readonly IBlockDevice _device;
        private readonly uint _totalBlocks;
        private readonly Dictionary<uint, BlockInfo> _blockMap;
        private readonly List<AnalysisIssue> _issues;

        /// <summary>
        /// Creates an analyzer for the given file system and block device.
        /// </summary>
        /// <param name="fileSystem">
        /// The file system to analyze.
        /// </param>
        /// <param name="device">
        /// The block device that holds the file system.
        /// </param>
        /// <exception cref="System.ArgumentNullException">
        /// Thrown when <paramref name="fileSystem"/> or <paramref name="device"/> is null.
        /// </exception>
        public NdfsAnalyzer(NdfsFileSystem fileSystem, IBlockDevice device)
        {
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _totalBlocks = device.TotalBlocks;
            _blockMap = new Dictionary<uint, BlockInfo>();
            _issues = new List<AnalysisIssue>();
        }

        /// <summary>
        /// Performs comprehensive filesystem analysis.
        /// </summary>
        public AnalysisReport Analyze()
        {
            var report = new AnalysisReport
            {
                AnalysisStartTime = DateTime.Now,
                TotalBlocks = _totalBlocks
            };

            // Phase 1: Initialize block map
            InitializeBlockMap();

            // Phase 2: Parse filesystem structures
            var masterBlock = ParseMasterBlock();
            var bitFile = ParseBitFile(masterBlock);
            var userFile = ParseUserFile(masterBlock);
            var objectFile = ParseObjectFile(masterBlock);

            // Phase 3: Map bitmap usage
            MapBitmapUsage(bitFile);

            // Phase 4: Traverse all pointers from master block
            TraverseFromMasterBlock(masterBlock);
            TraverseUserFile(userFile, masterBlock);
            TraverseObjectFile(objectFile, masterBlock);

            // Phase 5: Traverse all file pointers
            TraverseAllFiles(objectFile);

            // Phase 6: Detect issues
            DetectOrphanedBlocks();
            DetectMultiplyReferencedBlocks();
            DetectBitmapMismatches();
            DetectInvalidPointers();

            // Phase 7: Validate user quotas
            ValidateUserQuotas(userFile, objectFile);

            // Phase 8: Validate unreserved pages count
            ValidateUnreservedPages(masterBlock, bitFile);

            // Phase 9: Generate report
            report.AnalysisEndTime = DateTime.Now;
            report.BlockMap = _blockMap;
            report.Issues = _issues;
            report.MasterBlock = masterBlock;
            report.TotalIssues = _issues.Count;

            return report;
        }

        private void InitializeBlockMap()
        {
            for (uint i = 0; i < _totalBlocks; i++)
            {
                _blockMap[i] = new BlockInfo
                {
                    BlockId = i,
                    IsMarkedUsedInBitmap = false,
                    PointedToBy = new List<PointerInfo>(),
                    BlockType = DetermineSystemBlockType(i)
                };
            }
        }

        private BlockType DetermineSystemBlockType(uint blockId)
        {
            // System blocks 0-6 are reserved
            if (blockId == 0 || blockId == 1)
                return BlockType.MasterBlock;
            if (blockId >= 2 && blockId <= 6)
                return BlockType.SystemReserved;
            return BlockType.Unknown;
        }

        private MasterBlock ParseMasterBlock()
        {
            // Read master block from page 0
            byte[] buffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            _device.ReadBlock(BlockAddress.FromLBA(0), buffer);

            int offset = NdfsBlockDeviceDescriptor.MASTER_BLOCK_OFFSET;

            // Parse directory name
            int nameEnd = offset;
            while (nameEnd < offset + 16 && buffer[nameEnd] != 0x27 && buffer[nameEnd] != 0)
                nameEnd++;

            string dirName = Encoding.ASCII.GetString(buffer, offset, nameEnd - offset);

            // Parse total pages (big-endian)
            uint totalPages = (uint)((buffer[offset + 0x0C] << 24) |
                                    (buffer[offset + 0x0D] << 16) |
                                    (buffer[offset + 0x0E] << 8) |
                                    buffer[offset + 0x0F]);

            // Parse pointers
            var objectFilePtr = BlockPointer.FromBytes(buffer, offset + 0x10);
            var userFilePtr = BlockPointer.FromBytes(buffer, offset + 0x14);
            var bitFilePtr = BlockPointer.FromBytes(buffer, offset + 0x18);

            // Parse unreserved pages
            uint unreservedPages = (uint)((buffer[offset + 0x1C] << 24) |
                                         (buffer[offset + 0x1D] << 16) |
                                         (buffer[offset + 0x1E] << 8) |
                                         buffer[offset + 0x1F]);

            return new MasterBlock
            {
                DirectoryName = dirName,
                TotalPages = totalPages,
                ObjectFilePointer = objectFilePtr,
                UserFilePointer = userFilePtr,
                BitFilePointer = bitFilePtr,
                UnreservedPages = unreservedPages
            };
        }

        private BitFile ParseBitFile(MasterBlock master)
        {
            byte[] buffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            _device.ReadBlock(BlockAddress.FromLBA(master.BitFilePointer.BlockID), buffer);

            var bitFile = new BitFile();
            // BitFile is typically just the raw bitmap
            return bitFile;
        }

        private UserFile ParseUserFile(MasterBlock master)
        {
            var userFile = new UserFile();
            // UserFile parsing would go here
            // For now, use filesystem's existing UserFile
            return userFile;
        }

        private ObjectFile ParseObjectFile(MasterBlock master)
        {
            var objectFile = new ObjectFile();
            // ObjectFile parsing would go here
            // For now, use filesystem's existing ObjectFile
            return objectFile;
        }

        private void MapBitmapUsage(BitFile bitFile)
        {
            // Read bitmap and mark blocks as used/free
            byte[] buffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            _device.ReadBlock(BlockAddress.FromLBA(4), buffer); // BitFile is at block 4

            for (uint blockId = 0; blockId < _totalBlocks; blockId++)
            {
                uint byteIndex = blockId / 8;
                int bitIndex = (int)(blockId % 8);

                if (byteIndex < buffer.Length)
                {
                    bool isUsed = (buffer[byteIndex] & (1 << bitIndex)) != 0;
                    _blockMap[blockId].IsMarkedUsedInBitmap = isUsed;
                }
            }
        }

        private void TraverseFromMasterBlock(MasterBlock master)
        {
            // Mark system blocks (0-6) as used
            for (uint i = 0; i <= 6; i++)
            {
                _blockMap[i].BlockType = BlockType.SystemReserved;
                RecordPointer(i, new BlockPointer { BlockID = 0, Type = BlockPointer.PointerType.Contiguous }, "MasterBlock");
            }

            // Mark ObjectFile index block
            if (master.ObjectFilePointer.BlockID > 0 && master.ObjectFilePointer.BlockID < _totalBlocks)
            {
                _blockMap[master.ObjectFilePointer.BlockID].BlockType = BlockType.ObjectFileIndex;
                RecordPointer(master.ObjectFilePointer.BlockID, master.ObjectFilePointer, "MasterBlock->ObjectFile");
            }

            // Mark UserFile index block
            if (master.UserFilePointer.BlockID > 0 && master.UserFilePointer.BlockID < _totalBlocks)
            {
                _blockMap[master.UserFilePointer.BlockID].BlockType = BlockType.UserFileIndex;
                RecordPointer(master.UserFilePointer.BlockID, master.UserFilePointer, "MasterBlock->UserFile");
            }

            // Mark BitFile block
            if (master.BitFilePointer.BlockID > 0 && master.BitFilePointer.BlockID < _totalBlocks)
            {
                _blockMap[master.BitFilePointer.BlockID].BlockType = BlockType.BitFile;
                RecordPointer(master.BitFilePointer.BlockID, master.BitFilePointer, "MasterBlock->BitFile");
            }
        }

        private void TraverseUserFile(UserFile userFile, MasterBlock master)
        {
            // Read UserFile index block
            byte[] indexBuffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            _device.ReadBlock(BlockAddress.FromLBA(master.UserFilePointer.BlockID), indexBuffer);

            // Traverse data blocks
            for (int i = 0; i < 512; i++)
            {
                var dataPtr = BlockPointer.FromBytes(indexBuffer, i * 4);
                if (dataPtr.BlockID > 0 && dataPtr.BlockID < _totalBlocks)
                {
                    _blockMap[dataPtr.BlockID].BlockType = BlockType.UserFileData;
                    RecordPointer(dataPtr.BlockID, dataPtr, $"UserFile[Index]->DataBlock[{i}]");
                }
            }
        }

        private void TraverseObjectFile(ObjectFile objectFile, MasterBlock master)
        {
            // Read ObjectFile index block
            byte[] indexBuffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            _device.ReadBlock(BlockAddress.FromLBA(master.ObjectFilePointer.BlockID), indexBuffer);

            // Traverse data blocks
            for (int i = 0; i < 512; i++)
            {
                var dataPtr = BlockPointer.FromBytes(indexBuffer, i * 4);
                if (dataPtr.BlockID > 0 && dataPtr.BlockID < _totalBlocks)
                {
                    _blockMap[dataPtr.BlockID].BlockType = BlockType.ObjectFileData;
                    RecordPointer(dataPtr.BlockID, dataPtr, $"ObjectFile[Index]->DataBlock[{i}]");
                }
            }
        }

        private void TraverseAllFiles(ObjectFile objectFile)
        {
            // Get all files from filesystem
            var users = _fileSystem.GetUsers();

            foreach (var user in users)
            {
                var files = _fileSystem.ListDirectory(user.UserName);

                if (files != null && files.Length > 0)
                {
                    foreach (var file in files)
                    {
                        string fullPath = $"{user.UserName}/{file.Name}";
                        TraverseFile(fullPath, user.UserName);
                    }
                }
            }

            // Also try direct ObjectFile traversal using reflection
            TraverseObjectFileDirectly(objectFile);
        }

        private void TraverseObjectFileDirectly(ObjectFile objectFile)
        {
            try
            {
                // Use reflection to access internal ObjectFile entries
                var objFileField = typeof(NdfsFileSystem).GetField("_objectFile",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (objFileField == null) return;

                var objectFileInstance = objFileField.GetValue(_fileSystem);
                if (objectFileInstance == null) return;

                // Try to get all entries
                var getAllMethod = objectFileInstance.GetType().GetMethod("GetAllEntries");
                if (getAllMethod != null)
                {
                    object? entriesResult = getAllMethod.Invoke(objectFileInstance, null);
                    if (entriesResult != null)
                    {
                        dynamic entries = entriesResult;
                        foreach (var entry in entries)
                        {
                            string fileName = entry.FileName;
                            string userName = entry.UserName;
                            BlockPointer filePointer = entry.FilePointer;
                            uint fileSize = entry.BytesInFile;

                            string fullPath = $"{userName}/{fileName}";

                            if (filePointer.BlockID > 0 && filePointer.BlockID < _totalBlocks)
                            {
                                _blockMap[filePointer.BlockID].BlockType = BlockType.FileIndex;
                                RecordPointer(filePointer.BlockID, filePointer, $"File[{fullPath}]->IndexBlock");
                                TraverseFileDataBlocks(filePointer, fileSize, fullPath);
                            }
                        }
                    }
                }
            }
            catch
            {
                // Reflection failed, skip direct traversal
            }
        }

        private void TraverseFile(string fullPath, string userName)
        {
            try
            {
                var metadata = _fileSystem.GetMetadata(fullPath);
                if (metadata == null) return;

                // Use reflection to get ObjectEntry (internal)
                var objFileField = typeof(NdfsFileSystem).GetField("_objectFile",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (objFileField == null) return;

                var objectFile = objFileField.GetValue(_fileSystem);
                if (objectFile == null) return;

                var findMethod = objectFile.GetType().GetMethod("FindObject");
                if (findMethod == null) return;

                string fileName = System.IO.Path.GetFileName(fullPath);
                object? entryResult = findMethod.Invoke(objectFile, new object[] { fileName, userName });
                if (entryResult == null) return;
                dynamic entry = entryResult;

                BlockPointer filePointer = entry.FilePointer;
                uint fileSize = entry.BytesInFile;

                // Mark file index block
                if (filePointer.BlockID > 0 && filePointer.BlockID < _totalBlocks)
                {
                    _blockMap[filePointer.BlockID].BlockType = BlockType.FileIndex;
                    RecordPointer(filePointer.BlockID, filePointer, $"File[{fullPath}]->IndexBlock");

                    // Traverse data blocks
                    TraverseFileDataBlocks(filePointer, fileSize, fullPath);
                }
            }
            catch
            {
                // Skip files that can't be analyzed
            }
        }

        private void TraverseFileDataBlocks(BlockPointer indexPointer, uint fileSize, string filePath)
        {
            if (indexPointer.Type == BlockPointer.PointerType.Indexed)
            {
                // Read index block
                byte[] indexBuffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
                _device.ReadBlock(BlockAddress.FromLBA(indexPointer.BlockID), indexBuffer);

                // Calculate number of data blocks needed
                uint dataBlocks = (fileSize + NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE - 1) / NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;

                for (int i = 0; i < dataBlocks && i < 512; i++)
                {
                    var dataPtr = BlockPointer.FromBytes(indexBuffer, i * 4);
                    if (dataPtr.BlockID > 0 && dataPtr.BlockID < _totalBlocks)
                    {
                        _blockMap[dataPtr.BlockID].BlockType = BlockType.FileData;
                        RecordPointer(dataPtr.BlockID, dataPtr, $"File[{filePath}]->DataBlock[{i}]");
                    }
                }
            }
            else if (indexPointer.Type == BlockPointer.PointerType.Contiguous)
            {
                // Contiguous allocation
                uint dataBlocks = (fileSize + NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE - 1) / NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;

                for (uint i = 0; i < dataBlocks; i++)
                {
                    uint blockId = indexPointer.BlockID + i;
                    if (blockId < _totalBlocks)
                    {
                        _blockMap[blockId].BlockType = BlockType.FileData;
                        RecordPointer(blockId, indexPointer, $"File[{filePath}]->DataBlock[{i}](Contiguous)");
                    }
                }
            }
        }

        private void RecordPointer(uint blockId, BlockPointer pointer, string source)
        {
            if (!_blockMap.ContainsKey(blockId))
                return;

            var info = new PointerInfo
            {
                Pointer = pointer,
                Source = source
            };

            _blockMap[blockId].PointedToBy.Add(info);
        }

        private void DetectOrphanedBlocks()
        {
            foreach (var block in _blockMap.Values)
            {
                // Orphaned: marked as used in bitmap but nothing points to it
                if (block.IsMarkedUsedInBitmap && block.PointedToBy.Count == 0 && block.BlockId >= 7)
                {
                    _issues.Add(new AnalysisIssue
                    {
                        Severity = IssueSeverity.Warning,
                        Category = IssueCategory.OrphanedBlock,
                        BlockId = block.BlockId,
                        Description = $"Block {block.BlockId} is marked as used in bitmap but has no pointers to it (orphaned block)",
                        Recommendation = "Free this block in the bitmap"
                    });
                }
            }
        }

        private void DetectMultiplyReferencedBlocks()
        {
            foreach (var block in _blockMap.Values)
            {
                if (block.PointedToBy.Count > 1)
                {
                    // Build sources string manually
                    var sb = new StringBuilder();
                    for (int i = 0; i < block.PointedToBy.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(block.PointedToBy[i].Source);
                    }
                    string sources = sb.ToString();

                    _issues.Add(new AnalysisIssue
                    {
                        Severity = IssueSeverity.Critical,
                        Category = IssueCategory.MultiplePointers,
                        BlockId = block.BlockId,
                        Description = $"Block {block.BlockId} is pointed to by {block.PointedToBy.Count} different sources: {sources}",
                        Recommendation = "This indicates filesystem corruption - same block referenced multiple times"
                    });
                }
            }
        }

        private void DetectBitmapMismatches()
        {
            foreach (var block in _blockMap.Values)
            {
                bool shouldBeUsed = block.PointedToBy.Count > 0;

                if (shouldBeUsed && !block.IsMarkedUsedInBitmap)
                {
                    // Build list of who points to this block
                    var sb = new StringBuilder();
                    for (int i = 0; i < block.PointedToBy.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(block.PointedToBy[i].Source);
                    }
                    string pointers = sb.ToString();

                    _issues.Add(new AnalysisIssue
                    {
                        Severity = IssueSeverity.Error,
                        Category = IssueCategory.BitmapMismatch,
                        BlockId = block.BlockId,
                        Description = $"Block {block.BlockId} has {block.PointedToBy.Count} pointer(s) but is marked FREE in bitmap. Pointed to by: {pointers}",
                        Recommendation = "Mark block as USED in bitmap"
                    });
                }
            }
        }

        private void DetectInvalidPointers()
        {
            foreach (var block in _blockMap.Values)
            {
                foreach (var ptr in block.PointedToBy)
                {
                    // Check if pointer points to valid block
                    if (ptr.Pointer.BlockID >= _totalBlocks)
                    {
                        _issues.Add(new AnalysisIssue
                        {
                            Severity = IssueSeverity.Critical,
                            Category = IssueCategory.InvalidPointer,
                            BlockId = block.BlockId,
                            Description = $"Invalid pointer from {ptr.Source}: points to block {ptr.Pointer.BlockID} which is beyond disk size ({_totalBlocks})",
                            Recommendation = "Remove this invalid pointer"
                        });
                    }
                }
            }
        }

        private void ValidateUserQuotas(UserFile userFile, ObjectFile objectFile)
        {
            var users = _fileSystem.GetUsers();

            // Access ObjectFile via reflection to get actual entries
            var objFileField = typeof(NdfsFileSystem).GetField("_objectFile",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (objFileField == null) return;

            var objectFileInstance = objFileField.GetValue(_fileSystem);
            if (objectFileInstance == null) return;

            foreach (var user in users)
            {
                uint actualPagesUsed = 0;

                // Get user's files from ObjectFile using GetUserObjects method
                var getUserObjectsMethod = objectFileInstance.GetType().GetMethod("GetUserObjects", new[] { typeof(string) });
                if (getUserObjectsMethod != null)
                {
                    object? userObjectsResult = getUserObjectsMethod.Invoke(objectFileInstance, new object[] { user.UserName });

                    if (userObjectsResult != null)
                    {
                        dynamic userObjects = userObjectsResult;
                        // Calculate pages from ObjectEntry.PagesInFile
                        for (int i = 0; i < userObjects.Count; i++)
                        {
                            dynamic obj = userObjects[i];
                            uint pagesInFile = obj.PagesInFile;
                            actualPagesUsed += pagesInFile;
                        }
                    }
                }

                // Report mismatch if values don't match
                if (actualPagesUsed != user.PagesUsed)
                {
                    _issues.Add(new AnalysisIssue
                    {
                        Severity = IssueSeverity.Warning,
                        Category = IssueCategory.QuotaMismatch,
                        Description = $"User '{user.UserName}' has PagesUsed={user.PagesUsed} but actual blocks used={actualPagesUsed}",
                        Recommendation = $"Update user PagesUsed to {actualPagesUsed}"
                    });
                }
            }
        }

        private void ValidateUnreservedPages(MasterBlock master, BitFile bitFile)
        {
            // Count actual free blocks
            uint actualFree = 0;
            foreach (var block in _blockMap.Values)
            {
                if (!block.IsMarkedUsedInBitmap && block.BlockId >= 7)
                    actualFree++;
            }

            if (actualFree != master.UnreservedPages)
            {
                _issues.Add(new AnalysisIssue
                {
                    Severity = IssueSeverity.Warning,
                    Category = IssueCategory.UnreservedPagesMismatch,
                    Description = $"MasterBlock.UnreservedPages={master.UnreservedPages} but actual free blocks={actualFree}",
                    Recommendation = $"Update MasterBlock.UnreservedPages to {actualFree}"
                });
            }
        }
    }

    /// <summary>
    /// Information about a single block on disk.
    /// </summary>
    public class BlockInfo
    {
        /// <summary>
        /// Gets or sets the block number.
        /// </summary>
        public uint BlockId { get; set; }
        /// <summary>
        /// Gets or sets whether the bit file marks the block as used.
        /// </summary>
        public bool IsMarkedUsedInBitmap { get; set; }
        /// <summary>
        /// Gets or sets the pointers found that refer to this block.
        /// </summary>
        public List<PointerInfo> PointedToBy { get; set; } = new List<PointerInfo>();
        /// <summary>
        /// Gets or sets the kind of data the block holds.
        /// </summary>
        public BlockType BlockType { get; set; }
    }

    /// <summary>
    /// A pointer to a block, with a text description of where it was found.
    /// </summary>
    public class PointerInfo
    {
        /// <summary>
        /// Gets or sets the pointer.
        /// </summary>
        public BlockPointer Pointer { get; set; } = new BlockPointer();
        /// <summary>
        /// Gets or sets the text that says where the pointer was found, for example <c>MasterBlock->ObjectFile</c>.
        /// </summary>
        public string Source { get; set; } = string.Empty;
    }

    /// <summary>
    /// The kind of data a block holds.
    /// </summary>
    public enum BlockType
    {
        /// <summary>
        /// The block type is not known.
        /// </summary>
        Unknown,
        /// <summary>
        /// The master block.
        /// </summary>
        MasterBlock,
        /// <summary>
        /// A block in the reserved system area.
        /// </summary>
        SystemReserved,
        /// <summary>
        /// The index block of the object file.
        /// </summary>
        ObjectFileIndex,
        /// <summary>
        /// A data block of the object file.
        /// </summary>
        ObjectFileData,
        /// <summary>
        /// The index block of the user file.
        /// </summary>
        UserFileIndex,
        /// <summary>
        /// A data block of the user file.
        /// </summary>
        UserFileData,
        /// <summary>
        /// The bit file block.
        /// </summary>
        BitFile,
        /// <summary>
        /// The index block of a file.
        /// </summary>
        FileIndex,
        /// <summary>
        /// A data block of a file.
        /// </summary>
        FileData
    }

    /// <summary>
    /// The master block fields that the analyzer reads from the disk.
    /// </summary>
    public class MasterBlock
    {
        /// <summary>
        /// Gets or sets the directory name.
        /// </summary>
        public string DirectoryName { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the total number of pages stored in the master block.
        /// </summary>
        public uint TotalPages { get; set; }
        /// <summary>
        /// Gets or sets the pointer to the object file.
        /// </summary>
        public BlockPointer ObjectFilePointer { get; set; } = new BlockPointer();
        /// <summary>
        /// Gets or sets the pointer to the user file.
        /// </summary>
        public BlockPointer UserFilePointer { get; set; } = new BlockPointer();
        /// <summary>
        /// Gets or sets the pointer to the bit file.
        /// </summary>
        public BlockPointer BitFilePointer { get; set; } = new BlockPointer();
        /// <summary>
        /// Gets or sets the unreserved page count stored in the master block.
        /// </summary>
        public uint UnreservedPages { get; set; }
    }

    /// <summary>
    /// The result of an analysis run.
    /// </summary>
    public class AnalysisReport
    {
        /// <summary>
        /// Gets or sets when the analysis started.
        /// </summary>
        public DateTime AnalysisStartTime { get; set; }
        /// <summary>
        /// Gets or sets when the analysis ended.
        /// </summary>
        public DateTime AnalysisEndTime { get; set; }
        /// <summary>
        /// Gets or sets the number of blocks on the device.
        /// </summary>
        public uint TotalBlocks { get; set; }
        /// <summary>
        /// Gets or sets the information collected per block, keyed by block number.
        /// </summary>
        public Dictionary<uint, BlockInfo> BlockMap { get; set; } = new Dictionary<uint, BlockInfo>();
        /// <summary>
        /// Gets or sets the issues found.
        /// </summary>
        public List<AnalysisIssue> Issues { get; set; } = new List<AnalysisIssue>();
        /// <summary>
        /// Gets or sets the master block fields that were read.
        /// </summary>
        public MasterBlock MasterBlock { get; set; } = new MasterBlock();
        /// <summary>
        /// Gets or sets the number of issues found.
        /// </summary>
        public int TotalIssues { get; set; }

        /// <summary>
        /// Gets the time between the start and the end of the analysis.
        /// </summary>
        public TimeSpan AnalysisDuration => AnalysisEndTime - AnalysisStartTime;

        /// <summary>
        /// Counts the issues with the given severity.
        /// </summary>
        /// <param name="severity">
        /// The severity to count.
        /// </param>
        /// <returns>
        /// The number of issues with that severity.
        /// </returns>
        public int GetIssueCount(IssueSeverity severity)
        {
            int count = 0;
            for (int i = 0; i < Issues.Count; i++)
            {
                if (Issues[i].Severity == severity)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Counts the issues in the given category.
        /// </summary>
        /// <param name="category">
        /// The category to count.
        /// </param>
        /// <returns>
        /// The number of issues in that category.
        /// </returns>
        public int GetIssueCount(IssueCategory category)
        {
            int count = 0;
            for (int i = 0; i < Issues.Count; i++)
            {
                if (Issues[i].Category == category)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Builds a plain text report of the master block, block usage and issues.
        /// </summary>
        /// <returns>
        /// The report text.
        /// </returns>
        public string GenerateTextReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=".PadRight(80, '='));
            sb.AppendLine("NDFS FILESYSTEM ANALYSIS REPORT");
            sb.AppendLine("=".PadRight(80, '='));
            sb.AppendLine();

            sb.AppendLine($"Analysis Time: {AnalysisStartTime:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Duration: {AnalysisDuration.TotalSeconds:F2} seconds");
            sb.AppendLine($"Total Blocks: {TotalBlocks:N0}");
            sb.AppendLine();

            sb.AppendLine("MASTER BLOCK INFORMATION:");
            sb.AppendLine($"  Directory Name: {MasterBlock.DirectoryName}");
            sb.AppendLine($"  Total Pages: {MasterBlock.TotalPages:N0}");
            sb.AppendLine($"  Unreserved Pages: {MasterBlock.UnreservedPages:N0}");
            sb.AppendLine($"  ObjectFile Pointer: Block {MasterBlock.ObjectFilePointer.BlockID}");
            sb.AppendLine($"  UserFile Pointer: Block {MasterBlock.UserFilePointer.BlockID}");
            sb.AppendLine($"  BitFile Pointer: Block {MasterBlock.BitFilePointer.BlockID}");
            sb.AppendLine();

            sb.AppendLine("BLOCK USAGE SUMMARY:");
            int usedInBitmap = 0;
            int hasPointers = 0;
            foreach (var block in BlockMap.Values)
            {
                if (block.IsMarkedUsedInBitmap) usedInBitmap++;
                if (block.PointedToBy.Count > 0) hasPointers++;
            }

            int orphaned = 0;
            int multiRef = 0;
            for (int i = 0; i < Issues.Count; i++)
            {
                if (Issues[i].Category == IssueCategory.OrphanedBlock) orphaned++;
                if (Issues[i].Category == IssueCategory.MultiplePointers) multiRef++;
            }

            sb.AppendLine($"  Marked USED in bitmap: {usedInBitmap:N0}");
            sb.AppendLine($"  Marked FREE in bitmap: {TotalBlocks - usedInBitmap:N0}");
            sb.AppendLine($"  Blocks with pointers: {hasPointers:N0}");
            sb.AppendLine($"  Orphaned blocks: {orphaned:N0}");
            sb.AppendLine($"  Multiply-referenced blocks: {multiRef:N0}");
            sb.AppendLine();

            sb.AppendLine("ISSUES FOUND:");
            sb.AppendLine($"  Total: {TotalIssues}");
            sb.AppendLine($"  Critical: {GetIssueCount(IssueSeverity.Critical)}");
            sb.AppendLine($"  Error: {GetIssueCount(IssueSeverity.Error)}");
            sb.AppendLine($"  Warning: {GetIssueCount(IssueSeverity.Warning)}");
            sb.AppendLine($"  Info: {GetIssueCount(IssueSeverity.Info)}");
            sb.AppendLine();

            if (TotalIssues > 0)
            {
                sb.AppendLine("ISSUE DETAILS:");
                sb.AppendLine();

                // Group issues by category manually
                var categoryGroups = new Dictionary<IssueCategory, List<AnalysisIssue>>();
                for (int i = 0; i < Issues.Count; i++)
                {
                    var issue = Issues[i];
                    if (!categoryGroups.ContainsKey(issue.Category))
                        categoryGroups[issue.Category] = new List<AnalysisIssue>();
                    categoryGroups[issue.Category].Add(issue);
                }

                // Sort categories
                var categories = new List<IssueCategory>(categoryGroups.Keys);
                categories.Sort();

                for (int i = 0; i < categories.Count; i++)
                {
                    var category = categories[i];
                    var groupIssues = categoryGroups[category];

                    sb.AppendLine($"  [{category}] ({groupIssues.Count} issues)");
                    sb.AppendLine("  " + "-".PadRight(76, '-'));

                    // Sort issues by severity
                    groupIssues.Sort((a, b) => a.Severity.CompareTo(b.Severity));

                    for (int j = 0; j < groupIssues.Count; j++)
                    {
                        var issue = groupIssues[j];
                        sb.AppendLine($"    [{issue.Severity}] {issue.Description}");
                        if (!string.IsNullOrEmpty(issue.Recommendation))
                            sb.AppendLine($"      → {issue.Recommendation}");
                    }

                    sb.AppendLine();
                }
            }
            else
            {
                sb.AppendLine("✓ NO ISSUES FOUND - Filesystem is healthy!");
                sb.AppendLine();
            }

            sb.AppendLine("=".PadRight(80, '='));
            sb.AppendLine("END OF REPORT");
            sb.AppendLine("=".PadRight(80, '='));

            return sb.ToString();
        }
    }

    /// <summary>
    /// One problem found by the analyzer.
    /// </summary>
    public class AnalysisIssue
    {
        /// <summary>
        /// Gets or sets how serious the issue is.
        /// </summary>
        public IssueSeverity Severity { get; set; }
        /// <summary>
        /// Gets or sets the kind of issue.
        /// </summary>
        public IssueCategory Category { get; set; }
        /// <summary>
        /// Gets or sets the block the issue is about, or null when it is not about one block.
        /// </summary>
        public uint? BlockId { get; set; }
        /// <summary>
        /// Gets or sets the text that describes the issue.
        /// </summary>
        public string Description { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the text that says how to fix the issue.
        /// </summary>
        public string Recommendation { get; set; } = string.Empty;
    }

    /// <summary>
    /// How serious an issue is.
    /// </summary>
    public enum IssueSeverity
    {
        /// <summary>
        /// Information only.
        /// </summary>
        Info,
        /// <summary>
        /// A warning.
        /// </summary>
        Warning,
        /// <summary>
        /// An error.
        /// </summary>
        Error,
        /// <summary>
        /// A critical error.
        /// </summary>
        Critical
    }

    /// <summary>
    /// The kind of an issue.
    /// </summary>
    public enum IssueCategory
    {
        /// <summary>
        /// A block is marked used in the bitmap but nothing points to it.
        /// </summary>
        OrphanedBlock,
        /// <summary>
        /// More than one pointer refers to the same block.
        /// </summary>
        MultiplePointers,
        /// <summary>
        /// A block has pointers to it but the bitmap marks it free.
        /// </summary>
        BitmapMismatch,
        /// <summary>
        /// A pointer refers to a block beyond the size of the disk.
        /// </summary>
        InvalidPointer,
        /// <summary>
        /// The pages used recorded for a user differ from the pages found in use.
        /// </summary>
        QuotaMismatch,
        /// <summary>
        /// The unreserved page count in the master block differs from the number of free blocks found.
        /// </summary>
        UnreservedPagesMismatch
    }
}
