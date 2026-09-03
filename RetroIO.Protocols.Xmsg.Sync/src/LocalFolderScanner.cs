using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace RetroIO.Protocols.Xmsg.Sync
{
    /// <summary>
    /// Reads a mapped folder off disk: which files are in it, and the hash of each one.
    /// </summary>
    /// <remarks>
    /// <para><b>Polling, not a file-system watcher, and that is a choice</b></para>
    /// <para>
    /// A watcher delivers an event per write, which for an editor saving a file can be several
    /// events for one save, and none at all for a file copied in while the process was not
    /// running. Since a transfer to a real machine takes seconds anyway, a scan every few seconds
    /// costs nothing and has neither problem: whatever is in the folder is what gets considered,
    /// however it arrived. <see cref="FileSettleTracker"/> then holds back anything still being
    /// written.
    /// </para>
    /// <para><b>Hashing every file every pass is deliberate</b></para>
    /// <para>
    /// Size and timestamp are cheaper but they lie: an editor can rewrite a file to the same
    /// length in the same second, and some tools preserve the timestamp outright. The ledger's
    /// promise is about CONTENT, so content is what is measured. These are source files on a
    /// folder small enough to mirror onto a 1980s machine - the cost is not worth a wrong answer.
    /// </para>
    /// <para><b>A file that cannot be read is skipped, not fatal</b></para>
    /// <para>
    /// Something else holding a file open is the normal state of a folder somebody is working in.
    /// It is left out of this pass and picked up by the next one.
    /// </para>
    /// </remarks>
    public static class LocalFolderScanner
    {
        /// <summary>
        /// Lists and hashes the files in a folder.
        /// </summary>
        /// <param name="folder">
        /// The folder to read.
        /// </param>
        /// <param name="subfolders">
        /// Whether files below the folder are included.
        /// </param>
        /// <param name="unreadable">
        /// Filled with the paths that could not be read this pass, for reporting. May be null.
        /// </param>
        /// <returns>
        /// One entry per readable file, in the order the file system gave them.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="folder"/> is null.
        /// </exception>
        /// <remarks>
        /// A folder that does not exist yields nothing rather than throwing: a mapping may name a
        /// folder that has not been made yet, and a daemon must not die of it.
        /// </remarks>
        public static IReadOnlyList<LocalFileState> Scan(
            string folder, SyncSubfolderPolicy subfolders, List<string>? unreadable)
        {
            if (folder == null) { throw new ArgumentNullException(nameof(folder)); }

            List<LocalFileState> found = new List<LocalFileState>();
            if (!Directory.Exists(folder))
            {
                return found;
            }

            // FlattenAll means files below the folder are carried too - a SINTRAN user directory is
            // flat, so they land beside the top-level ones and the mapping's name rules decide what
            // they are called there.
            SearchOption option = subfolders == SyncSubfolderPolicy.FlattenAll
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;

            string[] paths;
            try
            {
                paths = Directory.GetFiles(folder, "*", option);
            }
            catch (IOException)
            {
                return found;
            }
            catch (UnauthorizedAccessException)
            {
                return found;
            }

            for (int i = 0; i < paths.Length; i++)
            {
                byte[]? hash = TryHash(paths[i]);
                if (hash == null)
                {
                    unreadable?.Add(paths[i]);
                    continue;
                }

                found.Add(new LocalFileState(paths[i], hash));
            }

            return found;
        }

        /// <summary>
        /// Hashes one file's contents.
        /// </summary>
        /// <param name="path">
        /// The file to hash.
        /// </param>
        /// <returns>
        /// The SHA-256 of the file, or null when it could not be read.
        /// </returns>
        /// <remarks>
        /// Streamed rather than read whole: a folder being mirrored can contain something far
        /// larger than anything that would ever be sent, and reading it into memory to decide NOT
        /// to send it would be a poor trade.
        /// </remarks>
        public static byte[]? TryHash(string path)
        {
            try
            {
                using (FileStream stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (SHA256 sha = SHA256.Create())
                {
                    return sha.ComputeHash(stream);
                }
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
