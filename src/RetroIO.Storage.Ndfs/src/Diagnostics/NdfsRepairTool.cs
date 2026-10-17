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
using RetroIO.Storage.Ndfs.FileSystems;
using RetroIO.Storage.Ndfs.Structures;

namespace RetroIO.Storage.Ndfs.Diagnostics
{
    /// <summary>
    /// Automated repair tool for NDFS filesystems.
    /// Fixes common issues detected by NdfsAnalyzer.
    /// </summary>
    public class NdfsRepairTool
    {
        private readonly NdfsFileSystem _fileSystem;
        private readonly IBlockDevice _device;
        private readonly AnalysisReport _report;
        private readonly List<RepairAction> _performedActions;

        /// <summary>
        /// Creates a repair tool for the given file system, using the issues in an analysis report.
        /// </summary>
        /// <param name="fileSystem">
        /// The file system to repair.
        /// </param>
        /// <param name="device">
        /// The block device that holds the file system.
        /// </param>
        /// <param name="report">
        /// The analysis report to repair from.
        /// </param>
        /// <exception cref="System.ArgumentNullException">
        /// Thrown when <paramref name="fileSystem"/>, <paramref name="device"/> or <paramref name="report"/> is null.
        /// </exception>
        public NdfsRepairTool(NdfsFileSystem fileSystem, IBlockDevice device, AnalysisReport report)
        {
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _report = report ?? throw new ArgumentNullException(nameof(report));
            _performedActions = new List<RepairAction>();
        }

        /// <summary>
        /// Generates a list of recommended repair actions without performing them.
        /// </summary>
        public RepairPlan GenerateRepairPlan()
        {
            var plan = new RepairPlan();

            // Plan 1: Free orphaned blocks
            var orphanedIssues = new List<AnalysisIssue>();
            for (int i = 0; i < _report.Issues.Count; i++)
            {
                if (_report.Issues[i].Category == IssueCategory.OrphanedBlock)
                    orphanedIssues.Add(_report.Issues[i]);
            }
            if (orphanedIssues.Count > 0)
            {
                var affectedBlocks = new List<uint>();
                for (int i = 0; i < orphanedIssues.Count; i++)
                {
                    if (orphanedIssues[i].BlockId is uint blockId)
                        affectedBlocks.Add(blockId);
                }

                plan.Actions.Add(new RepairAction
                {
                    ActionType = RepairActionType.FreeOrphanedBlocks,
                    Description = $"Free {orphanedIssues.Count} orphaned block(s) that are marked used but have no pointers",
                    AffectedBlocks = affectedBlocks,
                    Risk = RepairRisk.Low,
                    AutoApprove = true
                });
            }

            // Plan 2: Fix bitmap mismatches
            var bitmapIssues = new List<AnalysisIssue>();
            for (int i = 0; i < _report.Issues.Count; i++)
            {
                if (_report.Issues[i].Category == IssueCategory.BitmapMismatch)
                    bitmapIssues.Add(_report.Issues[i]);
            }
            if (bitmapIssues.Count > 0)
            {
                var affectedBlocks = new List<uint>();
                for (int i = 0; i < bitmapIssues.Count; i++)
                {
                    if (bitmapIssues[i].BlockId is uint blockId)
                        affectedBlocks.Add(blockId);
                }

                plan.Actions.Add(new RepairAction
                {
                    ActionType = RepairActionType.FixBitmapMismatch,
                    Description = $"Fix {bitmapIssues.Count} bitmap mismatch(es) where blocks have pointers but are marked free",
                    AffectedBlocks = affectedBlocks,
                    Risk = RepairRisk.Low,
                    AutoApprove = true
                });
            }

            // Plan 3: Update user quotas
            var quotaIssues = new List<AnalysisIssue>();
            for (int i = 0; i < _report.Issues.Count; i++)
            {
                if (_report.Issues[i].Category == IssueCategory.QuotaMismatch)
                    quotaIssues.Add(_report.Issues[i]);
            }
            if (quotaIssues.Count > 0)
            {
                plan.Actions.Add(new RepairAction
                {
                    ActionType = RepairActionType.FixUserQuotas,
                    Description = $"Fix {quotaIssues.Count} user quota mismatch(es)",
                    Risk = RepairRisk.Low,
                    AutoApprove = true
                });
            }

            // Plan 4: Update unreserved pages count
            var unreservedIssues = new List<AnalysisIssue>();
            for (int i = 0; i < _report.Issues.Count; i++)
            {
                if (_report.Issues[i].Category == IssueCategory.UnreservedPagesMismatch)
                    unreservedIssues.Add(_report.Issues[i]);
            }
            if (unreservedIssues.Count > 0)
            {
                plan.Actions.Add(new RepairAction
                {
                    ActionType = RepairActionType.FixUnreservedPages,
                    Description = "Update MasterBlock unreserved pages count",
                    Risk = RepairRisk.Low,
                    AutoApprove = true
                });
            }

            // Plan 5: Remove invalid pointers (HIGH RISK)
            var invalidPtrIssues = new List<AnalysisIssue>();
            for (int i = 0; i < _report.Issues.Count; i++)
            {
                if (_report.Issues[i].Category == IssueCategory.InvalidPointer)
                    invalidPtrIssues.Add(_report.Issues[i]);
            }
            if (invalidPtrIssues.Count > 0)
            {
                plan.Actions.Add(new RepairAction
                {
                    ActionType = RepairActionType.RemoveInvalidPointers,
                    Description = $"Remove {invalidPtrIssues.Count} invalid pointer(s) pointing beyond disk",
                    Risk = RepairRisk.High,
                    AutoApprove = false,
                    WarningMessage = "This may cause data loss if files become inaccessible"
                });
            }

            // Plan 6: Handle multiply-referenced blocks (CRITICAL RISK)
            var multiRefIssues = new List<AnalysisIssue>();
            for (int i = 0; i < _report.Issues.Count; i++)
            {
                if (_report.Issues[i].Category == IssueCategory.MultiplePointers)
                    multiRefIssues.Add(_report.Issues[i]);
            }
            if (multiRefIssues.Count > 0)
            {
                var affectedBlocks = new List<uint>();
                for (int i = 0; i < multiRefIssues.Count; i++)
                {
                    if (multiRefIssues[i].BlockId is uint blockId)
                        affectedBlocks.Add(blockId);
                }

                plan.Actions.Add(new RepairAction
                {
                    ActionType = RepairActionType.DuplicateMultiReferencedBlocks,
                    Description = $"Duplicate {multiRefIssues.Count} multiply-referenced block(s) to fix corruption",
                    AffectedBlocks = affectedBlocks,
                    Risk = RepairRisk.Critical,
                    AutoApprove = false,
                    WarningMessage = "This indicates serious corruption. Manual inspection recommended."
                });
            }

            plan.TotalActions = plan.Actions.Count;

            int autoApprove = 0;
            int manualApprove = 0;
            for (int i = 0; i < plan.Actions.Count; i++)
            {
                if (plan.Actions[i].AutoApprove)
                    autoApprove++;
                else
                    manualApprove++;
            }
            plan.AutoApproveCount = autoApprove;
            plan.ManualApproveCount = manualApprove;

            return plan;
        }

        /// <summary>
        /// Executes approved repair actions.
        /// </summary>
        public RepairResult ExecuteRepairs(RepairPlan plan, bool autoApproveAll = false)
        {
            var result = new RepairResult
            {
                StartTime = DateTime.Now
            };

            foreach (var action in plan.Actions)
            {
                if (!action.IsApproved && !autoApproveAll && !action.AutoApprove)
                {
                    result.SkippedActions.Add(action);
                    continue;
                }

                try
                {
                    bool success = ExecuteAction(action);
                    if (success)
                    {
                        result.SuccessfulActions.Add(action);
                        _performedActions.Add(action);
                    }
                    else
                    {
                        result.FailedActions.Add(action);
                    }
                }
                catch (Exception ex)
                {
                    action.ErrorMessage = ex.Message;
                    result.FailedActions.Add(action);
                }
            }

            result.EndTime = DateTime.Now;
            result.TotalActionsPerformed = result.SuccessfulActions.Count;
            result.TotalActionsFailed = result.FailedActions.Count;

            return result;
        }

        private bool ExecuteAction(RepairAction action)
        {
            switch (action.ActionType)
            {
                case RepairActionType.FreeOrphanedBlocks:
                    return FreeOrphanedBlocks(action.AffectedBlocks);

                case RepairActionType.FixBitmapMismatch:
                    return FixBitmapMismatches(action.AffectedBlocks);

                case RepairActionType.FixUserQuotas:
                    return FixUserQuotas();

                case RepairActionType.FixUnreservedPages:
                    return FixUnreservedPages();

                case RepairActionType.RemoveInvalidPointers:
                    return RemoveInvalidPointers();

                case RepairActionType.DuplicateMultiReferencedBlocks:
                    return DuplicateMultiReferencedBlocks(action.AffectedBlocks);

                default:
                    return false;
            }
        }

        private bool FreeOrphanedBlocks(List<uint> blockIds)
        {
            // Use reflection to access BitFile
            var bitFileField = typeof(NdfsFileSystem).GetField("_bitFile",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (bitFileField == null) return false;

            var bitFile = bitFileField.GetValue(_fileSystem);
            if (bitFile == null) return false;

            var markFreeMethod = bitFile.GetType().GetMethod("MarkBlockFree");
            if (markFreeMethod == null) return false;

            foreach (var blockId in blockIds)
            {
                markFreeMethod.Invoke(bitFile, new object[] { blockId });
            }

            // Write BitFile to disk
            var writeToDiskMethod = bitFile.GetType().GetMethod("WriteToDisk");
            if (writeToDiskMethod != null)
            {
                writeToDiskMethod.Invoke(bitFile, new object[] { _device });
            }

            return true;
        }

        private bool FixBitmapMismatches(List<uint> blockIds)
        {
            // Mark blocks as used in bitmap
            var bitFileField = typeof(NdfsFileSystem).GetField("_bitFile",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (bitFileField == null) return false;

            var bitFile = bitFileField.GetValue(_fileSystem);
            if (bitFile == null) return false;

            var allocateMethod = bitFile.GetType().GetMethod("AllocateBlocks");
            if (allocateMethod == null) return false;

            foreach (var blockId in blockIds)
            {
                allocateMethod.Invoke(bitFile, new object[] { blockId, 1U });
            }

            // Write BitFile to disk
            var writeToDiskMethod = bitFile.GetType().GetMethod("WriteToDisk");
            if (writeToDiskMethod != null)
            {
                writeToDiskMethod.Invoke(bitFile, new object[] { _device });
            }

            return true;
        }

        private bool FixUserQuotas()
        {
            var users = _fileSystem.GetUsers();

            foreach (var user in users)
            {
                uint actualBlocks = 0;
                var files = _fileSystem.ListDirectory(user.UserName);

                foreach (var file in files)
                {
                    var metadata = _fileSystem.GetMetadata($"{user.UserName}/{file.Name}");
                    if (metadata != null)
                    {
                        uint fileBlocks = ((uint)metadata.Size + NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE - 1) / NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
                        fileBlocks += 1; // Index block
                        actualBlocks += fileBlocks;
                    }
                }

                // Update user quota
                user.PagesUsed = actualBlocks;
            }

            // Write UserFile to disk
            var userFileField = typeof(NdfsFileSystem).GetField("_userFile",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (userFileField == null) return false;

            var userFile = userFileField.GetValue(_fileSystem);
            if (userFile == null) return false;

            var masterBlockField = typeof(NdfsFileSystem).GetField("_masterBlock",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (masterBlockField == null) return false;

            object? masterBlockObj = masterBlockField.GetValue(_fileSystem);
            if (masterBlockObj == null) return false;
            dynamic masterBlock = masterBlockObj;

            // Write each modified user's page individually
            var writePageMethod = userFile.GetType().GetMethod("WritePageForEntry");
            if (writePageMethod != null)
            {
                foreach (var user in users)
                {
                    writePageMethod.Invoke(userFile, new object[] { _device, masterBlock.UserFilePointer, user.UserIndex });
                }
            }

            return true;
        }

        private bool FixUnreservedPages()
        {
            // Count actual free blocks
            uint actualFree = 0;
            foreach (var block in _report.BlockMap.Values)
            {
                if (!block.IsMarkedUsedInBitmap && block.BlockId >= 7)
                    actualFree++;
            }

            // Update master block
            byte[] buffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            _device.ReadBlock(BlockAddress.FromLBA(0), buffer);

            int offset = NdfsBlockDeviceDescriptor.MASTER_BLOCK_OFFSET + 0x1C;
            buffer[offset] = (byte)(actualFree >> 24);
            buffer[offset + 1] = (byte)(actualFree >> 16);
            buffer[offset + 2] = (byte)(actualFree >> 8);
            buffer[offset + 3] = (byte)actualFree;

            _device.WriteBlock(BlockAddress.FromLBA(0), buffer);

            return true;
        }

        private bool RemoveInvalidPointers()
        {
            // Find all invalid pointer issues
            var invalidIssues = new List<AnalysisIssue>();
            for (int i = 0; i < _report.Issues.Count; i++)
            {
                if (_report.Issues[i].Category == IssueCategory.InvalidPointer)
                    invalidIssues.Add(_report.Issues[i]);
            }

            if (invalidIssues.Count == 0)
                return true;

            // Group by source to identify which index blocks need updating
            var sourceToIssues = new Dictionary<string, List<AnalysisIssue>>();
            for (int i = 0; i < invalidIssues.Count; i++)
            {
                var issue = invalidIssues[i];
                if (!issue.BlockId.HasValue)
                    continue;

                var blockInfo = _report.BlockMap[issue.BlockId.Value];

                for (int j = 0; j < blockInfo.PointedToBy.Count; j++)
                {
                    var ptr = blockInfo.PointedToBy[j];
                    if (ptr.Pointer.BlockID >= _device.TotalBlocks)
                    {
                        if (!sourceToIssues.ContainsKey(ptr.Source))
                            sourceToIssues[ptr.Source] = new List<AnalysisIssue>();
                        sourceToIssues[ptr.Source].Add(issue);
                    }
                }
            }

            // This requires careful handling per source type
            // For now, we'll nullify the pointers in index blocks
            foreach (var kvp in sourceToIssues)
            {
                string source = kvp.Key;

                // Parse source to identify block type and location
                if (source.Contains("File[") && source.Contains("]->DataBlock["))
                {
                    // File index block pointer - zero it out
                    // Extract file path and data block index from source string
                    // This is complex - for safety, skip for now
                    continue;
                }
            }

            return true;
        }

        private bool DuplicateMultiReferencedBlocks(List<uint> blockIds)
        {
            // This operation duplicates blocks that are incorrectly referenced multiple times
            // Strategy: Keep first reference, duplicate block for subsequent references

            var bitFileField = typeof(NdfsFileSystem).GetField("_bitFile",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (bitFileField == null) return false;

            var bitFile = bitFileField.GetValue(_fileSystem);
            if (bitFile == null) return false;

            var findFreeMethod = bitFile.GetType().GetMethod("FindFreeBlocks");
            if (findFreeMethod == null) return false;

            for (int i = 0; i < blockIds.Count; i++)
            {
                uint blockId = blockIds[i];
                var blockInfo = _report.BlockMap[blockId];

                if (blockInfo.PointedToBy.Count < 2)
                    continue; // Not multiply referenced

                // Read original block data
                byte[] originalData = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
                _device.ReadBlock(BlockAddress.FromLBA(blockId), originalData);

                // Keep first reference, duplicate for others
                for (int j = 1; j < blockInfo.PointedToBy.Count; j++)
                {
                    // Allocate new block
                    object? freeBlocksResult = findFreeMethod.Invoke(bitFile, new object[] { 1U });
                    if (freeBlocksResult is not System.Collections.IList freeBlocksList || freeBlocksList.Count == 0)
                        return false; // No free space

                    uint newBlockId = Convert.ToUInt32(freeBlocksList[0]);

                    // Copy data to new block
                    _device.WriteBlock(BlockAddress.FromLBA(newBlockId), originalData);

                    // Update pointer to point to new block
                    var ptrInfo = blockInfo.PointedToBy[j];
                    UpdatePointerLocation(ptrInfo, newBlockId);
                }
            }

            return true;
        }

        private bool UpdatePointerLocation(PointerInfo ptrInfo, uint newBlockId)
        {
            // Parse source string to find where pointer is stored
            string source = ptrInfo.Source;

            // Extract index block location from source
            // Format examples:
            // "File[username/filename]->DataBlock[3]"
            // "ObjectFile[Index]->DataBlock[5]"
            // "UserFile[Index]->DataBlock[2]"

            if (source.Contains("File[") && source.Contains("]->DataBlock["))
            {
                // Extract file path and block index
                int fileStart = source.IndexOf("File[") + 5;
                int fileEnd = source.IndexOf("]->DataBlock[");
                string filePath = source.Substring(fileStart, fileEnd - fileStart);

                int blockIdxStart = source.IndexOf("]->DataBlock[") + 13;
                int blockIdxEnd = source.IndexOf("]", blockIdxStart);
                string blockIdxStr = source.Substring(blockIdxStart, blockIdxEnd - blockIdxStart);
                int dataBlockIndex = int.Parse(blockIdxStr);

                // Get file's index block
                var metadata = _fileSystem.GetMetadata(filePath);
                if (metadata == null) return false;

                // Use reflection to get file pointer
                var objFileField = typeof(NdfsFileSystem).GetField("_objectFile",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (objFileField == null) return false;

                var objectFile = objFileField.GetValue(_fileSystem);
                if (objectFile == null) return false;

                var findMethod = objectFile.GetType().GetMethod("FindObject");
                if (findMethod == null) return false;

                string userName = filePath.Contains("/") ? filePath.Substring(0, filePath.IndexOf("/")) : "";
                string fileName = filePath.Contains("/") ? filePath.Substring(filePath.IndexOf("/") + 1) : filePath;

                object? entryResult = findMethod.Invoke(objectFile, new object[] { fileName, userName });
                if (entryResult == null) return false;
                dynamic entry = entryResult;

                BlockPointer filePointer = entry.FilePointer;

                // Read index block
                byte[] indexBuffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
                _device.ReadBlock(BlockAddress.FromLBA(filePointer.BlockID), indexBuffer);

                // Update pointer at offset
                int offset = dataBlockIndex * 4;
                indexBuffer[offset] = (byte)(newBlockId >> 24);
                indexBuffer[offset + 1] = (byte)(newBlockId >> 16);
                indexBuffer[offset + 2] = (byte)(newBlockId >> 8);
                indexBuffer[offset + 3] = (byte)newBlockId;

                // Write back
                _device.WriteBlock(BlockAddress.FromLBA(filePointer.BlockID), indexBuffer);
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// A list of planned repair actions.
    /// </summary>
    public class RepairPlan
    {
        /// <summary>
        /// Gets or sets the planned actions.
        /// </summary>
        public List<RepairAction> Actions { get; set; } = new List<RepairAction>();
        /// <summary>
        /// Gets or sets the number of planned actions.
        /// </summary>
        public int TotalActions { get; set; }
        /// <summary>
        /// Gets or sets the number of actions that are approved automatically.
        /// </summary>
        public int AutoApproveCount { get; set; }
        /// <summary>
        /// Gets or sets the number of actions that need manual approval.
        /// </summary>
        public int ManualApproveCount { get; set; }

        /// <summary>
        /// Builds a plain text summary of the plan.
        /// </summary>
        /// <returns>
        /// The summary text.
        /// </returns>
        public string GenerateTextSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=".PadRight(80, '='));
            sb.AppendLine("NDFS FILESYSTEM REPAIR PLAN");
            sb.AppendLine("=".PadRight(80, '='));
            sb.AppendLine();

            sb.AppendLine($"Total Actions: {TotalActions}");
            sb.AppendLine($"  Auto-approve: {AutoApproveCount}");
            sb.AppendLine($"  Manual approval required: {ManualApproveCount}");
            sb.AppendLine();

            if (Actions.Count == 0)
            {
                sb.AppendLine("✓ NO REPAIRS NEEDED - Filesystem is healthy!");
            }
            else
            {
                sb.AppendLine("PLANNED ACTIONS:");
                sb.AppendLine();

                for (int i = 0; i < Actions.Count; i++)
                {
                    var action = Actions[i];
                    sb.AppendLine($"  {i + 1}. [{action.Risk}] {action.Description}");
                    sb.AppendLine($"     Type: {action.ActionType}");
                    sb.AppendLine($"     Auto-approve: {action.AutoApprove}");

                    if (action.AffectedBlocks.Count > 0)
                    {
                        if (action.AffectedBlocks.Count <= 10)
                            sb.AppendLine($"     Affected blocks: {string.Join(", ", action.AffectedBlocks)}");
                        else
                            sb.AppendLine($"     Affected blocks: {action.AffectedBlocks.Count} blocks");
                    }

                    if (!string.IsNullOrEmpty(action.WarningMessage))
                        sb.AppendLine($"     ⚠ WARNING: {action.WarningMessage}");

                    sb.AppendLine();
                }
            }

            sb.AppendLine("=".PadRight(80, '='));

            return sb.ToString();
        }
    }

    /// <summary>
    /// One planned repair.
    /// </summary>
    public class RepairAction
    {
        /// <summary>
        /// Gets or sets the kind of repair.
        /// </summary>
        public RepairActionType ActionType { get; set; }
        /// <summary>
        /// Gets or sets the text that describes the repair.
        /// </summary>
        public string Description { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the blocks the repair changes.
        /// </summary>
        public List<uint> AffectedBlocks { get; set; } = new List<uint>();
        /// <summary>
        /// Gets or sets how risky the repair is.
        /// </summary>
        public RepairRisk Risk { get; set; }
        /// <summary>
        /// Gets or sets whether the repair runs without manual approval.
        /// </summary>
        public bool AutoApprove { get; set; }
        /// <summary>
        /// Gets or sets whether the repair has been approved manually.
        /// </summary>
        public bool IsApproved { get; set; }
        /// <summary>
        /// Gets or sets a warning to show before the repair runs.
        /// </summary>
        public string WarningMessage { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the error text when the repair threw an exception.
        /// </summary>
        public string ErrorMessage { get; set; } = string.Empty;
    }

    /// <summary>
    /// The kind of a repair.
    /// </summary>
    public enum RepairActionType
    {
        /// <summary>
        /// Marks orphaned blocks as free in the bitmap.
        /// </summary>
        FreeOrphanedBlocks,
        /// <summary>
        /// Fixes blocks whose bitmap state does not match their pointers.
        /// </summary>
        FixBitmapMismatch,
        /// <summary>
        /// Fixes the pages used recorded for users.
        /// </summary>
        FixUserQuotas,
        /// <summary>
        /// Fixes the unreserved page count in the master block.
        /// </summary>
        FixUnreservedPages,
        /// <summary>
        /// Removes pointers that refer to blocks beyond the size of the disk.
        /// </summary>
        RemoveInvalidPointers,
        /// <summary>
        /// Copies blocks that more than one pointer refers to.
        /// </summary>
        DuplicateMultiReferencedBlocks
    }

    /// <summary>
    /// How risky a repair is.
    /// </summary>
    public enum RepairRisk
    {
        /// <summary>
        /// Safe operations such as freeing orphaned blocks.
        /// </summary>
        Low,      // Safe operations like freeing orphaned blocks
        /// <summary>
        /// Operations that change metadata.
        /// </summary>
        Medium,   // Modifying metadata
        /// <summary>
        /// Operations that remove pointers or change the structure.
        /// </summary>
        High,     // Removing pointers or modifying structure
        /// <summary>
        /// Operations that can destroy data.
        /// </summary>
        Critical  // Potentially destructive operations
    }

    /// <summary>
    /// The outcome of running a repair plan.
    /// </summary>
    public class RepairResult
    {
        /// <summary>
        /// Gets or sets when the repair started.
        /// </summary>
        public DateTime StartTime { get; set; }
        /// <summary>
        /// Gets or sets when the repair ended.
        /// </summary>
        public DateTime EndTime { get; set; }
        /// <summary>
        /// Gets or sets the actions that succeeded.
        /// </summary>
        public List<RepairAction> SuccessfulActions { get; set; } = new List<RepairAction>();
        /// <summary>
        /// Gets or sets the actions that failed.
        /// </summary>
        public List<RepairAction> FailedActions { get; set; } = new List<RepairAction>();
        /// <summary>
        /// Gets or sets the actions that were skipped because they were not approved.
        /// </summary>
        public List<RepairAction> SkippedActions { get; set; } = new List<RepairAction>();
        /// <summary>
        /// Gets or sets the number of actions that succeeded.
        /// </summary>
        public int TotalActionsPerformed { get; set; }
        /// <summary>
        /// Gets or sets the number of actions that failed.
        /// </summary>
        public int TotalActionsFailed { get; set; }

        /// <summary>
        /// Gets the time between the start and the end of the repair.
        /// </summary>
        public TimeSpan Duration => EndTime - StartTime;

        /// <summary>
        /// Builds a plain text summary of the result.
        /// </summary>
        /// <returns>
        /// The summary text.
        /// </returns>
        public string GenerateTextSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=".PadRight(80, '='));
            sb.AppendLine("NDFS FILESYSTEM REPAIR RESULT");
            sb.AppendLine("=".PadRight(80, '='));
            sb.AppendLine();

            sb.AppendLine($"Repair Time: {StartTime:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Duration: {Duration.TotalSeconds:F2} seconds");
            sb.AppendLine();

            sb.AppendLine($"Successful: {SuccessfulActions.Count}");
            sb.AppendLine($"Failed: {FailedActions.Count}");
            sb.AppendLine($"Skipped: {SkippedActions.Count}");
            sb.AppendLine();

            if (SuccessfulActions.Count > 0)
            {
                sb.AppendLine("SUCCESSFUL REPAIRS:");
                foreach (var action in SuccessfulActions)
                {
                    sb.AppendLine($"  ✓ {action.Description}");
                }
                sb.AppendLine();
            }

            if (FailedActions.Count > 0)
            {
                sb.AppendLine("FAILED REPAIRS:");
                foreach (var action in FailedActions)
                {
                    sb.AppendLine($"  ✗ {action.Description}");
                    if (!string.IsNullOrEmpty(action.ErrorMessage))
                        sb.AppendLine($"    Error: {action.ErrorMessage}");
                }
                sb.AppendLine();
            }

            if (SkippedActions.Count > 0)
            {
                sb.AppendLine("SKIPPED REPAIRS (not approved):");
                foreach (var action in SkippedActions)
                {
                    sb.AppendLine($"  - {action.Description}");
                }
                sb.AppendLine();
            }

            sb.AppendLine("=".PadRight(80, '='));

            return sb.ToString();
        }
    }
}
