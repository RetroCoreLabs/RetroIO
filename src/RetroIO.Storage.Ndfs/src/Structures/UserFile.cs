//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Storage.Ndfs.Elements;

namespace RetroIO.Storage.Ndfs.Structures
{
    /// <summary>
    /// Represents the NDFS user file, which contains all user entries.
    /// </summary>
    public class UserFile
    {
        /// <summary>
        /// Pointer to the user file index block.
        /// </summary>
        public BlockPointer? IndexPointer { get; set; }

        /// <summary>
        /// Dictionary of user entries, keyed by user index.
        /// </summary>
        private Dictionary<int, UserEntry> _userEntries = new Dictionary<int, UserEntry>();

        private const int PAGE_SIZE = 2048;
        private const int ENTRIES_PER_PAGE = 32; // 2048 / 64

        /// <summary>
        /// Gets all user entries.
        /// </summary>
        public IReadOnlyList<UserEntry> GetUsers()
        {
            return _userEntries.Values.ToList();
        }

        /// <summary>
        /// Gets a user entry by index.
        /// </summary>
        public UserEntry? GetUser(int index)
        {
            return _userEntries.TryGetValue(index, out var entry) ? entry : null;
        }

        /// <summary>
        /// Finds a user by name - NO LINQ.
        /// </summary>
        public UserEntry? FindUser(string userName)
        {
            // NO LINQ - use manual iteration
            Dictionary<int, UserEntry>.ValueCollection.Enumerator enumerator = _userEntries.Values.GetEnumerator();
            while (enumerator.MoveNext())
            {
                UserEntry user = enumerator.Current;
                if (string.Equals(user.UserName, userName, StringComparison.OrdinalIgnoreCase))
                {
                    enumerator.Dispose();
                    return user;
                }
            }
            enumerator.Dispose();
            return null;
        }

        /// <summary>
        /// Adds a user entry to the in-memory collection.
        /// </summary>
        public void AddUser(UserEntry entry)
        {
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));

            _userEntries[entry.UserIndex] = entry;
        }

        /// <summary>
        /// Removes a user entry from the in-memory collection.
        /// </summary>
        public bool RemoveUser(int index)
        {
            return _userEntries.Remove(index);
        }

        /// <summary>
        /// Updates a user's quota.
        /// </summary>
        public bool UpdateUserQuota(int userIndex, uint newReservedPages)
        {
            if (_userEntries.TryGetValue(userIndex, out var user))
            {
                user.PagesReserved = newReservedPages;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Updates a user's usage.
        /// </summary>
        public bool UpdateUserUsage(int userIndex, uint pagesUsed)
        {
            if (_userEntries.TryGetValue(userIndex, out var user))
            {
                user.PagesUsed = pagesUsed;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Calculates the total pages reserved by all users - NO LINQ.
        /// </summary>
        public uint GetTotalPagesReserved()
        {
            // NO LINQ - use manual sum
            uint total = 0;
            Dictionary<int, UserEntry>.ValueCollection.Enumerator enumerator = _userEntries.Values.GetEnumerator();
            while (enumerator.MoveNext())
            {
                total += enumerator.Current.PagesReserved;
            }
            enumerator.Dispose();
            return total;
        }

        /// <summary>
        /// Calculates the total pages used by all users - NO LINQ.
        /// </summary>
        public uint GetTotalPagesUsed()
        {
            // NO LINQ - use manual sum
            uint total = 0;
            Dictionary<int, UserEntry>.ValueCollection.Enumerator enumerator = _userEntries.Values.GetEnumerator();
            while (enumerator.MoveNext())
            {
                total += enumerator.Current.PagesUsed;
            }
            enumerator.Dispose();
            return total;
        }

        /// <summary>
        /// Gets users who have exceeded their quota - NO LINQ.
        /// </summary>
        public List<UserEntry> GetOverQuotaUsers()
        {
            // NO LINQ - use manual iteration
            var result = new List<UserEntry>();
            Dictionary<int, UserEntry>.ValueCollection.Enumerator enumerator = _userEntries.Values.GetEnumerator();
            while (enumerator.MoveNext())
            {
                UserEntry user = enumerator.Current;
                if (user.IsOverQuota())
                {
                    result.Add(user);
                }
            }
            enumerator.Dispose();
            return result;
        }

        /// <summary>
        /// Finds the next available user index.
        /// </summary>
        public int GetNextAvailableIndex()
        {
            if (_userEntries.Count == 0)
                return 0;

            // Find the first gap or return max + 1
            int maxIndex = _userEntries.Keys.Max();
            for (int i = 0; i <= maxIndex; i++)
            {
                if (!_userEntries.ContainsKey(i))
                    return i;
            }

            return maxIndex + 1;
        }

        /// <summary>
        /// Validates that a user can allocate the specified number of pages.
        /// </summary>
        public bool CanUserAllocate(int userIndex, uint additionalPages)
        {
            if (!_userEntries.TryGetValue(userIndex, out var user))
                return false;

            return user.PagesUsed + additionalPages <= user.PagesReserved;
        }

        /// <summary>
        /// Clears all user entries.
        /// </summary>
        public void Clear()
        {
            _userEntries.Clear();
        }

        /// <summary>
        /// Writes the single page that contains the given userIndex to disk.
        /// Reads the index block, finds the data block for that page,
        /// rebuilds the page from in-memory entries, writes it back. One read, one write.
        /// </summary>
        public void WritePageForEntry(RetroIO.Storage.Core.IBlockDevice device, BlockPointer indexPointer, int userIndex)
        {
            if (device == null || indexPointer == null || indexPointer.BlockID == 0)
                throw new ArgumentException("Invalid device or index pointer");

            int pageNum = userIndex / ENTRIES_PER_PAGE;
            if (pageNum < 0 || pageNum >= 512)
                throw new ArgumentOutOfRangeException(nameof(userIndex));

            // Read index block to find the on-disk block ID for this page.
            var indexBuffer = new byte[PAGE_SIZE];
            device.ReadBlock(RetroIO.Storage.Core.BlockAddress.FromLBA(indexPointer.BlockID), indexBuffer);

            var dataPointer = BlockPointer.FromBytes(indexBuffer, pageNum * 4);
            if (dataPointer.BlockID == 0)
                return; // page not allocated — nothing to write

            // Rebuild this single page from in-memory entries.
            var dataBuffer = new byte[PAGE_SIZE]; // zero-filled
            for (int slot = 0; slot < ENTRIES_PER_PAGE; slot++)
            {
                int idx = pageNum * ENTRIES_PER_PAGE + slot;
                if (_userEntries.TryGetValue(idx, out var entry))
                {
                    var entryBytes = entry.ToBytes();
                    Array.Copy(entryBytes, 0, dataBuffer, slot * 64, 64);
                }
                // slots with no entry stay zeroed (clears deleted users)
            }
            device.WriteBlock(RetroIO.Storage.Core.BlockAddress.FromLBA(dataPointer.BlockID), dataBuffer);
        }
    }
}
