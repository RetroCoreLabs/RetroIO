//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Storage.Ndfs.Elements;

namespace RetroIO.Storage.Ndfs.Structures
{
    /// <summary>
    /// Represents the NDFS object file, which contains all file entries.
    /// </summary>
    public class ObjectFile
    {
        /// <summary>
        /// Pointer to the object file index block.
        /// </summary>
        public BlockPointer? IndexPointer { get; set; }

        /// <summary>
        /// Dictionary of object entries, keyed by object index.
        /// </summary>
        private Dictionary<int, ObjectEntry> _objectEntries = new Dictionary<int, ObjectEntry>();

        private const int PAGE_SIZE = 2048;
        private const int ENTRIES_PER_PAGE = 32; // 2048 / 64

        /// <summary>
        /// Gets all object entries.
        /// </summary>
        public IReadOnlyList<ObjectEntry> GetObjects()
        {
            return _objectEntries.Values.ToList();
        }

        /// <summary>
        /// Gets an object entry by index.
        /// </summary>
        public ObjectEntry? GetObject(int index)
        {
            return _objectEntries.TryGetValue(index, out var entry) ? entry : null;
        }

        /// <summary>
        /// Finds an object by name and user - NO LINQ.
        /// </summary>
        public ObjectEntry? FindObject(string objectName, string userName)
        {
            // NO LINQ - use manual iteration
            Dictionary<int, ObjectEntry>.ValueCollection.Enumerator enumerator = _objectEntries.Values.GetEnumerator();
            while (enumerator.MoveNext())
            {
                ObjectEntry obj = enumerator.Current;
                if (string.Equals(obj.ObjectName, objectName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(obj.UserName, userName, StringComparison.OrdinalIgnoreCase))
                {
                    enumerator.Dispose();
                    return obj;
                }
            }
            enumerator.Dispose();
            return null;
        }

        /// <summary>
        /// Adds an object entry to the in-memory collection.
        /// </summary>
        public void AddObject(ObjectEntry entry)
        {
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));

            _objectEntries[entry.ObjectIndex] = entry;
        }

        /// <summary>
        /// Removes an object entry from the in-memory collection.
        /// </summary>
        public bool RemoveObject(int index)
        {
            return _objectEntries.Remove(index);
        }

        /// <summary>
        /// Gets all objects for a specific user - NO LINQ.
        /// </summary>
        public List<ObjectEntry> GetUserObjects(string userName)
        {
            // NO LINQ - use manual iteration
            var result = new List<ObjectEntry>();
            Dictionary<int, ObjectEntry>.ValueCollection.Enumerator enumerator = _objectEntries.Values.GetEnumerator();
            while (enumerator.MoveNext())
            {
                ObjectEntry obj = enumerator.Current;
                if (string.Equals(obj.UserName, userName, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(obj);
                }
            }
            enumerator.Dispose();
            return result;
        }

        /// <summary>
        /// Gets all objects for a specific user by index - NO LINQ.
        /// </summary>
        public List<ObjectEntry> GetUserObjects(int userIndex)
        {
            // NO LINQ - use manual iteration
            var result = new List<ObjectEntry>();
            Dictionary<int, ObjectEntry>.ValueCollection.Enumerator enumerator = _objectEntries.Values.GetEnumerator();
            while (enumerator.MoveNext())
            {
                ObjectEntry obj = enumerator.Current;
                if (obj.UserIndex == userIndex)
                {
                    result.Add(obj);
                }
            }
            enumerator.Dispose();
            return result;
        }

        /// <summary>
        /// Calculates the total pages used by all objects.
        /// </summary>
        public uint GetTotalPagesUsed()
        {
            return (uint)_objectEntries.Values.Sum(obj => obj.PagesInFile);
        }

        /// <summary>
        /// Calculates the total bytes used by all objects.
        /// </summary>
        public ulong GetTotalBytesUsed()
        {
            return (ulong)_objectEntries.Values.Sum(obj => (long)obj.BytesInFile);
        }

        /// <summary>
        /// Finds the next available object index.
        /// </summary>
        public int GetNextAvailableIndex()
        {
            if (_objectEntries.Count == 0)
                return 0;

            // Find the first gap or return max + 1
            int maxIndex = _objectEntries.Keys.Max();
            for (int i = 0; i <= maxIndex; i++)
            {
                if (!_objectEntries.ContainsKey(i))
                    return i;
            }

            return maxIndex + 1;
        }

        /// <summary>
        /// Finds the next free object slot WITHIN a user's FIRST object block. SINTRAN
        /// partitions the object file so user U owns slots U*256..U*256+255 and
        /// the object-index high byte is the owning user. Returns -1 if that block is full.
        /// </summary>
        /// <param name="userIndex">
        /// The owning user.
        /// </param>
        /// <returns>
        /// A free physical slot in the user's first object block, or <c>-1</c> when it is full.
        /// </returns>
        /// <remarks>
        /// <para>
        /// <b>KNOWN INCOMPLETE (2026-08-01): this only ever searches ONE object block, so it cannot
        /// place file 257.</b>
        /// </para>
        /// <para>
        /// A SINTRAN user area holds 256 files per <i>object block</i>, and may be granted up to 16
        /// blocks (4096 files) with <c>@GIVE-OBJECT-BLOCKS</c>. Block <c>n</c> for user <c>U</c>
        /// lives at object-file pages <c>n*512 + U*8 .. +7</c>, so a correct search spans every
        /// ALLOCATED block (<see cref="Elements.UserEntry.AllocatedObjectBlocks"/>) and returns
        /// "needs a new block" - not "full" - while
        /// <see cref="Elements.UserEntry.AllocatedObjectBlocks"/> is below
        /// <see cref="Elements.UserEntry.MaxObjectBlocks"/>.
        /// </para>
        /// <para>
        /// Returning <c>-1</c> here currently makes callers throw "object table is full" at 256
        /// files, which is premature on any user that has been granted more blocks.
        /// </para>
        /// <para>
        /// Doc: <c>RetroFS/docs/NDFS-Object-Blocks.md</c> and
        /// <c>norskdata-ndfs/docs/NDFS-OBJECT-BLOCKS-SPEC.md</c>.
        /// </para>
        /// </remarks>
        public int FindFreeUserSlot(int userIndex)
        {
            return FindFreeUserSlot(userIndex, 1, 1);
        }

        /// <summary>
        /// Physical object-file position of a slot inside one of a user's object blocks.
        /// </summary>
        /// <param name="userIndex">
        /// The owning user.
        /// </param>
        /// <param name="block">
        /// The object block, 0-based.
        /// </param>
        /// <param name="slotInBlock">
        /// The slot within that block, 0..255.
        /// </param>
        /// <returns>
        /// The value to store in <see cref="Elements.ObjectEntry.ObjectIndex"/>.
        /// </returns>
        /// <remarks>
        /// Object block <c>block</c> for user <c>userIndex</c> occupies object-file pages
        /// <c>block*512 + userIndex*8 .. +7</c>, and each page holds 32 entries. The user-visible
        /// file number for the same slot is <c>block*256 + slotInBlock</c> - see
        /// <see cref="Elements.ObjectEntry.ComputeFileNumber"/>.
        /// </remarks>
        public static int PhysicalSlot(int userIndex, int block, int slotInBlock)
        {
            const int entriesPerPage = 32;
            const int pagesPerObjectBlock = 8;
            const int pagesPerIndexBlock = 512;

            int page = (block * pagesPerIndexBlock)
                     + (userIndex * pagesPerObjectBlock)
                     + (slotInBlock / entriesPerPage);
            return (page * entriesPerPage) + (slotInBlock % entriesPerPage);
        }

        /// <summary>
        /// Finds the next free object slot for a user, searching every object block they have.
        /// </summary>
        /// <param name="userIndex">
        /// The owning user.
        /// </param>
        /// <param name="maxObjectBlocks">
        /// The user's MXOBL - see <see cref="Elements.UserEntry.MaxObjectBlocks"/>.
        /// </param>
        /// <param name="allocatedObjectBlocks">
        /// The user's ACOBL - see <see cref="Elements.UserEntry.AllocatedObjectBlocks"/>.
        /// </param>
        /// <returns>
        /// A free physical slot, or <c>-1</c> when every block the user is permitted is full.
        /// </returns>
        /// <remarks>
        /// <para>
        /// A user area holds 256 files per object block, one by default and up to
        /// <paramref name="maxObjectBlocks"/> (16 max, 4096 files) once the operator has run
        /// <c>@GIVE-OBJECT-BLOCKS</c>. Blocks are allocated on demand, so this searches every block
        /// already allocated and, when they are all full, returns the first slot of the NEXT block
        /// while one is still permitted.
        /// </para>
        /// <para>
        /// The caller must raise the user's <see cref="Elements.UserEntry.AllocatedObjectBlocks"/>
        /// when the returned slot falls in a block beyond the current count, and must make the page
        /// exist. Holes are normal and are reused: deleting a file leaves one and SINTRAN never
        /// compacts.
        /// </para>
        /// <para>
        /// <c>-1</c> now means the user is genuinely at their SINTRAN ceiling, rather than merely
        /// past 256 files as it used to.
        /// </para>
        /// </remarks>
        /// <remarks>
        /// <para>
        /// <b>Block 1 and above overlap the high-user region.</b> An index block holds 512 pages =
        /// 64 users at 8 pages each, so index-block group <c>g</c> is where this library places
        /// users <c>g*64 .. g*64+63</c>. SINTRAN instead puts user <c>U</c>'s object block
        /// <c>n</c> in group <c>n</c> at the same offset - so a second block for user <c>U</c>
        /// lands exactly where user <c>64+U</c>'s FIRST block would go. Both layouts are real and
        /// they cannot both own those pages.
        /// </para>
        /// <para>
        /// This method only computes placement. The caller must refuse to grow into block
        /// <c>n</c> when user <c>n*64 + userIndex</c> exists - see
        /// <c>NdfsFileSystem.AllocateUserObjectSlot</c>.
        /// </para>
        /// </remarks>
        public int FindFreeUserSlot(int userIndex, int maxObjectBlocks, int allocatedObjectBlocks)
        {
            const int filesPerObjectBlock = 256;
            const int maxObjectBlocksAllowed = 16;

            int maxBlocks = maxObjectBlocks < 1 ? 1 : (maxObjectBlocks > maxObjectBlocksAllowed ? maxObjectBlocksAllowed : maxObjectBlocks);
            int allocated = allocatedObjectBlocks < 1 ? 1 : allocatedObjectBlocks;
            if (allocated > maxBlocks)
            {
                allocated = maxBlocks;
            }

            for (int block = 0; block < allocated; block++)
            {
                for (int slotInBlock = 0; slotInBlock < filesPerObjectBlock; slotInBlock++)
                {
                    int physical = PhysicalSlot(userIndex, block, slotInBlock);
                    if (!_objectEntries.ContainsKey(physical))
                    {
                        return physical;
                    }
                }
            }

            // Every allocated block is full: open the next one if the user's maximum allows.
            if (allocated < maxBlocks)
            {
                return PhysicalSlot(userIndex, allocated, 0);
            }

            return -1;
        }

        /// <summary>
        /// Clears all object entries.
        /// </summary>
        public void Clear()
        {
            _objectEntries.Clear();
        }

        /// <summary>
        /// Writes the single page that contains the given objectIndex to disk.
        /// Reads the index block (walking one extra level of indirection first if the object
        /// file has grown into a SubIndexed structure), finds the data block for that page,
        /// rebuilds the page from in-memory entries, writes it back.
        /// </summary>
        public void WritePageForEntry(RetroIO.Storage.Core.IBlockDevice device, BlockPointer indexPointer, int objectIndex)
        {
            if (device == null || indexPointer == null || indexPointer.BlockID == 0)
                throw new ArgumentException("Invalid device or index pointer");

            int pageNum = objectIndex / ENTRIES_PER_PAGE;
            const int GroupCapacity = 512;
            if (pageNum < 0 || pageNum >= GroupCapacity * GroupCapacity)
                throw new ArgumentOutOfRangeException(nameof(objectIndex));

            // Resolve the actual Indexed group block + within-group page number. For a plain
            // Indexed object file (the common case, and the only case this method used to
            // support) this is a no-op: indexPointer IS the group block and pageNum is already
            // relative to it. For a SubIndexed object file (more than 512 directory pages,
            // i.e. more than 16,384 files or more than ~64 users touched), one extra level of
            // indirection must be walked first.
            uint groupBlockId = indexPointer.BlockID;
            int pageWithinGroup = pageNum;

            if (indexPointer.Type == BlockPointer.PointerType.SubIndexed)
            {
                if (pageNum >= GroupCapacity)
                {
                    int groupIdx = pageNum / GroupCapacity;
                    pageWithinGroup = pageNum % GroupCapacity;

                    var subIndexBuffer = new byte[PAGE_SIZE];
                    device.ReadBlock(RetroIO.Storage.Core.BlockAddress.FromLBA(indexPointer.BlockID), subIndexBuffer);
                    var groupPointer = BlockPointer.FromBytes(subIndexBuffer, groupIdx * 4);
                    if (groupPointer.BlockID == 0)
                        return; // group not allocated — nothing to write

                    groupBlockId = groupPointer.BlockID;
                }
                // else: pageNum < 512 falls in "group 0", which for a converted object file is
                // the original Indexed block, reachable only by reading the sub-index block's
                // slot 0 — resolve that the same way as any other group, for consistency.
                else
                {
                    var subIndexBuffer = new byte[PAGE_SIZE];
                    device.ReadBlock(RetroIO.Storage.Core.BlockAddress.FromLBA(indexPointer.BlockID), subIndexBuffer);
                    var group0Pointer = BlockPointer.FromBytes(subIndexBuffer, 0);
                    if (group0Pointer.BlockID == 0)
                        return;

                    groupBlockId = group0Pointer.BlockID;
                }
            }

            // Read the (now definitely plain Indexed) group block to find the on-disk block ID
            // for this page.
            var indexBuffer = new byte[PAGE_SIZE];
            device.ReadBlock(RetroIO.Storage.Core.BlockAddress.FromLBA(groupBlockId), indexBuffer);

            var dataPointer = BlockPointer.FromBytes(indexBuffer, pageWithinGroup * 4);
            if (dataPointer.BlockID == 0)
                return; // page not allocated — nothing to write

            // Rebuild this single page from in-memory entries.
            var dataBuffer = new byte[PAGE_SIZE]; // zero-filled
            for (int slot = 0; slot < ENTRIES_PER_PAGE; slot++)
            {
                int objIdx = pageNum * ENTRIES_PER_PAGE + slot;
                if (_objectEntries.TryGetValue(objIdx, out var entry))
                {
                    entry.ToBytes(dataBuffer, slot * 64);
                }
                // slots with no entry stay zeroed (clears deleted files)
            }
            device.WriteBlock(RetroIO.Storage.Core.BlockAddress.FromLBA(dataPointer.BlockID), dataBuffer);
        }
    }
}
