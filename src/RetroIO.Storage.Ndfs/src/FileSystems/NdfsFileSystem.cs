//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using RetroIO.Storage.Core;  // IBlockFileSystem moved to Core to fix circular dependency
using RetroIO.Storage.Ndfs.BlockDevices;
using RetroIO.Storage.Ndfs.Boot;
using RetroIO.Storage.Ndfs.Elements;
using RetroIO.Storage.Ndfs.Structures;

namespace RetroIO.Storage.Ndfs.FileSystems
{
    /// <summary>
    /// NDFS filesystem implementation following existing RetroFS patterns.
    /// Operates on IBlockDevice like CbmDosFileSystem does.
    /// </summary>
    public partial class NdfsFileSystem : IBlockFileSystem, INdfsFileSystem, IDisposable
    {
        private IBlockDevice? _device;
        private MasterBlock? _masterBlock;
        private ObjectFile _objectFile = new ObjectFile();
        private UserFile _userFile = new UserFile();
        private BitFile _bitFile = new BitFile();
        private bool _initialized = false;

        // Random access file handles
        private Dictionary<int, NdfsFileHandle> _openHandles = new Dictionary<int, NdfsFileHandle>();

        public string FileSystemType => "NDFS";
        public string VolumeLabel => _masterBlock?.DirectoryName ?? "NDFS";
        public string ExtensionSeparator => ":";

        public void Initialize(IBlockDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));

            if (!_device.IsMounted)
                throw new InvalidOperationException("Block device must be mounted before initializing filesystem");

            // Read master block from first page
            var pageBuffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            _device.ReadBlock(BlockAddress.FromLBA(0), pageBuffer);

            // Parse master block
            _masterBlock = MasterBlock.FromBytes(pageBuffer);

            if (!_masterBlock.IsValid())
                throw new InvalidDataException("Invalid NDFS master block");

            // Load filesystem structures
            LoadFileSystemStructures();
            _initialized = true;
        }

        /// <summary>
        /// Rewrites the "Unreserved Pages" field (offset 0x1C of the 32-byte master block, at
        /// <see cref="NdfsBlockDeviceDescriptor.MASTER_BLOCK_OFFSET"/> within page 0) with the
        /// true free-page count from the bit file. Real SINTRAN reads this cached field to report
        /// free disk space without rescanning the bitmap, so every operation that allocates or
        /// frees pages must call this afterward or the cached count goes stale relative to the
        /// real bit-file state.
        ///
        /// This must be called AFTER the bit file itself has been persisted (i.e. after
        /// <c>_bitFile.WriteToDisk</c>), and only after mutating operations — never after
        /// read-only ones (ListDirectory, ReadFile, GetMetadata, etc.).
        ///
        /// Only the 32-byte master-block region of page 0 is touched (see
        /// <see cref="MasterBlock.WriteToBytes"/>), so this never disturbs the separate Extended
        /// Info Block (offset 0x07D0) or its checksum.
        /// </summary>
        private void PersistMasterBlock()
        {
            if (_device == null || _masterBlock == null)
                return;

            // Refresh the cached count from the authoritative bitmap before writing it out.
            _masterBlock.UnreservedPages = _bitFile.GetFreePages();

            // Read page 0 fresh so we only patch the master-block bytes within it and leave
            // everything else (including the Extended Info Block) exactly as it is on disk.
            var pageBuffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            _device.ReadBlock(BlockAddress.FromLBA(0), pageBuffer);

            _masterBlock.WriteToBytes(pageBuffer);

            _device.WriteBlock(BlockAddress.FromLBA(0), pageBuffer);
        }

        public bool CanHandle(IBlockDevice device)
        {
            if (device == null || !device.IsMounted)
                return false;

            try
            {
                // Read first page
                var pageBuffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
                device.ReadBlock(BlockAddress.FromLBA(0), pageBuffer);

                // Try to parse master block
                var masterBlock = MasterBlock.FromBytes(pageBuffer);
                return masterBlock.IsValid();
            }
            catch
            {
                return false;
            }
        }

        public DirectoryEntry[] ListDirectory(string path)
        {
            EnsureInitialized();

            var entries = new List<DirectoryEntry>();

            // Normalize path: remove leading/trailing slashes
            if (string.IsNullOrEmpty(path))
                path = string.Empty;
            else
            {
                path = path.Trim('/');
            }

            // NDFS has no real directories - only users as virtual folders
            // Root level (empty path): show users as directories
            // User level (single component): show that user's files

            if (string.IsNullOrEmpty(path))
            {
                // Root level - list all users as directories
                var users = _userFile.GetUsers();
                for (int i = 0; i < users.Count; i++)
                {
                    var user = users[i];

                    entries.Add(new DirectoryEntry
                    {
                        Name = user.UserName,
                        Metadata = new FileMetadata
                        {
                            Name = user.UserName,
                            Path = $"/{user.UserName}",
                            Size = 0,
                            IsDirectory = true,
                            FileType = "NDFS User",
                            CreatedTime = NdDateTime.FromNdDate(user.DateCreated),
                            ModifiedTime = NdDateTime.FromNdDate(user.LastDateEntered),
                            Attributes = $"UserIndex:{user.UserIndex},Reserved:{user.PagesReserved},Used:{user.PagesUsed}"
                        }
                    });
                }
            }
            else
            {
                // Check if this is a user directory or a file path
                var parts = path.Split('/');

                if (parts.Length == 1)
                {
                    // User level - list files for this user
                    var userName = parts[0];
                    var objects = _objectFile.GetUserObjects(userName);

                    for (int i = 0; i < objects.Count; i++)
                    {
                        var obj = objects[i];

                        // Combine object name and type (e.g. "FILENAME:TYPE")
                        // NDFS uses colon as separator, type is 4 chars like :MODE, :SYMB, :PROG, :BPUN
                        string displayName = string.IsNullOrEmpty(obj.Type)
                            ? obj.ObjectName
                            : $"{obj.ObjectName}{ExtensionSeparator}{obj.Type.TrimEnd('\x27', ' ')}";

                        entries.Add(new DirectoryEntry
                        {
                            Name = displayName,
                            Metadata = new FileMetadata
                            {
                                // Use the full display name (NAME:TYPE) as the metadata Name too.
                                // The UI surfaces entry.Metadata.Name, so using the bare ObjectName
                                // here dropped the ":TYPE" (e.g. "fs.bpun" showed as just "FS").
                                Name = displayName,
                                Path = $"/{obj.UserName}/{displayName}",
                                Size = (long)obj.BytesInFile,
                                IsDirectory = false,
                                FileType = obj.FileTypeAsText,
                                CreatedTime = NdDateTime.FromNdDate(obj.DateCreated),
                                ModifiedTime = NdDateTime.FromNdDate(obj.LastDateOpenedForWrite),
                                Attributes = $"User:{obj.UserName}"
                            }
                        });
                    }
                }
                else
                {
                    // This is a file path (USER/FILENAME), not a directory listing
                    // NDFS doesn't support subdirectories, so this is invalid for listing
                    throw new InvalidOperationException($"Cannot list directory at path '{path}'. NDFS does not support subdirectories.");
                }
            }

            return entries.ToArray();
        }

        public byte[] ReadFile(string path)
        {
            EnsureInitialized();

            var (userName, fileName) = ParseNdfsPath(path);

            // Normalize filename: convert . separator to : for lookup compatibility
            fileName = NormalizeFileNameForLookup(fileName);

            ObjectEntry? obj = null;

            if (string.IsNullOrEmpty(userName))
            {
                // Search all objects by full name (objectname:type) - NO LINQ
                var objects = _objectFile.GetObjects();
                for (int i = 0; i < objects.Count; i++)
                {
                    var o = objects[i];
                    var fullName = string.IsNullOrEmpty(o.Type)
                        ? o.ObjectName
                        : $"{o.ObjectName}{ExtensionSeparator}{o.Type.TrimEnd('\x27', ' ')}";
                    if (string.Equals(fullName, fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        obj = o;
                        break;
                    }
                }
            }
            else
            {
                // Search by user and filename - NO LINQ
                var objects = _objectFile.GetObjects();
                for (int i = 0; i < objects.Count; i++)
                {
                    var o = objects[i];
                    if (!string.Equals(o.UserName, userName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var fullName = string.IsNullOrEmpty(o.Type)
                        ? o.ObjectName
                        : $"{o.ObjectName}{ExtensionSeparator}{o.Type.TrimEnd('\x27', ' ')}";
                    if (string.Equals(fullName, fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        obj = o;
                        break;
                    }
                }
            }

            if (obj == null)
                throw new FileNotFoundException($"File not found: {path}");

            return ReadObjectData(obj);
        }

        public void WriteFile(string path, byte[] data)
        {
            EnsureInitialized();

            if (_device == null || _device.IsReadOnly)
                throw new InvalidOperationException("Cannot write to read-only device");

            if (data == null)
                throw new ArgumentNullException(nameof(data));

            var (userName, fileName) = ParseNdfsPath(path);

            if (string.IsNullOrEmpty(fileName))
                throw new ArgumentException("Invalid path: filename required", nameof(path));

            UserEntry user = ResolveUserForWrite(userName);

            // Parse filename into object name and type
            var (objectName, fileType) = ParseFileName(fileName);

            // Calculate required pages for this file. Quota tracks only REAL disk consumption —
            // sparse (all-zero) pages allocate no block and the index/sub-index structural
            // blocks are filesystem overhead, not charged to the user — see CountRealDataPages.
            uint requiredPages = CountRealDataPages(data);

            // Check if file already exists
            var existingObj = _objectFile.FindObject(objectName, user.UserName);

            // Calculate how many additional pages are needed
            uint additionalPagesNeeded = requiredPages;
            if (existingObj != null && existingObj.FileTypeFlags.IsContiguous())
            {
                // A contiguous file already owns every page it will ever have, so a write can never
                // need more. Skipping the quota step matters: the check below would otherwise
                // expand the user's reservation for pages that are never allocated, and it would do
                // so before UpdateExistingContiguousFile rejects an oversized write.
                additionalPagesNeeded = 0;
            }
            else if (existingObj != null)
            {
                // When updating, we free old pages and allocate new ones
                // So we need to check if user has quota for the new size
                uint existingPages = 0;
                if (existingObj.FilePointer != null && existingObj.FilePointer.BlockID > 0)
                {
                    var existingBlocks = GetAllBlockPointersForFile(existingObj.FilePointer, existingObj.BytesInFile);
                    existingPages = CountRealDataPages(existingBlocks);
                }

                if (requiredPages > existingPages)
                    additionalPagesNeeded = requiredPages - existingPages;
                else
                    additionalPagesNeeded = 0;
            }

            // Check and expand quota if needed
            if (additionalPagesNeeded > 0)
            {
                uint pagesAvailableToUser = user.PagesReserved - user.PagesUsed;

                if (pagesAvailableToUser < additionalPagesNeeded)
                {
                    // Need to expand quota
                    uint expansionNeeded = additionalPagesNeeded - pagesAvailableToUser;
                    uint availableOnDisk = _bitFile.GetFreePages();

                    if (availableOnDisk < expansionNeeded)
                        throw new InvalidOperationException($"Insufficient disk space. Need {expansionNeeded} pages, only {availableOnDisk} available.");

                    // Automatically expand user quota
                    uint newQuota = user.PagesReserved + expansionNeeded;
                    user.PagesReserved = newQuota;

                    // Write just this user's page
                    if (_masterBlock?.UserFilePointer != null)
                        _userFile.WritePageForEntry(_device, _masterBlock.UserFilePointer, user.UserIndex);
                }
            }

            if (existingObj != null)
            {
                // Update existing file
                UpdateExistingFile(existingObj, data);
            }
            else
            {
                // Create new file
                CreateNewFile(objectName, fileType, user, data);
            }
        }

        public void DeleteFile(string path)
        {
            EnsureInitialized();

            if (_device == null || _device.IsReadOnly)
                throw new InvalidOperationException("Cannot delete from read-only device");

            var (userName, fileName) = ParseNdfsPath(path);

            if (string.IsNullOrEmpty(fileName))
                throw new ArgumentException("Invalid path: filename required", nameof(path));

            // Parse filename
            var (objectName, fileType) = ParseFileName(fileName);

            // Find file - NO LINQ
            ObjectEntry? obj = null;
            if (!string.IsNullOrEmpty(userName))
            {
                obj = _objectFile.FindObject(objectName, userName);
            }
            else
            {
                // Search all users - NO LINQ
                var objects = _objectFile.GetObjects();
                for (int i = 0; i < objects.Count; i++)
                {
                    if (string.Equals(objects[i].ObjectName, objectName, StringComparison.OrdinalIgnoreCase))
                    {
                        obj = objects[i];
                        break;
                    }
                }
            }

            if (obj == null)
                throw new InvalidOperationException($"File not found: {path}");

            // Get the user for updating pages used
            var user = _userFile.GetUser(obj.UserIndex);

            // Free blocks used by file
            List<BlockPointer>? blocks = null;
            if (obj.FilePointer != null && obj.FilePointer.BlockID > 0)
            {
                blocks = GetAllBlockPointersForFile(obj.FilePointer, obj.BytesInFile);
                FreeFileBlocks(blocks);

                // Also free the top-level index/sub-index block, plus (for SubIndexed) every
                // group's own index block.
                FreeStructuralBlocks(obj.FilePointer);
            }

            // Update user's pages used. Quota tracks only REAL disk consumption — count actual
            // data blocks freed (blocks already excludes structural entries and sparse holes
            // report BlockID==0, per GetAllBlockPointersForFile), never the index/sub-index
            // block itself. See CountRealDataPages.
            if (user != null)
            {
                uint totalBlocks = blocks != null ? CountRealDataPages(blocks) : 0;

                if (user.PagesUsed >= totalBlocks)
                    user.PagesUsed -= totalBlocks;
                else
                    user.PagesUsed = 0; // Safety check
            }

            // Remove from object file
            int deletedObjectIndex = obj.ObjectIndex;
            _objectFile.RemoveObject(deletedObjectIndex);

            // Write just the affected pages
            if (_masterBlock != null && _device != null)
            {
                if (_masterBlock.UserFilePointer != null && user != null)
                    _userFile.WritePageForEntry(_device, _masterBlock.UserFilePointer, user.UserIndex);

                if (_masterBlock.ObjectFilePointer != null)
                    _objectFile.WritePageForEntry(_device, _masterBlock.ObjectFilePointer, deletedObjectIndex);

                if (_masterBlock.BitFilePointer != null)
                    _bitFile.WriteToDisk(_device, _masterBlock.BitFilePointer);

                // File deletion frees pages back to the bitmap — refresh the cached
                // "Unreserved Pages" count so SINTRAN's free-space report stays accurate.
                PersistMasterBlock();
            }
        }

        public FileMetadata? GetMetadata(string path)
        {
            EnsureInitialized();

            var (userName, fileName) = ParseNdfsPath(path);

            // Normalize filename: convert . separator to : for lookup compatibility
            fileName = NormalizeFileNameForLookup(fileName);

            // NO LINQ - search by full name (objectname:type)
            ObjectEntry? obj = null;
            var objects = _objectFile.GetObjects();
            for (int i = 0; i < objects.Count; i++)
            {
                var o = objects[i];
                // Filter by user if specified
                if (!string.IsNullOrEmpty(userName) &&
                    !string.Equals(o.UserName, userName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var fullName = string.IsNullOrEmpty(o.Type)
                    ? o.ObjectName
                    : $"{o.ObjectName}{ExtensionSeparator}{o.Type.TrimEnd('\x27', ' ')}";
                if (string.Equals(fullName, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    obj = o;
                    break;
                }
            }

            if (obj == null)
                return null;

            // Build display name with type extension (e.g., "FILENAME:PROG")
            string displayName = string.IsNullOrEmpty(obj.Type)
                ? obj.ObjectName
                : $"{obj.ObjectName}{ExtensionSeparator}{obj.Type.TrimEnd('\x27', ' ')}";

            return new FileMetadata
            {
                Name = displayName,
                Path = $"/{obj.UserName}/{displayName}",
                Size = (long)obj.BytesInFile,
                IsDirectory = false,
                FileType = obj.FileTypeAsText,
                Attributes = $"User:{obj.UserName},Pages:{obj.PagesInFile},Pointer:{obj.FilePointer}"
            };
        }

        public int GetBlocksFree()
        {
            EnsureInitialized();
            return (int)_bitFile.GetFreePages();
        }

        public void CreateDirectory(string path)
        {
            EnsureInitialized();

            if (_device == null || _device.IsReadOnly)
                throw new InvalidOperationException("Cannot create directory on read-only device");

            // Parse path - for NDFS, creating directory at root means creating a user
            // ParseNdfsPath("/NEWUSER") returns (userName="", fileName="NEWUSER")
            // We interpret this as creating user "NEWUSER"
            var (parsedUser, parsedFile) = ParseNdfsPath(path);

            // For root-level path like "/NEWUSER", swap the interpretation
            string userName;
            if (string.IsNullOrEmpty(parsedUser) && !string.IsNullOrEmpty(parsedFile))
            {
                // Root-level directory → user name
                userName = parsedFile;
            }
            else if (!string.IsNullOrEmpty(parsedUser) && !string.IsNullOrEmpty(parsedFile))
            {
                // Path like "/USER/SUBDIR" → subdirectories not supported
                throw new InvalidOperationException("NDFS does not support subdirectories. Directories can only be created at root level to add users.");
            }
            else
            {
                throw new ArgumentException("Invalid path: user name required", nameof(path));
            }

            // Start with a reasonable default quota (10% of available space, min 100 pages)
            uint availablePages = _bitFile.GetFreePages();
            uint defaultQuota = Math.Max(100, availablePages / 10);

            // Add user with default quota
            bool success = AddUser(userName, defaultQuota);
            if (!success)
                throw new InvalidOperationException($"Failed to create user '{userName}'. User may already exist.");
        }

        public void DeleteDirectory(string path)
        {
            EnsureInitialized();

            if (_device == null || _device.IsReadOnly)
                throw new InvalidOperationException("Cannot delete directory on read-only device");

            // Parse path - for NDFS, deleting directory at root means deleting a user
            // ParseNdfsPath("/TEMPUSER") returns (userName="", fileName="TEMPUSER")
            // We interpret this as deleting user "TEMPUSER"
            var (parsedUser, parsedFile) = ParseNdfsPath(path);

            // For root-level path like "/TEMPUSER", swap the interpretation
            string userName;
            if (string.IsNullOrEmpty(parsedUser) && !string.IsNullOrEmpty(parsedFile))
            {
                // Root-level directory → user name
                userName = parsedFile;
            }
            else if (!string.IsNullOrEmpty(parsedUser) && !string.IsNullOrEmpty(parsedFile))
            {
                // Path like "/USER/SUBDIR" → subdirectories not supported
                throw new InvalidOperationException("NDFS does not support subdirectories.");
            }
            else
            {
                throw new ArgumentException("Invalid path: user name required", nameof(path));
            }

            // Find user by name
            var user = _userFile.FindUser(userName);
            if (user == null)
                throw new DirectoryNotFoundException($"User '{userName}' not found");

            // Check if user has any files
            var userObjects = _objectFile.GetUserObjects(userName);
            if (userObjects.Count > 0)
                throw new InvalidOperationException($"Cannot delete user '{userName}': user has {userObjects.Count} files. Delete all files first.");

            // Remove user
            bool success = RemoveUser(user.UserIndex);
            if (!success)
                throw new InvalidOperationException($"Failed to delete user '{userName}'");
        }

        public void Rename(string oldPath, string newPath)
        {
            EnsureInitialized();

            if (_device == null || _device.IsReadOnly)
                throw new InvalidOperationException("Cannot rename on read-only device");

            var (oldUserName, oldFileName) = ParseNdfsPath(oldPath);
            var (newUserName, newFileName) = ParseNdfsPath(newPath);

            // Renaming users (directories at root) is not allowed
            if (string.IsNullOrEmpty(oldFileName) && string.IsNullOrEmpty(newFileName))
                throw new NotSupportedException("Renaming users is not supported in NDFS");

            // Moving files between users is not allowed
            if (!string.IsNullOrEmpty(oldUserName) && !string.IsNullOrEmpty(newUserName) &&
                !string.Equals(oldUserName, newUserName, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Moving files between users is not supported in NDFS");

            // Renaming files within same user
            if (string.IsNullOrEmpty(oldFileName) || string.IsNullOrEmpty(newFileName))
                throw new ArgumentException("Invalid path for file rename");

            // Find the file - NO LINQ
            ObjectEntry? obj = null;
            if (!string.IsNullOrEmpty(oldUserName))
            {
                obj = _objectFile.FindObject(oldFileName, oldUserName);
            }
            else
            {
                // Search all users - NO LINQ
                var objects = _objectFile.GetObjects();
                for (int i = 0; i < objects.Count; i++)
                {
                    var o = objects[i];
                    var fullName = string.IsNullOrEmpty(o.Type)
                        ? o.ObjectName
                        : $"{o.ObjectName}{ExtensionSeparator}{o.Type.TrimEnd('\x27', ' ')}";
                    if (string.Equals(fullName, oldFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        obj = o;
                        break;
                    }
                }
            }

            if (obj == null)
                throw new FileNotFoundException($"File not found: {oldPath}");

            // Parse new filename
            var (newObjectName, newFileType) = ParseFileName(newFileName);

            // Update object entry
            obj.ObjectName = newObjectName.ToUpper();
            obj.Type = newFileType.ToUpper();

            // Write just this entry's page
            if (_masterBlock?.ObjectFilePointer != null)
                _objectFile.WritePageForEntry(_device, _masterBlock.ObjectFilePointer, obj.ObjectIndex);
        }

        // ==================== INdfsFileSystem Implementation ====================

        public IReadOnlyList<UserEntry> GetUsers()
        {
            EnsureInitialized();
            return _userFile.GetUsers();
        }

        public UserEntry? GetUser(int userIndex)
        {
            EnsureInitialized();
            return _userFile.GetUser(userIndex);
        }

        public bool AddUser(string userName, uint reservedPages)
        {
            EnsureInitialized();

            if (string.IsNullOrWhiteSpace(userName))
                throw new ArgumentException("User name cannot be empty", nameof(userName));

            if (userName.Length > 16)
                throw new ArgumentException("User name cannot exceed 16 characters", nameof(userName));

            // Check if user already exists
            if (_userFile.FindUser(userName) != null)
                return false;

            // Get next available user index
            int userIndex = _userFile.GetNextAvailableIndex();

            // Create new user entry
            var newUser = new UserEntry
            {
                // Mark the slot as a used user entry. ToBytes writes UF verbatim now, so a zero here
                // would make FromBytes reject the slot on the next mount.
                UF = UserEntry.UfValidUser,
                UserIndex = userIndex,
                UserName = userName.ToUpperInvariant(),
                PagesReserved = reservedPages,
                PagesUsed = 0
            };

            // Add to user file
            _userFile.AddUser(newUser);

            // On a freshly-created image the user-file index page is empty (the image creator only
            // writes a zero-filled index — "SINTRAN initializes users on first mount"), so the data
            // page that will hold this entry has not been allocated yet. WritePageForEntry silently
            // no-ops when the page pointer is 0, which is why a newly-added user never persisted.
            // Allocate (and persist) the data page first so the entry actually reaches disk.
            EnsureUserFilePageAllocated(newUser.UserIndex);

            if (_device == null || _masterBlock == null || _masterBlock.UserFilePointer == null)
                throw new InvalidOperationException("Filesystem not properly initialized");

            // Write just this user's page
            _userFile.WritePageForEntry(_device, _masterBlock.UserFilePointer, newUser.UserIndex);

            // EnsureUserFilePageAllocated may have allocated a fresh bitmap page above —
            // refresh the cached "Unreserved Pages" count so it stays accurate on disk.
            PersistMasterBlock();

            return true;
        }

        /// <summary>
        /// Ensures the user-file data page that holds <paramref name="userIndex"/> is allocated and
        /// recorded in the user-file index block. NDFS user entries are 64 bytes, 32 per 2 KB page;
        /// the index block at <c>UserFilePointer</c> holds a 4-byte (big-endian) data-page pointer
        /// per page at offset pageNum*4. If that pointer is 0 (page not yet allocated), grab a free
        /// page from the bitmap, write the pointer into the index block, zero the new data page, and
        /// persist the bitmap — mirroring the object-file allocation path.
        /// </summary>
        private void EnsureUserFilePageAllocated(int userIndex)
        {
            if (_device == null || _masterBlock == null)
                return;

            const int pageSize = NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE; // 2048
            const int entriesPerPage = 32;                                 // 2048 / 64
            int pageNum = userIndex / entriesPerPage;

            if (_masterBlock.UserFilePointer == null)
                throw new InvalidOperationException("User file index block is not allocated");

            uint indexBlockId = _masterBlock.UserFilePointer.BlockID;
            if (indexBlockId == 0)
                throw new InvalidOperationException("User file index block is not allocated");

            // Read the user-file index block and look at this page's data pointer.
            byte[] indexBuffer = new byte[pageSize];
            _device.ReadBlock(RetroIO.Storage.Core.BlockAddress.FromLBA(indexBlockId), indexBuffer);

            BlockPointer dataPointer = BlockPointer.FromBytes(indexBuffer, pageNum * 4);
            if (dataPointer.BlockID != 0)
                return; // already allocated — WritePageForEntry can write directly

            // Allocate a fresh data page from the free-space bitmap.
            uint? freeBlock = _bitFile.FindFirstFreeBlock();
            if (freeBlock == null)
                throw new InvalidOperationException("No free pages available to allocate a user-file page");
            _bitFile.AllocateBlocks(freeBlock.Value, 1);

            // Record the new data page in the index block (Contiguous data pointer) and persist it.
            BlockPointer newPointer = new BlockPointer(freeBlock.Value, BlockPointer.PointerType.Contiguous);
            byte[] ptrBytes = newPointer.ToBytes();
            Array.Copy(ptrBytes, 0, indexBuffer, pageNum * 4, 4);
            _device.WriteBlock(RetroIO.Storage.Core.BlockAddress.FromLBA(indexBlockId), indexBuffer);

            // Zero-initialize the new data page so stale disk bytes are not mistaken for entries.
            byte[] zeroed = new byte[pageSize];
            _device.WriteBlock(RetroIO.Storage.Core.BlockAddress.FromLBA(freeBlock.Value), zeroed);

            // Persist the bitmap so the allocation survives a remount.
            if (_masterBlock.BitFilePointer == null)
                throw new InvalidOperationException("Bit file is not allocated");

            _bitFile.WriteToDisk(_device, _masterBlock.BitFilePointer);
        }

        public bool RemoveUser(int userIndex)
        {
            EnsureInitialized();

            // Get user to verify it exists
            var user = _userFile.GetUser(userIndex);
            if (user == null)
                return false;

            // Check if user has any files
            var userObjects = _objectFile.GetUserObjects(user.UserName);
            if (userObjects.Count > 0)
                throw new InvalidOperationException($"Cannot delete user {user.UserName}: user has {userObjects.Count} files. Delete files first.");

            // Remove user from user file
            bool removed = _userFile.RemoveUser(userIndex);
            if (!removed)
                return false;

            if (_device == null || _masterBlock == null || _masterBlock.UserFilePointer == null)
                throw new InvalidOperationException("Filesystem not properly initialized");

            // Write just this user's page
            _userFile.WritePageForEntry(_device, _masterBlock.UserFilePointer, userIndex);

            return true;
        }

        public bool UpdateUserQuota(int userIndex, uint newReservedPages)
        {
            EnsureInitialized();
            return _userFile.UpdateUserQuota(userIndex, newReservedPages);
        }

        public bool ClearUserPassword(int userIndex)
        {
            EnsureInitialized();

            if (_device == null || _masterBlock == null || _masterBlock.UserFilePointer == null)
                return false;

            var user = _userFile.GetUser(userIndex);
            if (user == null)
                return false;

            user.Password = 0;

            if (_device.IsReadOnly)
                return false;

            _userFile.WritePageForEntry(_device, _masterBlock.UserFilePointer, userIndex);

            // GenericBlockDevice buffers writes into an in-memory image and only writes them back to
            // the underlying stream on Flush()/Dispose(). Without this flush the cleared password lived
            // only in memory and was lost when the device was disposed AFTER its stream (CanWrite==false
            // makes the dispose-time flush a silent no-op) — the reason `NdfsPasswordTool clear` printed
            // "Changes saved" while the on-disk password stayed unchanged. Flush here so the change
            // reaches disk immediately, independent of dispose ordering.
            _device.Flush();
            return true;
        }

        public bool ClearUserPassword(string userName)
        {
            EnsureInitialized();

            if (string.IsNullOrEmpty(userName))
                return false;

            var user = _userFile.FindUser(userName);
            if (user == null)
                return false;

            return ClearUserPassword(user.UserIndex);
        }

        public string? GetPasswordClearInstructions(int userIndex)
        {
            EnsureInitialized();

            if (_device == null || _masterBlock == null || _masterBlock.UserFilePointer == null)
                return null;

            var user = _userFile.GetUser(userIndex);
            if (user == null)
                return null;

            var userFileIndexPointer = _masterBlock.UserFilePointer;
            if (userFileIndexPointer.Type != BlockPointer.PointerType.Indexed)
                return null;

            var indexBuffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            _device.ReadBlock(BlockAddress.FromLBA(userFileIndexPointer.BlockID), indexBuffer);

            int dataBlockIndex = userIndex / 32;
            int userOffsetInBlock = userIndex % 32;

            if (dataBlockIndex >= 8)
                return null;

            var dataPointer = BlockPointer.FromBytes(indexBuffer, dataBlockIndex * 4);
            if (dataPointer.BlockID == 0)
                return null;

            uint userFileIndexPage = userFileIndexPointer.BlockID;
            uint dataBlockPage = dataPointer.BlockID;
            int userEntryWordOffset = userOffsetInBlock * 32;
            int passwordWordOffset = userEntryWordOffset + 9;

            // Calculate absolute byte offset in file for hex editors
            long absoluteByteOffset = (long)dataBlockPage * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE + (userOffsetInBlock * 64) + 18;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== CLEAR PASSWORD: User '{user.UserName}' (Index {userIndex}) ===");
            sb.AppendLine();
            sb.AppendLine("FILE-SYSTEM-INVESTIGATOR Instructions:");
            sb.AppendLine();
            sb.AppendLine("1. Start FILE-SYSTEM-INVESTIGATOR:");
            sb.AppendLine("   @FILE-SYSTEM-INVESTIGATOR");
            sb.AppendLine("   Password: OK");
            sb.AppendLine();
            sb.AppendLine("2. [OPTIONAL] View current password value:");
            sb.AppendLine("   DUMP-PAGE");
            sb.AppendLine($"   Page no. ( 0-110120 ): {Convert.ToString(dataBlockPage, 8)}");
            sb.AppendLine($"   First wordno. (0-01777): {Convert.ToString(passwordWordOffset, 8)}");
            sb.AppendLine($"   Second wordno. (000000  - 001777): {Convert.ToString(passwordWordOffset, 8)}");
            sb.AppendLine("   Ascii or octal (A/O): O");
            sb.AppendLine();
            sb.AppendLine("3. Clear password to zero:");
            sb.AppendLine("   CHANGE-PAGE");
            sb.AppendLine($"   Give page no. ( 0-110120 ): {Convert.ToString(dataBlockPage, 8)}");
            sb.AppendLine("   Ready:");
            sb.AppendLine($"   {Convert.ToString(dataBlockPage, 8)}/{Convert.ToString(passwordWordOffset, 8)}");
            sb.AppendLine("   <current_password_value>    <- Will be displayed");
            sb.AppendLine("   000000                       <- Type this to clear");
            sb.AppendLine("   .                            <- Type period to save");
            sb.AppendLine();
            sb.AppendLine("4. [OPTIONAL] Verify password cleared:");
            sb.AppendLine("   DUMP-PAGE");
            sb.AppendLine($"   Page no. ( 0-110120 ): {Convert.ToString(dataBlockPage, 8)}");
            sb.AppendLine($"   First wordno. (0-01777): {Convert.ToString(passwordWordOffset, 8)}");
            sb.AppendLine($"   Second wordno. (000000  - 001777): {Convert.ToString(passwordWordOffset, 8)}");
            sb.AppendLine("   Ascii or octal (A/O): O");
            sb.AppendLine("   (Should show 000000 at the password word)");
            sb.AppendLine();
            sb.AppendLine("=== TECHNICAL DETAILS ===");
            sb.AppendLine($"Master Block Page:        0");
            sb.AppendLine($"User File Index Page:     {userFileIndexPage} (octal: {Convert.ToString(userFileIndexPage, 8)})");
            sb.AppendLine($"User Data Block Page:     {dataBlockPage} (octal: {Convert.ToString(dataBlockPage, 8)})");
            sb.AppendLine($"Data Block Index:         {dataBlockIndex} (users {dataBlockIndex * 32}-{dataBlockIndex * 32 + 31})");
            sb.AppendLine($"User Offset in Block:     {userOffsetInBlock}");
            sb.AppendLine($"User Entry Word Offset:   {userEntryWordOffset} (octal: {Convert.ToString(userEntryWordOffset, 8)})");
            sb.AppendLine($"Password Word Offset:     {passwordWordOffset} (decimal) = {Convert.ToString(passwordWordOffset, 8)} (octal)");
            sb.AppendLine($"Password Byte Offset:     {userOffsetInBlock * 64 + 18}-{userOffsetInBlock * 64 + 19}");
            sb.AppendLine($"Current Password Hash:    {user.Password} (decimal) = {Convert.ToString(user.Password, 8)} (octal) = 0x{user.Password:X4} (hex)");
            sb.AppendLine();
            sb.AppendLine("NOTE: All numbers in FILE-SYSTEM-INVESTIGATOR commands are OCTAL (base 8).");
            sb.AppendLine("      Word offset 11 (octal) = 9 (decimal)");
            sb.AppendLine();
            sb.AppendLine("=== HEX EDITOR INSTRUCTIONS (Windows/Linux/Unix) ===");
            sb.AppendLine($"File Offset (Decimal):    {absoluteByteOffset} bytes from start of file");
            sb.AppendLine($"File Offset (Hex):        0x{absoluteByteOffset:X} (0x{absoluteByteOffset:X8})");
            sb.AppendLine($"Bytes to Change:          2 bytes (big-endian 16-bit word)");
            sb.AppendLine($"Current Value (Hex):      {(user.Password >> 8):X2} {(user.Password & 0xFF):X2}");
            sb.AppendLine($"New Value (Clear):        00 00");
            sb.AppendLine();
            sb.AppendLine("Steps for Hex Editor:");
            sb.AppendLine("1. Open disk image in hex editor (HxD, ImHex, xxd, hexedit, etc.)");
            sb.AppendLine($"2. Go to offset 0x{absoluteByteOffset:X} (or decimal {absoluteByteOffset})");
            sb.AppendLine($"3. Current bytes should be: {(user.Password >> 8):X2} {(user.Password & 0xFF):X2}");
            sb.AppendLine("4. Change both bytes to: 00 00");
            sb.AppendLine("5. Save file");
            sb.AppendLine();
            sb.AppendLine("Common Hex Editors:");
            sb.AppendLine("  Windows:  HxD, ImHex, Hex Workshop");
            sb.AppendLine("  Linux:    hexedit, ghex, ImHex, xxd");
            sb.AppendLine("  macOS:    Hex Fiend, ImHex, xxd");
            sb.AppendLine($"  Command:  printf '\\x00\\x00' | dd of=disk.img bs=1 seek={absoluteByteOffset} count=2 conv=notrunc");

            return sb.ToString();
        }

        public string? GetPasswordClearInstructions(string userName)
        {
            EnsureInitialized();

            if (string.IsNullOrEmpty(userName))
                return null;

            var user = _userFile.FindUser(userName);
            if (user == null)
                return null;

            return GetPasswordClearInstructions(user.UserIndex);
        }

        public BitFile GetBitFile()
        {
            EnsureInitialized();
            return _bitFile;
        }

        public bool IsBlockUsed(uint blockId)
        {
            EnsureInitialized();
            return _bitFile.IsBlockUsed(blockId);
        }

        public uint GetUnreservedPages()
        {
            EnsureInitialized();
            return _masterBlock?.UnreservedPages ?? 0;
        }

        public uint GetPagesAvailable()
        {
            EnsureInitialized();
            return _bitFile.GetFreePages();
        }

        public IReadOnlyList<ObjectEntry> GetObjectEntries()
        {
            EnsureInitialized();
            return _objectFile.GetObjects();
        }

        public ObjectEntry? GetObjectEntry(string objectName, string userName)
        {
            EnsureInitialized();
            return _objectFile.FindObject(objectName, userName);
        }

        public ObjectFile GetObjectFile()
        {
            EnsureInitialized();
            return _objectFile;
        }

        public MasterBlock GetMasterBlock()
        {
            EnsureInitialized();
            return _masterBlock!;
        }

        public string GetDirectoryName()
        {
            EnsureInitialized();
            return _masterBlock?.DirectoryName ?? string.Empty;
        }

        /// <summary>
        /// Renames the directory (volume) by rewriting the directory-name field of the 32-byte
        /// master block at page-0 offset 0x07E0. The name is up to 16 ASCII bytes, terminated by
        /// 0x27 (<c>'</c>) when shorter than 16 (SINTRAN convention). This does NOT touch the
        /// separate Extended Info block or its checksum — the name is not part of the checksummed
        /// range (words 1751B–1757B), so no checksum recompute is needed. Requires a read-write mount.
        /// </summary>
        /// <param name="name">
        /// New directory name, 1 to 16 printable ASCII characters.
        /// </param>
        /// <returns>
        /// True if the name was changed and persisted; false if the device is read-only.
        /// </returns>
        public bool SetDirectoryName(string name)
        {
            EnsureInitialized();

            if (name == null)
                throw new ArgumentNullException(nameof(name));
            if (name.Length == 0)
                throw new ArgumentException("Directory name cannot be empty", nameof(name));

            // NDFS directory names are ASCII, max 16 bytes, printable only.
            var bytes = Encoding.ASCII.GetBytes(name);
            if (bytes.Length > 16)
                throw new ArgumentException("Directory name cannot exceed 16 characters", nameof(name));
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] < 0x20 || bytes[i] > 0x7E)
                    throw new ArgumentException("Directory name must be printable ASCII", nameof(name));
            }

            if (_device == null || _masterBlock == null || _device.IsReadOnly)
                return false;

            _masterBlock.DirectoryName = name;

            // PersistMasterBlock rewrites only the 32-byte master-block region of page 0 (name +
            // pointers + unreserved pages), leaving the Extended Info block and boot code intact.
            PersistMasterBlock();

            // GenericBlockDevice buffers writes in memory and only writes them back on Flush()/Dispose().
            // Flush now so an interactive rename hits disk immediately, independent of when (and in what
            // order) the mount is later disposed — same guarantee as ClearUserPassword / initial commands.
            _device.Flush();
            return true;
        }

        /// <summary>
        /// Clears flag-word bit 15 ("directory entered") in the Extended Info block and
        /// rewrites the block with a recomputed additive checksum. Mirrors the kernel
        /// release routine REENB (40162B). See <see cref="INdfsFileSystem.ClearEnteredDirectory"/>.
        /// </summary>
        public bool ClearEnteredDirectory()
        {
            EnsureInitialized();

            if (_device == null || _masterBlock == null || _device.IsReadOnly)
                return false;

            // Only meaningful on volumes that carry a valid Extended Info block
            // (hard disks). FLOMON floppies have no such block.
            if (!_masterBlock.ExtValid)
                return false;

            // Nothing to do if the interlock is already clear.
            if (!_masterBlock.IsDirectoryEntered)
                return false;

            // Clear bit 15, preserving bits 0–14 (no kernel-assigned meaning).
            _masterBlock.ExtFlagWord &= unchecked((uint)~MasterBlock.EXT_FLAG_ENTERED);

            // Read page 0 fresh and patch only the 8-word Extended Info block so the
            // master block and boot code are left byte-for-byte intact.
            var pageBuffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            _device.ReadBlock(BlockAddress.FromLBA(0), pageBuffer);
            _masterBlock.WriteExtendedInfoToBytes(pageBuffer);
            _device.WriteBlock(BlockAddress.FromLBA(0), pageBuffer);

            // Flush the buffered write to disk immediately (see SetDirectoryName / ClearUserPassword).
            _device.Flush();
            return true;
        }

        public string GenerateBitmapReport()
        {
            EnsureInitialized();

            var sb = new StringBuilder();
            sb.AppendLine("=== NDFS Bitmap Report ===");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Total Pages: {0}", _bitFile.TotalPages));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Used Pages: {0}", _bitFile.CalcUsedPages()));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Free Pages: {0}", _bitFile.GetFreePages()));
            sb.AppendLine();

            // Check consistency
            uint filesystemUsed = 0;
            foreach (var obj in _objectFile.GetObjects())
            {
                filesystemUsed += obj.PagesInFile;

                // Check if all blocks for this file are marked as used
                var pointers = GetAllBlockIdsForObjectIncludingIndex(obj);
                foreach (var pointer in pointers)
                {
                    if (!_bitFile.IsBlockUsed(pointer))
                    {
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "ERROR: Block {0} used by {1} but not marked in bitmap", pointer, obj.ObjectName));
                    }
                }
            }

            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Pages used by files: {0}", filesystemUsed));

            return sb.ToString();
        }

        public string DumpFileSystemInfo()
        {
            EnsureInitialized();

            var sb = new StringBuilder();
            sb.AppendLine("=== NDFS Filesystem Information ===");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Directory Name: {0}", _masterBlock?.DirectoryName));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Image Size: {0} pages", _masterBlock?.ImageSize));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Unreserved Pages: {0}", _masterBlock?.UnreservedPages));
            sb.AppendLine();

            sb.AppendLine("=== Users ===");
            foreach (var user in _userFile.GetUsers())
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "[{0:D3}] {1,-16} - Used: {2}, Reserved: {3}", user.UserIndex, user.UserName, user.PagesUsed, user.PagesReserved));
            }
            sb.AppendLine();

            sb.AppendLine("=== Files ===");
            foreach (var obj in _objectFile.GetObjects())
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "[{0:D3}] ({1}){2}:{3} - {4}, {5} pages, {6} bytes", obj.ObjectIndex, obj.UserName, obj.ObjectName, obj.Type, obj.FileTypeAsText, obj.PagesInFile, obj.BytesInFile));
            }

            return sb.ToString();
        }

        public bool VerifyFileSystemIntegrity()
        {
            EnsureInitialized();

            try
            {
                // Check master block
                if (!_masterBlock!.IsValid())
                    return false;

                // Check bitmap consistency
                foreach (var obj in _objectFile.GetObjects())
                {
                    var pointers = GetAllBlockIdsForObjectIncludingIndex(obj);
                    foreach (var pointer in pointers)
                    {
                        if (!_bitFile.IsBlockUsed(pointer))
                            return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        // ==================== Private Methods ====================

        private void LoadFileSystemStructures()
        {
            if (_device == null || _masterBlock == null)
                throw new InvalidOperationException("Device and master block must be initialized");

            // Load user file first (needed for object file user names)
            if (_masterBlock.UserFilePointer != null)
            {
                LoadUserFile(_masterBlock.UserFilePointer);
            }

            // Load object file
            if (_masterBlock.ObjectFilePointer != null)
            {
                LoadObjectFile(_masterBlock.ObjectFilePointer);
            }

            // Link object entries to user names
            LinkObjectsToUsers();

            // Load bit file
            if (_masterBlock.BitFilePointer != null)
            {
                LoadBitFile(_masterBlock.BitFilePointer);
            }
        }

        private void LinkObjectsToUsers()
        {
            // Map user index to user name for all objects
            var objects = _objectFile.GetObjects();
            for (int i = 0; i < objects.Count; i++)
            {
                var obj = objects[i];
                var user = _userFile.GetUser(obj.UserIndex);
                if (user != null)
                {
                    obj.UserName = user.UserName;
                }
            }
        }

        private void LoadObjectFile(BlockPointer pointer)
        {
            _objectFile.IndexPointer = pointer;

            if (_device == null || pointer == null || pointer.BlockID == 0)
                return;

            if (pointer.Type == BlockPointer.PointerType.SubIndexed)
            {
                // Sub-indexed: Read sub-index block containing pointers to index blocks
                var subIndexBuffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
                _device.ReadBlock(BlockAddress.FromLBA(pointer.BlockID), subIndexBuffer);

                for (int i = 0; i < 512; i++)
                {
                    var indexPointer = BlockPointer.FromBytes(subIndexBuffer, i * 4);

                    if (indexPointer.BlockID == 0)
                        continue;

                    if (indexPointer.Type == BlockPointer.PointerType.Indexed)
                    {
                        ReadIndexedObjectFileBlock(indexPointer, (uint)i);
                    }
                }
            }
            else if (pointer.Type == BlockPointer.PointerType.Indexed)
            {
                // Indexed: Direct index block
                ReadIndexedObjectFileBlock(pointer, 0);
            }
        }

        private void ReadIndexedObjectFileBlock(BlockPointer indexPointer, uint subindex)
        {
            if (_device == null)
                return;

            // Read the index block containing 512 pointers to data blocks
            var indexBuffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            _device.ReadBlock(BlockAddress.FromLBA(indexPointer.BlockID), indexBuffer);

            for (int i = 0; i < 512; i++)
            {
                var dataPointer = BlockPointer.FromBytes(indexBuffer, i * 4);

                if (dataPointer.BlockID == 0)
                    continue;

                // Must be Contiguous type
                if (dataPointer.Type != BlockPointer.PointerType.Contiguous)
                    continue;

                // Read the data block containing 32 object entries
                var dataBuffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
                _device.ReadBlock(BlockAddress.FromLBA(dataPointer.BlockID), dataBuffer);

                // Parse 32 object entries (each 64 bytes)
                for (int j = 0; j < 32; j++)
                {
                    int entryOffset = j * 64;

                    // Check if entry is in use (0x80 bit in header)
                    if ((dataBuffer[entryOffset] & 0x80) == 0)
                        continue;

                    var entry = ObjectEntry.FromBytes(dataBuffer, entryOffset);
                    if (entry != null && !string.IsNullOrEmpty(entry.ObjectName))
                    {
                        // ObjectIndex is the entry's PHYSICAL position: the GLOBAL directory-page
                        // index (subindex*512 + i, where subindex is which SubIndexed group this
                        // index block belongs to — 0 for a plain Indexed object file) * 32 + entry
                        // slot (j). This equals user<<8|fileEntry on a real disk, so the in-memory
                        // index matches SINTRAN's layout. Real bug fixed here: this used to ignore
                        // `subindex` entirely (always `i * 32 + j`), which was harmless for a plain
                        // Indexed object file (subindex is always 0 there) but would have produced
                        // colliding/wrong ObjectIndex values for every group beyond the first once
                        // the object file grows past 512 pages (16,384 files) into SubIndexed.
                        entry.ObjectIndex = (int)((subindex * 512) + (uint)i) * 32 + j;

                        // ObjectIndex above is the PHYSICAL position. The number SINTRAN shows the
                        // user is per-user and differs as soon as a user owns more than one object
                        // block, because block n sits a whole index block (512 pages) further on.
                        // Deriving the number - or the owner - from the physical position is right
                        // only inside a user's first block. See ObjectEntry.ComputeFileNumber.
                        // Provisional only. AssignFileNumbers() below recomputes it from the
                        // group RANK once the whole object file is loaded, which is the only
                        // correct basis - see that method.
                        entry.FileNumber = ObjectEntry.ComputeFileNumber(entry.ObjectIndex, entry.UserIndex);

                        _objectFile.AddObject(entry);
                    }
                }
            }

            AssignFileNumbers();
        }

        /// <summary>
        /// Assigns every entry the number SINTRAN reports — the 307 in "FILE 307".
        /// </summary>
        /// <remarks>
        /// <para><b>The number is not a function of the physical position</b></para>
        /// It is <c>logicalBlock * 256 + slotInBlock</c>, where logicalBlock is the ORDINAL
        /// RANK of the entry's index-block group among the groups that user occupies,
        /// ascending — NOT the physical group number.
        /// <para>
        /// That distinction only appears once an overflow block has been relocated, and SINTRAN
        /// relocates them freely. Measured live on a SINTRAN III K pack, 2026-08-02: user 8 with
        /// 300 files held block 0 in group 0 and its overflow in group 2. Creating files for user
        /// 136 — whose HOME is group 2, slot 8 — moved user 8's overflow to group 3; creating
        /// files for user 200 then moved it to group 4. All 300 files survived every move, and
        /// SINTRAN reported them throughout as FILE 0..299.
        /// </para>
        /// <para>
        /// The old <c>block = page / 512</c> formula gave that user 0..255 and then 1024..1067,
        /// where SINTRAN says 256..299. It looked correct on the earlier BIGMAN pack only because
        /// nothing had displaced that pack's overflow block, so rank happened to equal the group
        /// number.
        /// </para>
        /// <para>
        /// A user's blocks always sit at slot <c>user % 64</c> of a group; only the group varies.
        /// </para>
        /// </remarks>
        private void AssignFileNumbers()
        {
            const int entriesPerPage = NdfsObjectBlocks.EntriesPerPage;
            const int pagesPerIndexBlock = NdfsObjectBlocks.PagesPerIndexBlock;
            const int pagesPerObjectBlock = NdfsObjectBlocks.PagesPerObjectBlock;
            const int usersPerIndexBlock = NdfsObjectBlocks.UsersPerIndexBlock;

            Dictionary<int, List<ObjectEntry>> byUser = new Dictionary<int, List<ObjectEntry>>();
            IReadOnlyList<ObjectEntry> all = _objectFile.GetObjects();
            for (int i = 0; i < all.Count; i++)
            {
                ObjectEntry e = all[i];
                List<ObjectEntry>? list;
                if (!byUser.TryGetValue(e.UserIndex, out list))
                {
                    list = new List<ObjectEntry>();
                    byUser[e.UserIndex] = list;
                }
                list.Add(e);
            }

            foreach (KeyValuePair<int, List<ObjectEntry>> pair in byUser)
            {
                int userIndex = pair.Key;
                List<ObjectEntry> entries = pair.Value;

                // Distinct groups this user occupies, ascending: the rank IS the logical block.
                List<int> groups = new List<int>();
                for (int i = 0; i < entries.Count; i++)
                {
                    int g = (entries[i].ObjectIndex / entriesPerPage) / pagesPerIndexBlock;
                    if (!groups.Contains(g)) groups.Add(g);
                }
                groups.Sort();

                for (int i = 0; i < entries.Count; i++)
                {
                    ObjectEntry e = entries[i];
                    int page = e.ObjectIndex / entriesPerPage;
                    int group = page / pagesPerIndexBlock;

                    // The slot WITHIN a group is (user % 64) — 64 users fill one 512-page group.
                    int slotBase = (userIndex % usersPerIndexBlock) * pagesPerObjectBlock;
                    int offsetPages = (page % pagesPerIndexBlock) - slotBase;

                    if (offsetPages < 0 || offsetPages >= pagesPerObjectBlock)
                    {
                        // Outside this user's slot: report -1 rather than a plausible wrong
                        // number, so a mismatch surfaces instead of being believed.
                        e.FileNumber = -1;
                        continue;
                    }

                    int slot = offsetPages * entriesPerPage + (e.ObjectIndex % entriesPerPage);
                    e.FileNumber = groups.IndexOf(group) * NdfsObjectBlocks.FilesPerObjectBlock + slot;
                }
            }
        }

        private void LoadUserFile(BlockPointer pointer)
        {
            _userFile.IndexPointer = pointer;

            if (_device == null || pointer == null || pointer.BlockID == 0)
                return;

            // UserFile pointer must be Indexed type
            if (pointer.Type != BlockPointer.PointerType.Indexed)
                return;

            // Read the index block containing 8 pointers
            var indexBuffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            _device.ReadBlock(BlockAddress.FromLBA(pointer.BlockID), indexBuffer);

            // There are 8 pointers in this index block
            // Each pointer points to a block with 32 user entries (max 256 users total)
            for (int i = 0; i < 8; i++)
            {
                var userBlockPointer = BlockPointer.FromBytes(indexBuffer, i * 4);

                if (userBlockPointer.BlockID == 0)
                    continue;

                // Must be Contiguous type
                if (userBlockPointer.Type != BlockPointer.PointerType.Contiguous)
                    continue;

                // Read the user block
                var userBuffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
                _device.ReadBlock(BlockAddress.FromLBA(userBlockPointer.BlockID), userBuffer);

                // Parse 32 user entries (each 64 bytes)
                for (int j = 0; j < 32; j++)
                {
                    int offset = j * 64;
                    var entry = UserEntry.FromBytes(userBuffer, offset);

                    if (entry != null && !string.IsNullOrEmpty(entry.UserName))
                    {
                        entry.UserIndex = (i * 32) + j; // Calculate absolute user index
                        _userFile.AddUser(entry);
                    }
                }
            }
        }

        private void LoadBitFile(BlockPointer pointer)
        {
            _bitFile.IndexPointer = pointer;

            if (_device == null || pointer == null || pointer.BlockID == 0)
                return;

            // Initialize bitmap sized to the physical device geometry (SINTRAN sizes the
            // bitmap to device pages, not to declared capacity).
            _bitFile.Initialize(_device.TotalBlocks);

            // Bound the allocation window by the directory's declared capacity
            // (pages_available, extended-info words 1756B-1757B) when it is valid. SINTRAN's
            // downward allocator (RSPAG/TESTP) never allocates above capacity-1; the gap
            // between capacity and device size stays free-but-unreachable. Falls back to the
            // full device when Extended Info is absent (e.g. FLOMON floppies).
            if (_masterBlock != null && _masterBlock.ExtValid &&
                _masterBlock.ExtPagesAvailable > 0 &&
                _masterBlock.ExtPagesAvailable <= _device.TotalBlocks)
            {
                _bitFile.AllocCeiling = _masterBlock.ExtPagesAvailable;
            }

            // NDFS bitmap: one bit per page, 0 = free, 1 = used, LSB-first within each byte
            // The bit file is CONTIGUOUS: it spans ceil(TotalBlocks / 8) bytes, rounded up to
            // a whole 16-bit word, across consecutive pages starting at the pointer. Page N
            // lives at bit N%16 of the 16-bit big-endian word N/16 - i.e. global byte
            // (N>>3)^1, bit N&7. See BitFile.BitPosition. We MUST read the FULL
            // span so every reserved/used block is known — otherwise the allocator would treat
            // unread high blocks (including the bit file's own later pages and the object/user
            // index blocks) as free and hand them out, corrupting the filesystem.
            const int pageSize = NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
            // Whole 16-bit words: the bit file is word addressed (see BitFile.BitPosition), so
            // ceil(pages/8) cuts the last word in half on any device whose page count is not a
            // multiple of 16, and the pages living in its high byte are never loaded. A
            // 616-page floppy loses pages 608..615 exactly this way.
            int bitmapBytes = (int)(((_device.TotalBlocks + 7) / 8 + 1) & ~1u);
            int bitmapPages = (bitmapBytes + pageSize - 1) / pageSize;

            if (pointer.Type == BlockPointer.PointerType.Indexed)
            {
                // Rare INDEXED bit file: the first page is a chain of page pointers. Follow the
                // chain, reading each data page as a contiguous bitmap chunk. (Kept for
                // completeness; SINTRAN's on-disk bit file is contiguous.)
                LoadIndexedBitFile(pointer, bitmapBytes);
                return;
            }

            // CONTIGUOUS bit file (the normal case): read the exact page span linearly. Do NOT
            // interpret any leading bytes as a "next page pointer" — every byte is bitmap data.
            var pageBuffer = new byte[pageSize];
            for (int p = 0; p < bitmapPages; p++)
            {
                _device.ReadBlock(BlockAddress.FromLBA(pointer.BlockID + (uint)p), pageBuffer);

                for (int i = 0; i < pageSize; i++)
                {
                    int globalByte = p * pageSize + i;
                    if (globalByte >= bitmapBytes)
                        break;

                    byte bitByte = pageBuffer[i];
                    for (int bit = 0; bit < 8; bit++)
                    {
                        // Raw byte -> page number, inverting BitFile.BitPosition. Page N is
                        // at byte (N>>3)^1, so byte B carries pages ((B^1)*8) .. +7. Decoding
                        // this as B*8+bit and then storing through MarkBlockUsed - which
                        // applies the word convention - byte-swaps the entire map on mount.
                        uint pageNum = (uint)(((globalByte ^ 1) * 8) + bit);
                        if (pageNum >= _device.TotalBlocks)
                            break;

                        if ((bitByte & (1 << bit)) != 0)
                            _bitFile.MarkBlockUsed(pageNum);
                        else
                            _bitFile.MarkBlockFree(pageNum);
                    }
                }
            }
        }

        /// <summary>
        /// Loads a chained (INDEXED) bit file: the index page holds up to 512 pointers to the
        /// bitmap data pages, which together form the contiguous bit stream. Reads them in order
        /// and applies the same LSB-first, one-bit-per-page mapping as the contiguous path.
        /// </summary>
        private void LoadIndexedBitFile(BlockPointer pointer, int bitmapBytes)
        {
            if (_device == null)
                return;

            const int pageSize = NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;

            var indexBuffer = new byte[pageSize];
            _device.ReadBlock(BlockAddress.FromLBA(pointer.BlockID), indexBuffer);

            var dataBuffer = new byte[pageSize];
            int chunkIndex = 0; // which contiguous bitmap chunk (data page) this is

            for (int slot = 0; slot < pageSize / 4; slot++)
            {
                var dataPtr = BlockPointer.FromBytes(indexBuffer, slot * 4);
                if (dataPtr.BlockID == 0)
                    break;

                _device.ReadBlock(BlockAddress.FromLBA(dataPtr.BlockID), dataBuffer);

                for (int i = 0; i < pageSize; i++)
                {
                    int globalByte = chunkIndex * pageSize + i;
                    if (globalByte >= bitmapBytes)
                        return;

                    byte bitByte = dataBuffer[i];
                    for (int bit = 0; bit < 8; bit++)
                    {
                        // Raw byte -> page number, inverting BitFile.BitPosition. Page N is
                        // at byte (N>>3)^1, so byte B carries pages ((B^1)*8) .. +7. Decoding
                        // this as B*8+bit and then storing through MarkBlockUsed - which
                        // applies the word convention - byte-swaps the entire map on mount.
                        uint pageNum = (uint)(((globalByte ^ 1) * 8) + bit);
                        if (pageNum >= _device.TotalBlocks)
                            break;

                        if ((bitByte & (1 << bit)) != 0)
                            _bitFile.MarkBlockUsed(pageNum);
                        else
                            _bitFile.MarkBlockFree(pageNum);
                    }
                }

                chunkIndex++;
            }
        }

        private byte[] ReadObjectData(ObjectEntry obj)
        {
            if (_device == null)
                throw new InvalidOperationException("Device not initialized");

            if (obj.FilePointer == null || obj.FilePointer.BlockID == 0)
                throw new InvalidDataException("Invalid file pointer");

            var data = new byte[obj.BytesInFile];
            ulong bytesLeft = obj.BytesInFile;
            int offset = 0;

            // Get all block pointers based on pointer type
            var blockPointers = GetAllBlockPointersForFile(obj.FilePointer, obj.BytesInFile);

            for (int i = 0; i < blockPointers.Count; i++)
            {
                var pointer = blockPointers[i];
                var pageBuffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];

                if (pointer.BlockID == 0)
                {
                    // Empty page - fill with zeros
                    Array.Clear(pageBuffer, 0, pageBuffer.Length);
                }
                else
                {
                    _device.ReadBlock(BlockAddress.FromLBA(pointer.BlockID), pageBuffer);
                }

                int copyLength = (int)Math.Min((ulong)pageBuffer.Length, bytesLeft);
                Array.Copy(pageBuffer, 0, data, offset, copyLength);
                offset += copyLength;
                bytesLeft -= (ulong)copyLength;

                if (bytesLeft == 0)
                    break;
            }

            return data;
        }

        /// <summary>
        /// Full block-ID list for a file's diagnostic/integrity checks, including the top-level
        /// index/sub-index block itself (data blocks alone aren't enough to verify the whole
        /// on-disk footprint is bitmap-accounted-for). Replaces the old, now-removed
        /// <c>ObjectEntry.GetAllBlockPointersForFile</c> stub — that method can't actually
        /// walk index/sub-index blocks since <see cref="ObjectEntry"/> has no device access;
        /// this reuses the filesystem's own already-correct read-side walk instead
        /// (<see cref="GetAllBlockPointersForFile(BlockPointer, ulong)"/>), which single-page,
        /// Indexed, and SubIndexed files all already go through for real file reads.
        /// </summary>
        private List<uint> GetAllBlockIdsForObjectIncludingIndex(ObjectEntry obj)
        {
            var result = new List<uint>();
            if (obj.FilePointer == null || obj.FilePointer.BlockID == 0)
                return result;

            if (obj.FilePointer.Type == BlockPointer.PointerType.Indexed ||
                obj.FilePointer.Type == BlockPointer.PointerType.SubIndexed)
            {
                result.Add(obj.FilePointer.BlockID);
            }

            if (obj.FilePointer.Type == BlockPointer.PointerType.SubIndexed)
            {
                result.AddRange(GetSubIndexedGroupIndexBlockIds(obj.FilePointer));
            }

            var pointers = GetAllBlockPointersForFile(obj.FilePointer, obj.BytesInFile);
            for (int i = 0; i < pointers.Count; i++)
            {
                if (pointers[i].BlockID > 0)
                {
                    result.Add(pointers[i].BlockID);
                }
            }

            return result;
        }

        /// <summary>
        /// Reads a SubIndexed file's top-level sub-index block and returns the BlockID of each
        /// group's own Indexed index block — the structural blocks that
        /// <see cref="GetBlockPointersSubIndexed"/> deliberately excludes from its content-order
        /// return value (see the comment there) but which still need to be freed on delete/
        /// overwrite, same as the top-level pointer itself.
        /// </summary>
        private List<uint> GetSubIndexedGroupIndexBlockIds(BlockPointer subIndexPointer)
        {
            var result = new List<uint>();
            if (_device == null)
                return result;

            var subIndexBuffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            _device.ReadBlock(BlockAddress.FromLBA(subIndexPointer.BlockID), subIndexBuffer);

            const uint IndexPageLimit = 512;
            for (uint i = 0; i < IndexPageLimit; i++)
            {
                var groupPointer = BlockPointer.FromBytes(subIndexBuffer, (int)(i * 4));
                if (groupPointer.BlockID != 0)
                {
                    result.Add(groupPointer.BlockID);
                }
            }

            return result;
        }

        /// <summary>
        /// Frees the structural (non-data) blocks belonging to a file's pointer: the top-level
        /// index/sub-index block, plus — for SubIndexed files — every group's own index block.
        /// Complements <c>FreeFileBlocks(GetAllBlockPointersForFile(...))</c>, which frees only
        /// the real data blocks. Must be called by every place that frees a file's blocks
        /// (DeleteFile and UpdateExistingFile's before-overwrite free) — omitting it for
        /// UpdateExistingFile was itself a real, separate pre-existing leak (every overwrite of
        /// an existing file leaked its old top-level index block), fixed alongside this.
        /// </summary>
        private void FreeStructuralBlocks(BlockPointer? filePointer)
        {
            if (_device == null || filePointer == null || filePointer.BlockID == 0)
                return;

            if (filePointer.Type != BlockPointer.PointerType.Indexed &&
                filePointer.Type != BlockPointer.PointerType.SubIndexed)
                return;

            if (filePointer.Type == BlockPointer.PointerType.SubIndexed)
            {
                var groupIds = GetSubIndexedGroupIndexBlockIds(filePointer);
                for (int i = 0; i < groupIds.Count; i++)
                {
                    _bitFile.FreeBlocks(groupIds[i], 1);
                }
            }

            _bitFile.FreeBlocks(filePointer.BlockID, 1);
        }

        private List<BlockPointer> GetAllBlockPointersForFile(BlockPointer filePointer, ulong bytesInFile)
        {
            if (_device == null)
                throw new InvalidOperationException("Device not initialized");

            uint filePages = (uint)((bytesInFile + 2047) / 2048);

            switch (filePointer.Type)
            {
                case BlockPointer.PointerType.Contiguous:
                    return GetBlockPointersContiguous(filePointer, filePages);

                case BlockPointer.PointerType.Indexed:
                    return GetBlockPointersIndexed(filePointer, ref filePages);

                case BlockPointer.PointerType.SubIndexed:
                    return GetBlockPointersSubIndexed(filePointer, ref filePages);

                default:
                    throw new InvalidDataException($"Invalid block pointer type: {filePointer.Type}");
            }
        }

        private List<BlockPointer> GetBlockPointersContiguous(BlockPointer bp, uint filePages)
        {
            var blockList = new List<BlockPointer>();
            uint blockID = bp.BlockID;

            for (int i = 0; i < filePages; i++)
            {
                blockList.Add(new BlockPointer(blockID, BlockPointer.PointerType.Contiguous));
                blockID++;
            }

            return blockList;
        }

        private List<BlockPointer> GetBlockPointersIndexed(BlockPointer bp, ref uint filePages)
        {
            if (_device == null)
                throw new InvalidOperationException("Device not initialized");

            var blockList = new List<BlockPointer>();

            // Limit to 512 pointers per index block
            uint pagesToRead = Math.Min(filePages, 512);

            // Read the index block
            var indexBuffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            _device.ReadBlock(BlockAddress.FromLBA(bp.BlockID), indexBuffer);

            for (uint i = 0; i < pagesToRead; i++)
            {
                var blockPointer = BlockPointer.FromBytes(indexBuffer, (int)(i * 4));
                blockList.Add(blockPointer);
                filePages--;
            }

            return blockList;
        }

        private List<BlockPointer> GetBlockPointersSubIndexed(BlockPointer bp, ref uint filePages)
        {
            if (_device == null)
                throw new InvalidOperationException("Device not initialized");

            var blockList = new List<BlockPointer>();

            // Read the sub-index block
            var subIndexBuffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            _device.ReadBlock(BlockAddress.FromLBA(bp.BlockID), subIndexBuffer);

            const uint IndexPageLimit = 512;

            for (uint i = 0; i < IndexPageLimit && filePages > 0; i++)
            {
                var indexPointer = BlockPointer.FromBytes(subIndexBuffer, (int)(i * 4));

                if (indexPointer.BlockID == 0)
                {
                    // An empty entry is a hole of 512 pages, not something to skip: entry i covers
                    // file pages i * 512 on whether it is set or not. SINTRAN III leaves the entry of
                    // a group with no page in it empty (a domain of the ND Linker B01, 2026-10-14:
                    // entries 0, 2, 4, 6 set). Skipping it moved every page behind it 512 pages
                    // (1 MB) down. BlockID 0 is a sparse hole to every reader of this list.
                    uint holes = Math.Min(filePages, IndexPageLimit);
                    for (uint h = 0; h < holes; h++)
                        blockList.Add(new BlockPointer(0, BlockPointer.PointerType.Contiguous));
                    filePages -= holes;
                    continue;
                }

                if (indexPointer.Type == BlockPointer.PointerType.Indexed)
                {
                    var indexBlocks = GetBlockPointersIndexed(indexPointer, ref filePages);
                    blockList.AddRange(indexBlocks);

                    // Deliberately NOT adding indexPointer itself here (a real, pre-existing bug
                    // fixed alongside SubIndexed write support): ReadObjectData treats this whole
                    // list as sequential file content, so splicing a structural index-block
                    // pointer in between groups corrupted every file spanning more than 512 pages
                    // at exactly each 512-page group boundary. Group index blocks are freed
                    // separately via FreeStructuralBlocks/GetAllBlockIdsForObjectIncludingIndex.
                }
                else
                {
                    throw new InvalidDataException($"Invalid pointer type in sub-index: {indexPointer.Type}");
                }

                if (filePages == 0)
                    break;
            }

            return blockList;
        }

        private static (string userName, string fileName) ParseNdfsPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return (string.Empty, string.Empty);

            // Remove leading slash if present
            if (path.StartsWith("/"))
                path = path.Substring(1);

            // Remove trailing slash if present
            if (path.EndsWith("/"))
                path = path.Substring(0, path.Length - 1);

            // NDFS path format: [user/]filename
            var parts = path.Split('/', 2);

            if (parts.Length == 2)
            {
                return (parts[0], parts[1]);
            }
            else
            {
                return (string.Empty, parts[0]);
            }
        }

        private void EnsureInitialized()
        {
            if (!_initialized)
                throw new InvalidOperationException("Filesystem not initialized");
        }

        private static (string objectName, string fileType) ParseFileName(string fileName)
        {
            // NDFS uses colon as separator (FILENAME:TYPE), but also support period for compatibility
            var lastColon = fileName.LastIndexOf(':');
            if (lastColon > 0 && lastColon < fileName.Length - 1)
            {
                var name = fileName.Substring(0, lastColon);
                var type = fileName.Substring(lastColon + 1);
                return (name.Length > 16 ? name.Substring(0, 16) : name,
                       type.Length > 4 ? type.Substring(0, 4) : type);
            }

            // Fallback to period for backward compatibility
            var lastDot = fileName.LastIndexOf('.');
            if (lastDot > 0 && lastDot < fileName.Length - 1)
            {
                var name = fileName.Substring(0, lastDot);
                var type = fileName.Substring(lastDot + 1);
                return (name.Length > 16 ? name.Substring(0, 16) : name,
                       type.Length > 4 ? type.Substring(0, 4) : type);
            }

            return (fileName.Length > 16 ? fileName.Substring(0, 16) : fileName, string.Empty);
        }

        /// <summary>
        /// Normalizes a filename for lookup by converting period separator to colon.
        /// NDFS internally uses colon as extension separator, but accepts period for compatibility.
        /// </summary>
        private string NormalizeFileNameForLookup(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return fileName;

            // If already has colon, no normalization needed
            if (fileName.IndexOf(':') >= 0)
                return fileName;

            // Convert last period to colon for NDFS internal lookup
            var lastDot = fileName.LastIndexOf('.');
            if (lastDot > 0 && lastDot < fileName.Length - 1)
            {
                return fileName.Substring(0, lastDot) + ExtensionSeparator + fileName.Substring(lastDot + 1);
            }

            return fileName;
        }

        private void CreateNewFile(string objectName, string fileType, UserEntry user, byte[] data)
        {
            if (_device == null || _masterBlock == null)
                throw new InvalidOperationException("Device not initialized");

            uint filePages = (uint)((data.Length + NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE - 1) /
                                   NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE);

            if (filePages == 0)
                filePages = 1; // Minimum 1 page

            // Determine pointer type based on file size
            BlockPointer.PointerType pointerType;
            if (filePages <= 512)
                pointerType = BlockPointer.PointerType.Indexed; // Up to 512 pages
            else if (filePages <= 262144)
                pointerType = BlockPointer.PointerType.SubIndexed; // Up to 512*512 pages
            else
                throw new InvalidOperationException($"File too large: {data.Length} bytes ({filePages} pages)");

            // Allocate blocks for data with sparse support
            var allocatedBlocks = AllocateFileBlocksSparse(data, pointerType);
            if (allocatedBlocks == null || allocatedBlocks.Count == 0)
                throw new InvalidOperationException("Failed to allocate blocks for file");

            // Write file data to allocated blocks (skip sparse holes). SubIndexed files write
            // their data pages inline during allocation (see AllocateFileBlocksSparse) since a
            // single flat "skip the first element" pass can't express a file split across
            // multiple per-group index blocks the way WriteFileDataSparse assumes.
            if (pointerType != BlockPointer.PointerType.SubIndexed)
            {
                WriteFileDataSparse(allocatedBlocks, data);
            }

            // Choose the object slot inside the OWNING USER's region (SINTRAN
            // partitions the object file: user U owns slots U*256..U*256+255;
            // the object-index high byte is the owner). A flat global slot would
            // place the file in the wrong user's region.
            int objIndex = AllocateUserObjectSlot(user);
            // Make sure that user's directory page is allocated and linked.
            EnsureObjectDirPage(objIndex);

            // Create ObjectEntry
            uint createdNow = NdDateTime.ToNdDateOrUnset(DateTime.Now);
            var newEntry = new ObjectEntry
            {
                // Mark the slot in use. ToBytes writes Header verbatim now, so leaving this unset
                // would write a zero header and SINTRAN would read the slot as free.
                Header = ObjectEntry.HeaderEntryUsed,
                ObjectName = objectName.ToUpper(),
                Type = fileType.ToUpper(),
                UserName = user.UserName,
                UserIndex = user.UserIndex,
                ObjectIndex = objIndex,
                // The user-visible number is not the physical position once a user owns more than
                // one object block, so compute it here too - otherwise a freshly created entry
                // reads 0 until the pack is reloaded.
                FileNumber = ObjectEntry.ComputeFileNumber(objIndex, user.UserIndex),
                // Object index word = [user|fileEntry]; a single-version file
                // points its next/prev version at itself (else SINTRAN ";2").
                ObjectEntryNextVersion = (uint)objIndex,
                ObjectEntryPrevVersion = (uint)objIndex,
                AccessBits = 0x03FF,                 // owner+friend full, public none
                FileTypeFlags = FileTypes.IndexedFile,
                BytesInFile = (ulong)data.Length,

                // PagesInFile counts the pages ACTUALLY ALLOCATED, not the logical extent.
                //
                // Corrected 2026-07-30 to match SINTRAN. On a real pack
                // (SYSTEM)S3-CONFIG-E01:PROG records 89 while its index spans 99 slots with 10
                // holes, and the machine itself reports "No of pages in file: 89" while
                // transferring page numbers 0..53 then 64..98. Writing the logical extent here
                // charged the user quota for holes that occupy no disk, and produced packs that
                // disagreed with SINTRAN-written ones.
                //
                // The logical extent is not lost: it is ceil(BytesInFile / 2048), which is what
                // GetAllBlockPointersForFile already uses to walk the index.
                PagesInFile = CountRealDataPages(data),
                FilePointer = allocatedBlocks[0], // First pointer (index or data block)
                FileType = 0, // DATA file
                DateCreated = createdNow,
                LastDateOpenedForWrite = createdNow
            };

            // Add to object file
            _objectFile.AddObject(newEntry);

            // Update user's pages used. Quota tracks only REAL disk consumption: sparse holes
            // allocate no block (see IsPageAllZeros) and the index/sub-index structural blocks
            // are filesystem overhead, not charged against the user — see CountRealDataPages.
            user.PagesUsed += CountRealDataPages(data);

            // Write just the affected pages
            if (_masterBlock.UserFilePointer != null)
                _userFile.WritePageForEntry(_device, _masterBlock.UserFilePointer, user.UserIndex);

            if (_masterBlock.ObjectFilePointer != null)
                _objectFile.WritePageForEntry(_device, _masterBlock.ObjectFilePointer, newEntry.ObjectIndex);

            if (_masterBlock.BitFilePointer != null)
                _bitFile.WriteToDisk(_device, _masterBlock.BitFilePointer);

            // File creation allocates pages from the bitmap — refresh the cached
            // "Unreserved Pages" count so SINTRAN's free-space report stays accurate.
            PersistMasterBlock();
        }

        /// <summary>
        /// Picks a free object slot for a user, growing into a new object block when the allocated
        /// ones are full.
        /// </summary>
        /// <param name="user">
        /// The owning user. Its <see cref="UserEntry.AllocatedObjectBlocks"/> is raised when the
        /// slot falls in a block beyond the current count.
        /// </param>
        /// <returns>
        /// The physical object-file slot to place the new entry in.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown only when the user is genuinely at their ceiling - every one of their
        /// <see cref="UserEntry.MaxObjectBlocks"/> blocks is full.
        /// </exception>
        /// <remarks>
        /// A user area holds 256 files per object block, one by default and up to 16 (4096 files)
        /// once <c>@GIVE-OBJECT-BLOCKS</c> has been run on the machine. Blocks are allocated on
        /// demand, which is what makes creating file 257 work at all - previously that threw
        /// "object table is full" because only the first block was ever searched.
        /// </remarks>
        private int AllocateUserObjectSlot(UserEntry user)
        {
            int slot = _objectFile.FindFreeUserSlot(
                user.UserIndex,
                user.MaxObjectBlocks,
                user.AllocatedObjectBlocks);

            if (slot < 0)
            {
                // Two very different situations reach here, and a caller copying a folder in
                // needs to tell them apart: below 16 blocks the operator can grant more, at 16
                // nothing can help. ObjectBlockLimitException carries that as a flag so a UI
                // does not have to match on message text. It still derives from
                // InvalidOperationException, which is what this used to throw.
                throw new ObjectBlockLimitException(
                    user.UserName,
                    user.MaxObjectBlocks,
                    user.AllocatedObjectBlocks,
                    user.MaxObjectBlocks >= NdfsObjectBlocks.MaxObjectBlocks);
            }

            // Record growth into a new block. Byte 47's low nibble is how a later directory walk
            // knows how many blocks to search, so this must be persisted with the user entry.
            const int entriesPerPage = NdfsObjectBlocks.EntriesPerPage;
            const int pagesPerIndexBlock = NdfsObjectBlocks.PagesPerIndexBlock;
            const int usersPerIndexBlock = NdfsObjectBlocks.UsersPerIndexBlock;   // 64

            int block = (slot / entriesPerPage) / pagesPerIndexBlock;
            if (block + 1 > user.AllocatedObjectBlocks)
            {
                // Growing into block N puts this user's entries in index-block group N, at the
                // same 8-page offset this library uses for user (N*64 + U)'s FIRST block. Both
                // placements are real - the multi-user one is how we reach 256 users, the
                // multi-block one was measured on a live SINTRAN K pack - and they collide. Refuse
                // rather than write over another user's directory.
                int collidingUser = (block * usersPerIndexBlock) + user.UserIndex;
                if (_userFile.GetUser(collidingUser) != null)
                {
                    throw new ObjectBlockCollisionException(user.UserName, block, collidingUser);
                }

                user.AllocatedObjectBlocks = block + 1;
            }

            return slot;
        }

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
        /// <exception cref="ArgumentException">
        /// Thrown when no user matches <paramref name="userNameOrIndex"/>.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="count"/> is below 1, or when the grant would push the
        /// maximum past the SINTRAN limit of 16 blocks. The message reports how many blocks are
        /// still grantable so the caller can retry with a figure that fits.
        /// </exception>
        /// <remarks>
        /// <para>The <c>@GIVE-OBJECT-BLOCKS</c> equivalent</para>
        /// This mirrors the SINTRAN operator command
        /// <c>@GIVE-OBJECT-BLOCKS (directory:)user,count</c>, which likewise
        /// ADDS to the maximum rather than setting it. Each block is 256 files, so granting 3
        /// blocks to a default user takes the ceiling from 256 to 1024 — exactly what
        /// <c>@USER-STATISTICS</c> then reports as MAXIMUM NUMBER OF FILES. VERIFIED on a live
        /// SINTRAN III K pack: user BIGMAN went from 256 to 1024 after
        /// <c>@GIVE-OBJECT-BLOCKS BIGMAN,3</c>, and user-entry byte 47 read back <c>0x31</c>.
        /// <para><b>What this deliberately does not do</b></para>
        ///  - It does not allocate the blocks. Only the maximum moves; SINTRAN allocates a block
        ///    the first time a file needs it, and so does this library. A user granted 4 blocks
        ///    who holds 300 files still has <c>AllocatedObjectBlocks</c> of 2.
        ///  - It does not reserve disk pages. Object blocks limit the number of FILES, not their
        ///    size; page quota is separate (<c>@GIVE-USER-SPACE</c>).
        ///  - It does not convert an Indexed object file to SubIndexed. A second object block
        ///    lives at page 512 and up, which an Indexed object file cannot address at all, so
        ///    growth into it fails later — when a file actually needs the block. See
        ///    <c>docs/NDFS-Object-Blocks.md</c> section 4.
        /// </remarks>
        public int GiveObjectBlocks(string userNameOrIndex, int count = 1)
        {
            // A grant on a read-only mount would change only the in-memory view and
            // silently vanish, so refuse it outright - the other ports do the same.
            if (_device == null || _device.IsReadOnly)
                throw new InvalidOperationException("Filesystem is read-only");

            if (count < 1)
                throw new ArgumentOutOfRangeException(nameof(count), count, "count must be 1 or more");

            UserEntry user = RequireUser(userNameOrIndex);
            int newMax = user.MaxObjectBlocks + count;
            if (newMax > NdfsObjectBlocks.MaxObjectBlocks)
            {
                int grantable = NdfsObjectBlocks.MaxObjectBlocks - user.MaxObjectBlocks;
                throw new ArgumentOutOfRangeException(
                    nameof(count),
                    count,
                    $"Cannot give user {user.UserName} {count} more object block(s): they have "
                    + $"{user.MaxObjectBlocks} and SINTRAN allows at most "
                    + $"{NdfsObjectBlocks.MaxObjectBlocks} "
                    + $"({NdfsObjectBlocks.MaxObjectBlocks * NdfsObjectBlocks.FilesPerObjectBlock} "
                    + $"files). At most {grantable} more can be granted.");
            }

            user.MaxObjectBlocks = newMax;
            PersistUserEntry(user.UserIndex);
            return newMax;
        }

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
        /// <exception cref="ArgumentException">
        /// Thrown when no user matches <paramref name="userNameOrIndex"/>.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="count"/> is below 1, or when the reduction would drop the
        /// maximum below the number of blocks actually allocated.
        /// </exception>
        /// <remarks>
        /// This has no SINTRAN equivalent. No command to remove object blocks is documented
        /// in the manuals, nor was one found while carving the kernel — which is consistent with
        /// the structure, since file numbers are stable identifiers derived from the block a file
        /// sits in, so dropping a block would orphan every file in it. This exists only so a tool
        /// can undo its own grant on an image it is building, and it refuses anything that would
        /// strand a file: the maximum can never fall below the allocated count, nor below 1.
        /// </remarks>
        public int TakeObjectBlocks(string userNameOrIndex, int count = 1)
        {
            // A grant on a read-only mount would change only the in-memory view and
            // silently vanish, so refuse it outright - the other ports do the same.
            if (_device == null || _device.IsReadOnly)
                throw new InvalidOperationException("Filesystem is read-only");

            if (count < 1)
                throw new ArgumentOutOfRangeException(nameof(count), count, "count must be 1 or more");

            UserEntry user = RequireUser(userNameOrIndex);
            int newMax = user.MaxObjectBlocks - count;
            if (newMax < user.AllocatedObjectBlocks)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(count),
                    count,
                    $"Cannot take {count} object block(s) from user {user.UserName}: "
                    + $"{user.AllocatedObjectBlocks} of {user.MaxObjectBlocks} are allocated and "
                    + "hold files. Lowering the maximum below the allocated count would orphan them.");
            }

            if (newMax < 1)
                throw new ArgumentOutOfRangeException(nameof(count), count, "A user must keep at least one object block");

            user.MaxObjectBlocks = newMax;
            PersistUserEntry(user.UserIndex);
            return newMax;
        }

        /// <summary>
        /// Reports a user's object-block position: what they hold and what they may hold.
        /// </summary>
        /// <param name="userNameOrIndex">
        /// User name (case-insensitive) or user index.
        /// </param>
        /// <returns>
        /// The user's block counts, file counts and whether more blocks can be granted.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when no user matches <paramref name="userNameOrIndex"/>.
        /// </exception>
        /// <remarks>
        /// This is the query a user interface needs in order to answer "why can I not add another
        /// file". Blocks are allocated on demand, so
        /// <see cref="ObjectBlockInfo.AllocatedObjectBlocks"/> climbs by itself as files are
        /// created; only <see cref="ObjectBlockInfo.MaxObjectBlocks"/> needs an operator.
        /// </remarks>
        public ObjectBlockInfo GetObjectBlockInfo(string userNameOrIndex)
        {
            UserEntry user = RequireUser(userNameOrIndex);

            // Count what the user actually holds. Deliberately a live count rather than a stored
            // figure: SINTRAN never compacts, so deletions leave holes and any cached total drifts.
            List<ObjectEntry> owned = _objectFile.GetUserObjects(user.UserIndex);
            int filesInUse = owned.Count;

            return new ObjectBlockInfo(
                user.UserName,
                user.UserIndex,
                user.AllocatedObjectBlocks,
                user.MaxObjectBlocks,
                filesInUse);
        }

        /// <summary>
        /// Writes the user-file page holding <paramref name="userIndex"/> back to the device.
        /// </summary>
        /// <param name="userIndex">
        /// The user whose entry changed.
        /// </param>
        /// <remarks>
        /// Does nothing when the file system has no backing device or no user-file pointer, which
        /// is the same behaviour as the other in-place user updates in this class: the change
        /// still stands in memory and is picked up by a later full serialisation.
        /// </remarks>
        private void PersistUserEntry(int userIndex)
        {
            if (_device == null || _masterBlock == null || _masterBlock.UserFilePointer == null)
                return;

            _userFile.WritePageForEntry(_device, _masterBlock.UserFilePointer, userIndex);
        }

        /// <summary>
        /// Looks a user up by name or index, throwing when absent.
        /// </summary>
        /// <param name="userNameOrIndex">
        /// User name (case-insensitive), or a decimal user index.
        /// </param>
        /// <returns>
        /// The matching user entry.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when no user matches.
        /// </exception>
        private UserEntry RequireUser(string userNameOrIndex)
        {
            UserEntry? user = null;

            // A pure-digit reference is a user index. Names are never all digits on SINTRAN,
            // so this is unambiguous.
            int index;
            if (int.TryParse(userNameOrIndex, out index) && index >= 0 && index <= 255)
                user = _userFile.GetUser(index);
            else if (!string.IsNullOrEmpty(userNameOrIndex))
                user = _userFile.FindUser(userNameOrIndex);

            if (user == null)
                throw new ArgumentException($"User not found: {userNameOrIndex}", nameof(userNameOrIndex));

            return user;
        }

        /// <summary>
        /// Ensure the object-file directory data page that holds
        /// <paramref name="objectIndex"/> exists, allocating and linking it on
        /// demand (SINTRAN grows a user's region as needed). Page index in the
        /// object-file index block is objectIndex/32; for user U that maps to
        /// index-pointer slots U*8..U*8+7.
        /// </summary>
        /// <remarks>
        /// A plain Indexed object file caps out at 512 directory pages (16,384 files across all
        /// users combined — 64 users' worth at 8 reserved pages each). Beyond that, the object
        /// file must grow into a SubIndexed structure (sub-index block -> up to 512 group index
        /// blocks -> up to 512 directory pages each), exactly like a single file's own data does
        /// past 512 pages (see AllocateFileBlocksSparse). The one-time conversion reuses the
        /// existing single Indexed block as group 0 — no directory data is copied or moved, only
        /// a new level of indirection is added above it.
        /// </remarks>
        private void EnsureObjectDirPage(int objectIndex)
        {
            if (_device == null || _masterBlock?.ObjectFilePointer == null)
                return;

            int pageIdx = objectIndex / 32;
            const int GroupCapacity = 512;
            if (pageIdx >= GroupCapacity * GroupCapacity)
                throw new InvalidOperationException("Object index exceeds maximum sub-indexed object-file capacity");

            var idxPtr = _masterBlock.ObjectFilePointer;

            if (idxPtr.Type == BlockPointer.PointerType.Indexed && pageIdx < GroupCapacity)
            {
                // Fits in the existing single-level index — original behavior, unchanged.
                EnsureObjectDirPageInGroup(idxPtr.BlockID, pageIdx);
                return;
            }

            if (idxPtr.Type == BlockPointer.PointerType.Indexed)
            {
                // One-time conversion: wrap the existing Indexed block as group 0 of a new
                // SubIndexed structure. No data migration — the old block's directory-page
                // pointers for pageIdx 0-511 stay exactly where they are.
                uint oldIndexBlockId = idxPtr.BlockID;

                var subIndexBlock = _bitFile.FindFirstFreeBlock();
                if (subIndexBlock == null)
                    throw new InvalidOperationException("No free block for object-file sub-index conversion");
                _bitFile.AllocateBlocks(subIndexBlock.Value, 1);

                var subIndexBuffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
                var group0Pointer = new BlockPointer(oldIndexBlockId, BlockPointer.PointerType.Indexed);
                WriteBlockPointer(subIndexBuffer, 0, group0Pointer.Native);
                _device.WriteBlock(BlockAddress.FromLBA(subIndexBlock.Value), subIndexBuffer);

                var newObjectFilePointer = new BlockPointer(subIndexBlock.Value, BlockPointer.PointerType.SubIndexed);
                _masterBlock.ObjectFilePointer = newObjectFilePointer;
                PersistMasterBlock();

                idxPtr = newObjectFilePointer;
            }

            // SubIndexed case (either already was, or was just converted above).
            int groupIdx = pageIdx / GroupCapacity;
            int innerIdx = pageIdx % GroupCapacity;

            var subIndexPage = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            _device.ReadBlock(BlockAddress.FromLBA(idxPtr.BlockID), subIndexPage);
            var groupPointer = BlockPointer.FromBytes(subIndexPage, groupIdx * 4);

            if (groupPointer.BlockID == 0)
            {
                // Allocate a new group index block and link it into the sub-index block.
                var newGroupBlock = _bitFile.FindFirstFreeBlock();
                if (newGroupBlock == null)
                    throw new InvalidOperationException("No free block for object-file group index page");
                _bitFile.AllocateBlocks(newGroupBlock.Value, 1);
                _device.WriteBlock(BlockAddress.FromLBA(newGroupBlock.Value), new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE]);

                groupPointer = new BlockPointer(newGroupBlock.Value, BlockPointer.PointerType.Indexed);
                WriteBlockPointer(subIndexPage, groupIdx * 4, groupPointer.Native);
                _device.WriteBlock(BlockAddress.FromLBA(idxPtr.BlockID), subIndexPage);
            }

            EnsureObjectDirPageInGroup(groupPointer.BlockID, innerIdx);
        }

        /// <summary>
        /// Ensures the directory data page at <paramref name="pageIdx"/> within a single Indexed
        /// block (either the object file's top-level block, or one SubIndexed group's own index
        /// block) exists, allocating and linking it on demand.
        /// </summary>
        private void EnsureObjectDirPageInGroup(uint indexBlockId, int pageIdx)
        {
            const int PAGE_SIZE = NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;

            var indexBuffer = new byte[PAGE_SIZE];
            _device!.ReadBlock(BlockAddress.FromLBA(indexBlockId), indexBuffer);
            var existing = BlockPointer.FromBytes(indexBuffer, pageIdx * 4);
            if (existing.BlockID != 0)
                return; // already allocated and linked

            var blk = _bitFile.FindFirstFreeBlock();
            if (blk == null)
                throw new InvalidOperationException("No free block for object directory page");
            _bitFile.AllocateBlocks(blk.Value, 1);
            _device.WriteBlock(BlockAddress.FromLBA(blk.Value), new byte[PAGE_SIZE]);

            var newPtr = new BlockPointer(blk.Value, BlockPointer.PointerType.Contiguous);
            WriteBlockPointer(indexBuffer, pageIdx * 4, newPtr.Native);
            _device.WriteBlock(BlockAddress.FromLBA(indexBlockId), indexBuffer);
        }

        /// <summary>
        /// Resolves the user a write is charged to.
        /// </summary>
        /// <param name="userName">
        /// The user named in the path, or an empty string when the path carried no user prefix.
        /// </param>
        /// <returns>
        /// The matching user entry, or the first user on the pack when no user was named.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <paramref name="userName"/> names a user that does not exist, or when the
        /// pack has no users at all.
        /// </exception>
        private UserEntry ResolveUserForWrite(string userName)
        {
            // NO LINQ - explicit scan.
            IReadOnlyList<UserEntry> users = _userFile.GetUsers();

            if (!string.IsNullOrEmpty(userName))
            {
                for (int i = 0; i < users.Count; i++)
                {
                    if (string.Equals(users[i].UserName, userName, StringComparison.OrdinalIgnoreCase))
                    {
                        return users[i];
                    }
                }

                throw new InvalidOperationException($"User '{userName}' not found");
            }

            if (users.Count > 0)
            {
                return users[0];
            }

            throw new InvalidOperationException("No users available");
        }

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
        /// This is what SINTRAN's <c>@CREATE-FILE name pages</c> builds, and what the COSMOS
        /// file-access server's <c>Create-file</c> operation asks for when it carries a page count.
        /// The file is created empty - <c>BytesInFile</c> is zero - but all of its pages are
        /// allocated up front and are guaranteed to be consecutive on the pack.
        /// </para>
        /// <para>
        /// The reservation is fixed for the life of the file. A later write of more than
        /// <paramref name="pages"/> pages fails rather than converting the file to an indexed one,
        /// because the pages following the run belong to other files and cannot be claimed.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="path"/> carries no file name.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="pages"/> is zero.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the device is read-only, the file already exists, the user's object table is
        /// full, or no free run of <paramref name="pages"/> consecutive pages exists on the pack.
        /// </exception>
        public void CreateContiguousFile(string path, uint pages)
        {
            EnsureInitialized();

            if (_device == null || _masterBlock == null)
                throw new InvalidOperationException("Device not initialized");

            if (_device.IsReadOnly)
                throw new InvalidOperationException("Cannot write to read-only device");

            if (pages == 0)
                throw new ArgumentOutOfRangeException(nameof(pages), "A contiguous file needs at least one page");

            (string userName, string fileName) = ParseNdfsPath(path);

            if (string.IsNullOrEmpty(fileName))
                throw new ArgumentException("Invalid path: filename required", nameof(path));

            UserEntry user = ResolveUserForWrite(userName);
            (string objectName, string fileType) = ParseFileName(fileName);

            if (_objectFile.FindObject(objectName, user.UserName) != null)
                throw new InvalidOperationException($"File already exists: {path}");

            // Every page of a contiguous file is real disk, so the whole run is charged to the
            // user's quota at once - there are no holes to discount, unlike an indexed file.
            uint pagesAvailableToUser = user.PagesReserved - user.PagesUsed;
            if (pagesAvailableToUser < pages)
            {
                uint expansionNeeded = pages - pagesAvailableToUser;
                if (_bitFile.GetFreePages() < expansionNeeded)
                    throw new InvalidOperationException(
                        $"Insufficient disk space. Need {expansionNeeded} more pages for the quota.");

                user.PagesReserved += expansionNeeded;
            }

            // The defining property: one run, no index block. FindFreeBlockRange is the only
            // allocator that can promise consecutive blocks - the sparse allocator picks each
            // page independently and would scatter them.
            uint? startBlock = _bitFile.FindFreeBlockRange(pages);
            if (startBlock == null)
                throw new InvalidOperationException(
                    $"No contiguous run of {pages} free pages is available on this pack");

            _bitFile.AllocateBlocks(startBlock.Value, pages);

            // Zero the run. A freshly created file must not expose whatever a deleted file left
            // behind on those pages.
            byte[] emptyPage = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            for (uint i = 0; i < pages; i++)
            {
                _device.WriteBlock(BlockAddress.FromLBA(startBlock.Value + i), emptyPage);
            }

            int objIndex;
            try
            {
                objIndex = AllocateUserObjectSlot(user);
            }
            catch (InvalidOperationException)
            {
                // Give the run back rather than leaking it - the entry is never created.
                _bitFile.FreeBlocks(startBlock.Value, pages);
                throw;
            }

            EnsureObjectDirPage(objIndex);

            uint createdNow = NdDateTime.ToNdDateOrUnset(DateTime.Now);
            ObjectEntry newEntry = new ObjectEntry
            {
                Header = ObjectEntry.HeaderEntryUsed,
                ObjectName = objectName.ToUpper(),
                Type = fileType.ToUpper(),
                UserName = user.UserName,
                UserIndex = user.UserIndex,
                ObjectIndex = objIndex,
                // The user-visible number is not the physical position once a user owns more than
                // one object block, so compute it here too - otherwise a freshly created entry
                // reads 0 until the pack is reloaded.
                FileNumber = ObjectEntry.ComputeFileNumber(objIndex, user.UserIndex),
                ObjectEntryNextVersion = (uint)objIndex,
                ObjectEntryPrevVersion = (uint)objIndex,
                AccessBits = 0x03FF,                 // owner+friend full, public none

                // Contiguous, not indexed - this is the flag SINTRAN prints as "CONTINUOUS FILE".
                FileTypeFlags = FileTypes.ContiguousFile,

                // Created empty: the pages exist, but no byte of them is file content yet. This is
                // what the live machine reports for a freshly created contiguous file.
                BytesInFile = 0,

                // Unlike an indexed file, allocated and logical extent are the same here, and both
                // equal the reservation - there are no holes in a contiguous run.
                PagesInFile = pages,

                // Points straight at the first data page. There is no index block to walk: page N
                // of the file is simply block (FilePointer.BlockID + N).
                FilePointer = new BlockPointer(startBlock.Value, BlockPointer.PointerType.Contiguous),
                FileType = 0, // DATA file
                DateCreated = createdNow,
                LastDateOpenedForWrite = createdNow
            };

            _objectFile.AddObject(newEntry);

            user.PagesUsed += pages;

            if (_masterBlock.UserFilePointer != null)
                _userFile.WritePageForEntry(_device, _masterBlock.UserFilePointer, user.UserIndex);

            if (_masterBlock.ObjectFilePointer != null)
                _objectFile.WritePageForEntry(_device, _masterBlock.ObjectFilePointer, newEntry.ObjectIndex);

            if (_masterBlock.BitFilePointer != null)
                _bitFile.WriteToDisk(_device, _masterBlock.BitFilePointer);

            PersistMasterBlock();
        }

        /// <summary>
        /// Writes into an existing contiguous file, in place and within its original reservation.
        /// </summary>
        /// <param name="existingEntry">
        /// The object entry of the contiguous file being written.
        /// </param>
        /// <param name="data">
        /// The bytes to write. Must fit inside the reserved run.
        /// </param>
        /// <remarks>
        /// The run is never reallocated, so unlike the indexed path this neither frees nor claims
        /// blocks, and the user's page count does not move: the pages were charged at creation and
        /// stay charged until the file is deleted.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <paramref name="data"/> needs more pages than the file reserved.
        /// </exception>
        private void UpdateExistingContiguousFile(ObjectEntry existingEntry, byte[] data)
        {
            if (_device == null || _masterBlock == null)
                throw new InvalidOperationException("Device not initialized");

            uint reservedPages = existingEntry.PagesInFile;
            uint neededPages = (uint)((data.Length + NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE - 1) /
                                      NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE);

            if (neededPages > reservedPages)
            {
                throw new InvalidOperationException(
                    $"Cannot write {data.Length} bytes ({neededPages} pages) to contiguous file " +
                    $"'{existingEntry.ObjectName}': it reserved {reservedPages} pages and a " +
                    "contiguous file cannot grow.");
            }

            if (existingEntry.FilePointer == null || existingEntry.FilePointer.BlockID == 0)
                throw new InvalidOperationException(
                    $"Contiguous file '{existingEntry.ObjectName}' has no allocated run");

            uint startBlock = existingEntry.FilePointer.BlockID;

            // Write the run straight through. Every page of the reservation is rewritten, not just
            // the pages the data covers, so bytes left over from a previous longer write cannot
            // reappear past the new end of file.
            byte[] page = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            for (uint i = 0; i < reservedPages; i++)
            {
                Array.Clear(page, 0, page.Length);

                long offset = (long)i * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
                if (offset < data.Length)
                {
                    int count = (int)Math.Min(NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE, data.Length - offset);
                    Array.Copy(data, offset, page, 0, count);
                }

                _device.WriteBlock(BlockAddress.FromLBA(startBlock + i), page);
            }

            existingEntry.BytesInFile = (ulong)data.Length;

            // PagesInFile stays at the reservation. For a contiguous file the pages are allocated
            // whether or not the data reaches them, so counting only the written pages would
            // under-report the disk this file actually holds.
            existingEntry.LastDateOpenedForWrite = NdDateTime.ToNdDateOrUnset(DateTime.Now);

            if (_masterBlock.ObjectFilePointer != null)
                _objectFile.WritePageForEntry(_device, _masterBlock.ObjectFilePointer, existingEntry.ObjectIndex);
        }

        private void UpdateExistingFile(ObjectEntry existingEntry, byte[] data)
        {
            if (_device == null || _masterBlock == null)
                throw new InvalidOperationException("Device not initialized");

            // A contiguous file keeps the run it was created with. It is never converted to an
            // indexed file behind the caller's back, and it never grows: SINTRAN cannot grow a
            // CONTINUOUS file either, because the pages after the run belong to someone else.
            if (existingEntry.FileTypeFlags.IsContiguous())
            {
                UpdateExistingContiguousFile(existingEntry, data);
                return;
            }

            // Free existing blocks
            UserEntry? user = _userFile.GetUser(existingEntry.UserIndex);
            if (existingEntry.FilePointer != null && existingEntry.FilePointer.BlockID > 0)
            {
                var existingBlocks = GetAllBlockPointersForFile(existingEntry.FilePointer,
                                                                existingEntry.BytesInFile);
                FreeFileBlocks(existingBlocks);

                // Real, separate pre-existing leak fixed here: this path never freed the old
                // top-level index/sub-index block itself, unlike DeleteFile — every overwrite of
                // an existing file permanently leaked its previous index block.
                FreeStructuralBlocks(existingEntry.FilePointer);

                // Another real, separate bug fixed alongside: this path never adjusted the
                // user's PagesUsed at all on overwrite, even though real disk usage can grow or
                // shrink. Quota tracks only real data blocks (excludes structural blocks and
                // sparse holes, same as CreateNewFile/DeleteFile) — see CountRealDataPages.
                if (user != null)
                {
                    uint oldRealPages = CountRealDataPages(existingBlocks);
                    if (user.PagesUsed >= oldRealPages)
                        user.PagesUsed -= oldRealPages;
                    else
                        user.PagesUsed = 0;
                }
            }

            // Allocate new blocks
            uint filePages = (uint)((data.Length + NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE - 1) /
                                   NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE);

            if (filePages == 0)
                filePages = 1;

            BlockPointer.PointerType pointerType;
            if (filePages <= 512)
                pointerType = BlockPointer.PointerType.Indexed;
            else if (filePages <= 262144)
                pointerType = BlockPointer.PointerType.SubIndexed;
            else
                throw new InvalidOperationException($"File too large: {data.Length} bytes");

            var allocatedBlocks = AllocateFileBlocksSparse(data, pointerType);
            if (allocatedBlocks == null || allocatedBlocks.Count == 0)
                throw new InvalidOperationException("Failed to allocate blocks for file");

            // Write file data with sparse support (SubIndexed writes its data pages inline
            // during allocation — see the comment in CreateNewFile for why).
            if (pointerType != BlockPointer.PointerType.SubIndexed)
            {
                WriteFileDataSparse(allocatedBlocks, data);
            }

            // Update entry
            existingEntry.BytesInFile = (ulong)data.Length;

            // Allocated count, not the logical extent - see the note at the create site.
            existingEntry.PagesInFile = CountRealDataPages(data);
            existingEntry.FilePointer = allocatedBlocks[0];
            existingEntry.LastDateOpenedForWrite = NdDateTime.ToNdDateOrUnset(DateTime.Now);

            if (user != null)
            {
                user.PagesUsed += CountRealDataPages(data);
                if (_masterBlock.UserFilePointer != null)
                    _userFile.WritePageForEntry(_device, _masterBlock.UserFilePointer, user.UserIndex);
            }

            // Write just this entry's page
            if (_masterBlock.ObjectFilePointer != null)
                _objectFile.WritePageForEntry(_device, _masterBlock.ObjectFilePointer, existingEntry.ObjectIndex);

            if (_masterBlock.BitFilePointer != null)
                _bitFile.WriteToDisk(_device, _masterBlock.BitFilePointer);

            // Overwriting a file frees the old blocks and allocates new ones — the bitmap's
            // free-page total can shift either way, so refresh the cached count on disk.
            PersistMasterBlock();
        }

        private List<BlockPointer>? AllocateFileBlocks(uint filePages, BlockPointer.PointerType pointerType)
        {
            if (_device == null)
                return null;

            var blockList = new List<BlockPointer>();

            if (pointerType == BlockPointer.PointerType.Indexed)
            {
                // Allocate index block
                var indexBlock = _bitFile.FindFirstFreeBlock();
                if (indexBlock == null)
                    throw new InvalidOperationException("No free blocks available for index");

                _bitFile.AllocateBlocks(indexBlock.Value, 1);
                blockList.Add(new BlockPointer(indexBlock.Value, BlockPointer.PointerType.Indexed));

                // Allocate data blocks (placeholder - actual allocation done in AllocateFileBlocksSparse)
                for (uint i = 0; i < filePages; i++)
                {
                    var dataBlock = _bitFile.FindFirstFreeBlock();
                    if (dataBlock == null)
                        throw new InvalidOperationException($"No free blocks available for data page {i}");

                    _bitFile.AllocateBlocks(dataBlock.Value, 1);
                    blockList.Add(new BlockPointer(dataBlock.Value, BlockPointer.PointerType.Contiguous));
                }

                // Write index block
                WriteIndexBlock(blockList[0].BlockID, blockList.Skip(1).ToList());
            }
            else if (pointerType == BlockPointer.PointerType.SubIndexed)
            {
                throw new NotSupportedException("SubIndexed allocation not yet implemented");
            }

            return blockList;
        }

        /// <summary>
        /// Allocates file blocks with sparse hole support (BlockID=0 for zero-filled pages).
        /// </summary>
        private List<BlockPointer>? AllocateFileBlocksSparse(byte[] data, BlockPointer.PointerType pointerType)
        {
            if (_device == null)
                return null;

            uint filePages = (uint)((data.Length + NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE - 1) /
                                   NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE);

            var blockList = new List<BlockPointer>();

            if (pointerType == BlockPointer.PointerType.Indexed)
            {
                // Allocate index block
                var indexBlock = _bitFile.FindFirstFreeBlock();
                if (indexBlock == null)
                    throw new InvalidOperationException("No free blocks available for index");

                _bitFile.AllocateBlocks(indexBlock.Value, 1);
                blockList.Add(new BlockPointer(indexBlock.Value, BlockPointer.PointerType.Indexed));

                // Allocate data blocks, skipping zero-filled pages
                for (uint i = 0; i < filePages; i++)
                {
                    int pageOffset = (int)(i * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE);
                    int pageSize = Math.Min(NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE, data.Length - pageOffset);

                    // Check if page is all zeros (sparse hole)
                    bool isZeroPage = IsPageAllZeros(data, pageOffset, pageSize);

                    if (isZeroPage)
                    {
                        // Sparse hole - use BlockID=0
                        blockList.Add(new BlockPointer(0, BlockPointer.PointerType.Contiguous));
                    }
                    else
                    {
                        // Allocate real block
                        var dataBlock = _bitFile.FindFirstFreeBlock();
                        if (dataBlock == null)
                            throw new InvalidOperationException($"No free blocks available for data page {i}");

                        _bitFile.AllocateBlocks(dataBlock.Value, 1);
                        blockList.Add(new BlockPointer(dataBlock.Value, BlockPointer.PointerType.Contiguous));
                    }
                }

                // Write index block with sparse holes
                WriteIndexBlock(blockList[0].BlockID, blockList.Skip(1).ToList());
            }
            else if (pointerType == BlockPointer.PointerType.SubIndexed)
            {
                // Sub-indexed: file spans more than 512 pages (over ~1MB at 2KB/page). Data is
                // split into groups of up to 512 pages, each with its own Indexed index block —
                // exactly the Indexed case above, repeated per group. A top-level sub-index block
                // (Type=SubIndexed) holds up to 512 pointers to those group index blocks, giving a
                // maximum of 512*512 = 262144 pages. Mirrors the read side's
                // GetBlockPointersSubIndexed layout exactly.
                const uint PagesPerGroup = 512;
                const int PageSize = NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;

                var groupIndexPointers = new List<BlockPointer>();
                var allBlocksForFreeing = new List<BlockPointer>();

                uint pagesRemaining = filePages;
                uint pageCursor = 0;

                while (pagesRemaining > 0)
                {
                    uint pagesThisGroup = Math.Min(pagesRemaining, PagesPerGroup);

                    var groupIndexBlock = _bitFile.FindFirstFreeBlock();
                    if (groupIndexBlock == null)
                        throw new InvalidOperationException("No free blocks available for sub-index group index block");
                    _bitFile.AllocateBlocks(groupIndexBlock.Value, 1);
                    var groupIndexPointer = new BlockPointer(groupIndexBlock.Value, BlockPointer.PointerType.Indexed);

                    var groupDataBlocks = new List<BlockPointer>();
                    for (uint i = 0; i < pagesThisGroup; i++)
                    {
                        uint absolutePage = pageCursor + i;
                        int pageOffset = (int)(absolutePage * PageSize);
                        int pageSize = Math.Max(0, Math.Min(PageSize, data.Length - pageOffset));
                        bool isZeroPage = pageSize == 0 || IsPageAllZeros(data, pageOffset, pageSize);

                        if (isZeroPage)
                        {
                            groupDataBlocks.Add(new BlockPointer(0, BlockPointer.PointerType.Contiguous));
                        }
                        else
                        {
                            var dataBlock = _bitFile.FindFirstFreeBlock();
                            if (dataBlock == null)
                                throw new InvalidOperationException($"No free blocks available for data page {absolutePage}");

                            _bitFile.AllocateBlocks(dataBlock.Value, 1);
                            groupDataBlocks.Add(new BlockPointer(dataBlock.Value, BlockPointer.PointerType.Contiguous));

                            // Written inline here rather than via WriteFileDataSparse — see the
                            // comments in CreateNewFile/UpdateExistingFile for why.
                            var pageBuffer = new byte[PageSize];
                            Array.Copy(data, pageOffset, pageBuffer, 0, pageSize);
                            _device.WriteBlock(BlockAddress.FromLBA(dataBlock.Value), pageBuffer);
                        }

                        allBlocksForFreeing.Add(groupDataBlocks[groupDataBlocks.Count - 1]);
                    }

                    WriteIndexBlock(groupIndexBlock.Value, groupDataBlocks);
                    allBlocksForFreeing.Add(groupIndexPointer);
                    groupIndexPointers.Add(groupIndexPointer);

                    pageCursor += pagesThisGroup;
                    pagesRemaining -= pagesThisGroup;
                }

                if (groupIndexPointers.Count > 512)
                    throw new InvalidOperationException(
                        $"File exceeds maximum sub-indexed capacity: {groupIndexPointers.Count} index groups (max 512, i.e. 262144 pages)");

                var subIndexBlock = _bitFile.FindFirstFreeBlock();
                if (subIndexBlock == null)
                    throw new InvalidOperationException("No free blocks available for sub-index block");
                _bitFile.AllocateBlocks(subIndexBlock.Value, 1);
                WriteIndexBlock(subIndexBlock.Value, groupIndexPointers);

                blockList.Add(new BlockPointer(subIndexBlock.Value, BlockPointer.PointerType.SubIndexed));
                blockList.AddRange(allBlocksForFreeing);
            }

            return blockList;
        }

        /// <summary>
        /// Checks if a page in the data buffer is all zeros.
        /// </summary>
        private bool IsPageAllZeros(byte[] data, int offset, int length)
        {
            for (int i = 0; i < length; i++)
            {
                if (data[offset + i] != 0)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Counts how many of a file's logical pages are NOT sparse holes — i.e. how many pages
        /// AllocateFileBlocksSparse will actually allocate a real disk block for. Sparse-file
        /// accounting divergence fix (docs\NDFS-VALIDATION-PLAN.md in the norskdata-ndfs repo):
        /// user quota (PagesUsed) must reflect real disk consumption only, never the logical page
        /// count (which counts holes as if they consumed space) and never the index/sub-index
        /// structural blocks (filesystem overhead, not user data).
        /// </summary>
        private uint CountRealDataPages(byte[] data)
        {
            uint filePages = (uint)((data.Length + NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE - 1) /
                                   NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE);

            uint realPages = 0;
            for (uint i = 0; i < filePages; i++)
            {
                int pageOffset = (int)(i * NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE);
                int pageSize = Math.Min(NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE, data.Length - pageOffset);
                if (!IsPageAllZeros(data, pageOffset, pageSize))
                {
                    realPages++;
                }
            }

            return realPages;
        }

        /// <summary>
        /// Counts real (non-sparse) data-block pointers in an already-resolved block list — the
        /// list returned by <see cref="GetAllBlockPointersForFile"/>, which contains only data
        /// pointers (BlockID==0 for a sparse hole), never structural index/sub-index entries.
        /// </summary>
        private static uint CountRealDataPages(List<BlockPointer> dataPointers)
        {
            uint count = 0;
            for (int i = 0; i < dataPointers.Count; i++)
            {
                if (dataPointers[i].BlockID > 0)
                {
                    count++;
                }
            }

            return count;
        }

        private void WriteIndexBlock(uint indexBlockId, List<BlockPointer> dataBlocks)
        {
            if (_device == null)
                return;

            var indexBuffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];

            // Write data block pointers to index
            int offset = 0;
            for (int i = 0; i < dataBlocks.Count; i++)
            {
                WriteBlockPointer(indexBuffer, offset, dataBlocks[i].Native);
                offset += 4;
            }

            _device.WriteBlock(BlockAddress.FromLBA(indexBlockId), indexBuffer);
        }

        private static void WriteBlockPointer(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private void WriteFileData(List<BlockPointer> allocatedBlocks, byte[] data)
        {
            if (_device == null)
                return;

            // Skip index block (first element)
            var dataBlocks = allocatedBlocks.Skip(1).ToList();
            int dataOffset = 0;

            for (int i = 0; i < dataBlocks.Count; i++)
            {
                var pageBuffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
                int bytesToWrite = Math.Min(NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE,
                                           data.Length - dataOffset);

                if (bytesToWrite > 0)
                {
                    Array.Copy(data, dataOffset, pageBuffer, 0, bytesToWrite);
                    dataOffset += bytesToWrite;
                }

                _device.WriteBlock(BlockAddress.FromLBA(dataBlocks[i].BlockID), pageBuffer);
            }
        }

        /// <summary>
        /// Writes file data with sparse hole support (skips BlockID=0).
        /// </summary>
        private void WriteFileDataSparse(List<BlockPointer> allocatedBlocks, byte[] data)
        {
            if (_device == null)
                return;

            // Skip index block (first element)
            var dataBlocks = allocatedBlocks.Skip(1).ToList();
            int dataOffset = 0;

            for (int i = 0; i < dataBlocks.Count; i++)
            {
                // Skip sparse holes (BlockID=0)
                if (dataBlocks[i].BlockID == 0)
                {
                    // Skip the page in data
                    dataOffset += NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;
                    continue;
                }

                var pageBuffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
                int bytesToWrite = Math.Min(NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE,
                                           data.Length - dataOffset);

                if (bytesToWrite > 0)
                {
                    Array.Copy(data, dataOffset, pageBuffer, 0, bytesToWrite);
                    dataOffset += bytesToWrite;
                }

                _device.WriteBlock(BlockAddress.FromLBA(dataBlocks[i].BlockID), pageBuffer);
            }
        }

        private void FreeFileBlocks(List<BlockPointer> blocks)
        {
            for (int i = 0; i < blocks.Count; i++)
            {
                if (blocks[i].BlockID > 0)
                    _bitFile.FreeBlocks(blocks[i].BlockID, 1);
            }
        }

        // ==================== Boot Sector Operations ====================

        /// <summary>
        /// Loads boot code from NDFS block 0.
        /// </summary>
        public NdfsBootCode? LoadBootCode()
        {
            EnsureInitialized();

            if (_device == null)
                return null;

            return NdfsBootLoader.LoadBootCode(_device);
        }

        /// <summary>
        /// Detects the boot format without loading full code.
        /// </summary>
        public NdfsBootFormat DetectBootFormat()
        {
            EnsureInitialized();

            if (_device == null)
                return NdfsBootFormat.None;

            return NdfsBootLoader.DetectBootFormat(_device);
        }

        /// <summary>
        /// Checks if the NDFS disk is bootable.
        /// </summary>
        public bool IsBootable()
        {
            EnsureInitialized();

            if (_device == null)
                return false;

            return NdfsBootLoader.IsBootable(_device);
        }

        /// <summary>
        /// Detects the hard-disk controller family (SMD/ECC, Winchester, SCSI) targeted by a
        /// raw-binary bootstrap. Returns <see cref="NdfsBootControllerType.Unknown"/> for
        /// BPUN/FLOMON (floppy) boots or non-bootable disks.
        /// </summary>
        public NdfsBootControllerType DetectBootControllerType()
        {
            EnsureInitialized();

            if (_device == null)
                return NdfsBootControllerType.Unknown;

            return NdfsBootLoader.DetectControllerType(_device);
        }

        // ==================== PropertyBag Support ====================

        /// <summary>
        /// Reads a file and populates PropertyBag with NDFS-specific metadata.
        /// </summary>
        public byte[] ReadFileWithProperties(string path, IDictionary<string, object> propertyBag)
        {
            EnsureInitialized();

            var (userName, fileName) = ParseNdfsPath(path);
            ObjectEntry? obj = FindObjectEntry(userName, fileName);

            if (obj == null)
                throw new FileNotFoundException($"File not found: {path}");

            // Serialize NDFS metadata to property bag
            if (propertyBag != null)
            {
                PropertyBags.NdfsPropertyBagSerializer.SerializeObjectEntry(obj, propertyBag);
                AddSparseHoles(obj, propertyBag);
            }

            return ReadObjectData(obj);
        }

        /// <summary>
        /// Records where a file's sparse holes are, as ascending logical page indices.
        /// </summary>
        /// <param name="obj">
        /// The object entry whose file is being described.
        /// </param>
        /// <param name="propertyBag">
        /// Property bag to add <see cref="PropertyBags.NdfsPropertyBagKeys.Holes"/> to.
        /// </param>
        /// <remarks>
        /// <para>
        /// Only the file system can supply this: the holes live in the file's index, not in the
        /// object entry, so the serializer working from an entry alone cannot see them. Without it a
        /// sidecar records THAT a file is sparse - <c>BytesInFile / 2048</c> exceeds
        /// <c>PagesInFile</c> - but not where, and a file copied out to a host and back would return
        /// with a different layout.
        /// </para>
        /// <para>
        /// An empty array means the file was checked and is solid. The key is absent only when the
        /// file has no contents at all, which is not the same claim.
        /// </para>
        /// </remarks>
        private void AddSparseHoles(ObjectEntry obj, IDictionary<string, object> propertyBag)
        {
            if (obj.FilePointer == null || obj.FilePointer.BlockID == 0)
                return;

            List<BlockPointer> blocks = GetAllBlockPointersForFile(obj.FilePointer, obj.BytesInFile);

            int holeCount = 0;
            for (int i = 0; i < blocks.Count; i++)
            {
                if (blocks[i].BlockID == 0)
                    holeCount++;
            }

            var holes = new int[holeCount];
            int at = 0;
            for (int i = 0; i < blocks.Count; i++)
            {
                if (blocks[i].BlockID == 0)
                    holes[at++] = i;
            }

            propertyBag[PropertyBags.NdfsPropertyBagKeys.Holes] = holes;
        }

        /// <summary>
        /// Writes a file and applies NDFS-specific metadata from PropertyBag.
        /// </summary>
        public void WriteFileWithProperties(string path, byte[] data, IDictionary<string, object> propertyBag)
        {
            EnsureInitialized();

            if (_device == null || _device.IsReadOnly)
                throw new InvalidOperationException("Cannot write to read-only device");

            if (data == null)
                throw new ArgumentNullException(nameof(data));

            // Perform the real write first — this is the single source of truth for structural
            // fields (page allocation, byte count, file pointer, object slot/owner). A property bag
            // may have come from a different write entirely (e.g. a re-imported ".xat" sidecar from
            // a previous export), so it must never be allowed to override what was actually just
            // allocated on disk — see NdfsPropertyBagSerializer.ApplyPortableMetadata for exactly
            // which fields are safe to restore afterward.
            WriteFile(path, data);

            if (propertyBag == null || !PropertyBags.NdfsPropertyBagSerializer.HasNdfsMetadata(propertyBag))
                return;

            var (userName, fileName) = ParseNdfsPath(path);
            ObjectEntry? written = FindObjectEntry(userName, fileName);
            if (written == null)
                return; // WriteFile above just created/updated it; should not happen

            PropertyBags.NdfsPropertyBagSerializer.ApplyPortableMetadata(propertyBag, written);

            if (_masterBlock?.ObjectFilePointer != null)
                _objectFile.WritePageForEntry(_device, _masterBlock.ObjectFilePointer, written.ObjectIndex);
        }

        /// <summary>
        /// Gets PropertyBag for a file without reading its contents.
        /// </summary>
        public IDictionary<string, object>? GetFileProperties(string path)
        {
            EnsureInitialized();

            var (userName, fileName) = ParseNdfsPath(path);
            ObjectEntry? obj = FindObjectEntry(userName, fileName);

            if (obj == null)
                return null;

            var propertyBag = new Dictionary<string, object>();
            PropertyBags.NdfsPropertyBagSerializer.SerializeObjectEntry(obj, propertyBag);
            AddSparseHoles(obj, propertyBag);
            return propertyBag;
        }

        /// <summary>
        /// Helper method to find an ObjectEntry by user and file name.
        /// </summary>
        private ObjectEntry? FindObjectEntry(string userName, string fileName)
        {
            // Normalize filename: convert . separator to : for lookup compatibility
            fileName = NormalizeFileNameForLookup(fileName);

            if (string.IsNullOrEmpty(userName))
            {
                // Search all objects by full name (objectname:type)
                for (int i = 0; i < _objectFile.GetObjects().Count; i++)
                {
                    var o = _objectFile.GetObjects()[i];
                    var fullName = string.IsNullOrEmpty(o.Type)
                        ? o.ObjectName
                        : $"{o.ObjectName}{ExtensionSeparator}{o.Type.TrimEnd('\x27', ' ')}";

                    if (string.Equals(fullName, fileName, StringComparison.OrdinalIgnoreCase))
                        return o;
                }
            }
            else
            {
                // Search by user and filename
                for (int i = 0; i < _objectFile.GetObjects().Count; i++)
                {
                    var o = _objectFile.GetObjects()[i];
                    if (!string.Equals(o.UserName, userName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var fullName = string.IsNullOrEmpty(o.Type)
                        ? o.ObjectName
                        : $"{o.ObjectName}{ExtensionSeparator}{o.Type.TrimEnd('\x27', ' ')}";

                    if (string.Equals(fullName, fileName, StringComparison.OrdinalIgnoreCase))
                        return o;
                }
            }

            return null;
        }

        /// <summary>
        /// Extracts file name without extension.
        /// NDFS uses colon as separator (FILENAME:TYPE), also supports period for compatibility.
        /// </summary>
        private string GetFileNameWithoutExtension(string fileName)
        {
            // Check for colon separator first (NDFS native format)
            int colonIndex = fileName.LastIndexOf(':');
            if (colonIndex > 0)
                return fileName.Substring(0, colonIndex);

            // Fallback to period for compatibility
            int dotIndex = fileName.LastIndexOf('.');
            return dotIndex > 0 ? fileName.Substring(0, dotIndex) : fileName;
        }

        /// <summary>
        /// Extracts file extension.
        /// NDFS uses colon as separator (FILENAME:TYPE), also supports period for compatibility.
        /// </summary>
        private string GetFileExtension(string fileName)
        {
            // Check for colon separator first (NDFS native format)
            int colonIndex = fileName.LastIndexOf(':');
            if (colonIndex > 0 && colonIndex < fileName.Length - 1)
                return fileName.Substring(colonIndex + 1);

            // Fallback to period for compatibility
            int dotIndex = fileName.LastIndexOf('.');
            return dotIndex > 0 && dotIndex < fileName.Length - 1
                ? fileName.Substring(dotIndex + 1)
                : string.Empty;
        }

        // ==================== Extended Master Block Information ====================

        /// <summary>
        /// Gets the Extended Master Block information (if available).
        /// Extended Info is only valid for hard disks (not FLOMON floppy disks).
        /// </summary>
        public IDictionary<string, object>? GetExtendedMasterBlockInfo()
        {
            EnsureInitialized();

            if (_masterBlock == null || !_masterBlock.ExtValid)
                return null;

            var info = new Dictionary<string, object>
            {
                ["Checksum"] = _masterBlock.ExtCheckSum,
                ["ChecksumCalculated"] = _masterBlock.ExtCalculatedChecksum,
                ["ChecksumState"] = _masterBlock.ChecksumState.ToString(),
                ["ChecksumValid"] = (_masterBlock.ChecksumState == Structures.ChecksumValidationState.CrcValid),
                ["HasFlomon"] = _masterBlock.HasFlomon,
                ["PagesAvailable"] = _masterBlock.ExtPagesAvailable,
                ["SystemNumber"] = _masterBlock.ExtLastSystemNumber,
                ["FlagWord"] = _masterBlock.ExtFlagWord,
                ["Reserved1"] = _masterBlock.ExtReserved1,
                ["Reserved2"] = _masterBlock.ExtReserved2,
                ["Reserved3"] = _masterBlock.ExtReserved3,
                ["IsHardDisk"] = !_masterBlock.HasFlomon,
                ["IsFloppy"] = _masterBlock.HasFlomon
            };

            return info;
        }

        /// <summary>
        /// Checks if the Extended Master Block information is valid.
        /// Valid Extended Info has: Reserved fields = 0, Checksum != 0.
        /// FLOMON floppy disks have non-zero Reserved fields and should return false.
        /// </summary>
        public bool HasValidExtendedInfo()
        {
            EnsureInitialized();
            return _masterBlock != null && _masterBlock.ExtValid;
        }

        public void Dispose()
        {
            _initialized = false;
            _device = null;
        }
    }
}
