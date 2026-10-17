//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;

namespace RetroIO.Protocols.Xmsg.Ndfs
{
    /// <summary>
    /// The outcome of a file-store operation.
    /// </summary>
    public enum FaStoreStatus
    {
        /// <summary>
        /// The operation succeeded.
        /// </summary>
        Ok = 0,

        /// <summary>
        /// No file of that name exists.
        /// </summary>
        NoSuchFile,

        /// <summary>
        /// A file of that name already exists.
        /// </summary>
        AlreadyExists,

        /// <summary>
        /// The name could not be parsed, or resolved outside the served folder.
        /// </summary>
        BadName,

        /// <summary>
        /// The file handle is not open.
        /// </summary>
        NotOpen,

        /// <summary>
        /// The file is open for reading and the operation writes (or the reverse).
        /// </summary>
        WrongAccess,

        /// <summary>
        /// The host filesystem refused the operation.
        /// </summary>
        IoError
    }

    /// <summary>
    /// One file as the store reports it.
    /// </summary>
    public sealed class FaFileInfo
    {
        /// <summary>
        /// Initialises the entry.
        /// </summary>
        /// <param name="fileNumber">
        /// The stable file number.
        /// </param>
        /// <param name="name">
        /// The SINTRAN name.
        /// </param>
        /// <param name="byteLength">
        /// The file length in bytes.
        /// </param>
        /// <param name="modifiedUtc">
        /// Last write time, UTC.
        /// </param>
        public FaFileInfo(ushort fileNumber, FaFileName name, long byteLength, DateTime modifiedUtc)
        {
            FileNumber = fileNumber;
            Name = name;
            ByteLength = byteLength;
            ModifiedUtc = modifiedUtc;
        }

        /// <summary>
        /// Gets the stable file number.
        /// </summary>
        public ushort FileNumber { get; }

        /// <summary>
        /// Gets the SINTRAN name.
        /// </summary>
        public FaFileName Name { get; }

        /// <summary>
        /// Gets the file length in bytes.
        /// </summary>
        public long ByteLength { get; }

        /// <summary>
        /// Gets the last write time, UTC.
        /// </summary>
        public DateTime ModifiedUtc { get; }
    }

    /// <summary>
    /// Serves the files in one Windows folder to remote SINTRAN clients.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the store behind the FA protocol: the codec in <c>RetroIO.Protocols.Xmsg.Fa</c> already knows
    /// how a <c>*FA-SERVER</c> conversation is framed, but nothing was holding actual files. This
    /// holds them, and knows nothing about the wire.
    /// </para>
    /// <para><b>File numbers are stable, and holes are never reused</b></para>
    /// <para>
    /// A file number is handed out once and stays with that file for as long as it exists; deleting
    /// a file leaves a hole that is never filled. A sync tool may therefore cache them. This mirrors
    /// SINTRAN, where the number is the ordinal RANK of the entry's index-block group among the
    /// groups that user occupies - NOT the physical group, because SINTRAN relocates a user's
    /// overflow object block when another user needs that group. We do not have to imitate that
    /// relocation, but we DO have to keep the promise that the number is stable.
    /// </para>
    /// <para><b>Names are the identifier on the wire</b></para>
    /// <para>
    /// Requests arrive BY NAME. File numbers exist for reporting and for a client that caches them;
    /// they are never how a file is asked for. Note also that a file number is 16-bit (4096 files
    /// per user as 16 object blocks of 256) - anything that narrows one to 8 bits is a bug.
    /// </para>
    /// </remarks>
    public sealed class FolderFileStore
    {
        private readonly string _root;
        private readonly Dictionary<string, ushort> _fileNumbers =
            new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ushort, OpenFile> _open = new Dictionary<ushort, OpenFile>();
        private readonly object _gate = new object();

        private ushort _nextFileNumber = 1;
        private ushort _nextHandle = 1;

        /// <summary>
        /// Opens a store over a folder, creating the folder when it does not exist.
        /// </summary>
        /// <param name="rootFolder">
        /// The Windows folder whose files are served.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="rootFolder"/> is null or blank.
        /// </exception>
        public FolderFileStore(string rootFolder)
        {
            if (string.IsNullOrWhiteSpace(rootFolder))
            {
                throw new ArgumentException("A root folder is required.", nameof(rootFolder));
            }

            _root = Path.GetFullPath(rootFolder);
            Directory.CreateDirectory(_root);
        }

        /// <summary>
        /// Gets the folder being served.
        /// </summary>
        public string RootFolder
        {
            get { return _root; }
        }

        /// <summary>
        /// Lists the files in the served folder.
        /// </summary>
        /// <returns>
        /// The files, in name order.
        /// </returns>
        public IReadOnlyList<FaFileInfo> ListFiles()
        {
            List<FaFileInfo> results = new List<FaFileInfo>();

            lock (_gate)
            {
                string[] paths = Directory.GetFiles(_root);
                Array.Sort(paths, StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < paths.Length; i++)
                {
                    FaFileName? name = FromHostPath(paths[i]);
                    if (name == null)
                    {
                        continue;
                    }

                    FileInfo info = new FileInfo(paths[i]);
                    results.Add(new FaFileInfo(
                        FileNumberFor(name), name, info.Length, info.LastWriteTimeUtc));
                }
            }

            return results;
        }

        /// <summary>
        /// Creates an empty file.
        /// </summary>
        /// <param name="specification">
        /// The SINTRAN file specification.
        /// </param>
        /// <param name="fileNumber">
        /// Receives the new file's stable number.
        /// </param>
        /// <returns>
        /// The outcome.
        /// </returns>
        public FaStoreStatus Create(string specification, out ushort fileNumber)
        {
            fileNumber = 0;
            if (!TryResolve(specification, out FaFileName? name, out string? path))
            {
                return FaStoreStatus.BadName;
            }

            lock (_gate)
            {
                if (File.Exists(path))
                {
                    return FaStoreStatus.AlreadyExists;
                }

                try
                {
                    using (File.Create(path!))
                    {
                    }
                }
                catch (IOException)
                {
                    return FaStoreStatus.IoError;
                }
                catch (UnauthorizedAccessException)
                {
                    return FaStoreStatus.IoError;
                }

                fileNumber = FileNumberFor(name!);
                return FaStoreStatus.Ok;
            }
        }

        /// <summary>
        /// Deletes a file.
        /// </summary>
        /// <param name="specification">
        /// The SINTRAN file specification.
        /// </param>
        /// <returns>
        /// The outcome.
        /// </returns>
        /// <remarks>
        /// The file's number is deliberately NOT released. A number identifies a file for the life of
        /// that file and a deletion leaves a hole that is never compacted; handing the number to a
        /// later file would break any client that cached it.
        /// </remarks>
        public FaStoreStatus Delete(string specification)
        {
            if (!TryResolve(specification, out FaFileName? _, out string? path))
            {
                return FaStoreStatus.BadName;
            }

            lock (_gate)
            {
                if (!File.Exists(path))
                {
                    return FaStoreStatus.NoSuchFile;
                }

                try
                {
                    File.Delete(path!);
                }
                catch (IOException)
                {
                    return FaStoreStatus.IoError;
                }
                catch (UnauthorizedAccessException)
                {
                    return FaStoreStatus.IoError;
                }

                return FaStoreStatus.Ok;
            }
        }

        /// <summary>
        /// Opens a file for reading or writing.
        /// </summary>
        /// <param name="specification">
        /// The SINTRAN file specification.
        /// </param>
        /// <param name="forWrite">
        /// True to open for writing, false for reading.
        /// </param>
        /// <param name="handle">
        /// Receives the open-file handle.
        /// </param>
        /// <returns>
        /// The outcome.
        /// </returns>
        public FaStoreStatus Open(string specification, bool forWrite, out ushort handle)
        {
            handle = 0;
            if (!TryResolve(specification, out FaFileName? name, out string? path))
            {
                return FaStoreStatus.BadName;
            }

            lock (_gate)
            {
                if (!forWrite && !File.Exists(path))
                {
                    return FaStoreStatus.NoSuchFile;
                }

                FileStream stream;
                try
                {
                    stream = forWrite
                        ? new FileStream(path!, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read)
                        : new FileStream(path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                }
                catch (IOException)
                {
                    return FaStoreStatus.IoError;
                }
                catch (UnauthorizedAccessException)
                {
                    return FaStoreStatus.IoError;
                }

                handle = _nextHandle++;
                _open[handle] = new OpenFile(stream, forWrite, FileNumberFor(name!));
                return FaStoreStatus.Ok;
            }
        }

        /// <summary>
        /// Closes an open file.
        /// </summary>
        /// <param name="handle">
        /// The handle from <see cref="Open"/>.
        /// </param>
        /// <returns>
        /// The outcome.
        /// </returns>
        public FaStoreStatus Close(ushort handle)
        {
            lock (_gate)
            {
                if (!_open.TryGetValue(handle, out OpenFile? file))
                {
                    return FaStoreStatus.NotOpen;
                }

                file!.Stream.Dispose();
                _open.Remove(handle);
                return FaStoreStatus.Ok;
            }
        }

        /// <summary>
        /// Reads bytes from an open file at a byte offset.
        /// </summary>
        /// <param name="handle">
        /// The handle from <see cref="Open"/>.
        /// </param>
        /// <param name="offset">
        /// The byte offset to read from.
        /// </param>
        /// <param name="destination">
        /// The buffer to fill.
        /// </param>
        /// <param name="bytesRead">
        /// Receives how many bytes were read; 0 at end of file.
        /// </param>
        /// <returns>
        /// The outcome.
        /// </returns>
        public FaStoreStatus Read(ushort handle, long offset, Span<byte> destination, out int bytesRead)
        {
            bytesRead = 0;

            lock (_gate)
            {
                if (!_open.TryGetValue(handle, out OpenFile? file))
                {
                    return FaStoreStatus.NotOpen;
                }

                try
                {
                    if (offset >= file!.Stream.Length)
                    {
                        return FaStoreStatus.Ok;   // end of file: zero bytes, not an error
                    }

                    file.Stream.Position = offset;

                    // Read repeatedly: one Read is not obliged to fill the buffer even mid-file.
                    int total = 0;
                    while (total < destination.Length)
                    {
                        int n = file.Stream.Read(destination.Slice(total));
                        if (n <= 0)
                        {
                            break;
                        }

                        total += n;
                    }

                    bytesRead = total;
                    return FaStoreStatus.Ok;
                }
                catch (IOException)
                {
                    return FaStoreStatus.IoError;
                }
            }
        }

        /// <summary>
        /// Writes bytes to an open file at a byte offset.
        /// </summary>
        /// <param name="handle">
        /// The handle from <see cref="Open"/>.
        /// </param>
        /// <param name="offset">
        /// The byte offset to write at.
        /// </param>
        /// <param name="source">
        /// The bytes to write.
        /// </param>
        /// <returns>
        /// The outcome.
        /// </returns>
        public FaStoreStatus Write(ushort handle, long offset, ReadOnlySpan<byte> source)
        {
            lock (_gate)
            {
                if (!_open.TryGetValue(handle, out OpenFile? file))
                {
                    return FaStoreStatus.NotOpen;
                }

                if (!file!.ForWrite)
                {
                    return FaStoreStatus.WrongAccess;
                }

                try
                {
                    file.Stream.Position = offset;
                    file.Stream.Write(source);
                    file.Stream.Flush();
                    return FaStoreStatus.Ok;
                }
                catch (IOException)
                {
                    return FaStoreStatus.IoError;
                }
            }
        }

        /// <summary>
        /// Truncates or extends an open file to a byte length.
        /// </summary>
        /// <param name="handle">
        /// The handle from <see cref="Open"/>.
        /// </param>
        /// <param name="byteLength">
        /// The length the file is to have.
        /// </param>
        /// <returns>
        /// The outcome.
        /// </returns>
        /// <remarks>
        /// <para><b>Why a writer needs this</b></para>
        /// A COSMOS write ships WHOLE 2048-byte blocks, so the final block is padded and the file
        /// would otherwise be rounded up to a block boundary. The writer states the true length
        /// afterwards, through <c>FaSpecialFunction.SetEndOfFile</c>: the captured write says
        /// <c>0x45F0</c> = 17904 bytes for a file that arrived as nine 2048-byte blocks.
        /// <para>
        /// Extending is allowed as well as truncating, because nothing on the wire says the length
        /// can only shrink and refusing would be inventing a rule.
        /// </para>
        /// </remarks>
        public FaStoreStatus SetLength(ushort handle, long byteLength)
        {
            if (byteLength < 0)
            {
                return FaStoreStatus.BadName;
            }

            lock (_gate)
            {
                if (!_open.TryGetValue(handle, out OpenFile? file))
                {
                    return FaStoreStatus.NotOpen;
                }

                if (!file!.ForWrite)
                {
                    return FaStoreStatus.WrongAccess;
                }

                try
                {
                    file.Stream.SetLength(byteLength);
                    file.Stream.Flush();
                    return FaStoreStatus.Ok;
                }
                catch (IOException)
                {
                    return FaStoreStatus.IoError;
                }
            }
        }

        /// <summary>
        /// Gets the stable file number of a named file, assigning one if the name is new.
        /// </summary>
        /// <param name="name">
        /// The parsed name.
        /// </param>
        /// <returns>
        /// The file number.
        /// </returns>
        private ushort FileNumberFor(FaFileName name)
        {
            string key = name.ToString();
            if (_fileNumbers.TryGetValue(key, out ushort existing))
            {
                return existing;
            }

            ushort assigned = _nextFileNumber++;
            _fileNumbers[key] = assigned;
            return assigned;
        }

        /// <summary>
        /// Parses a specification and maps it to a path inside the served folder.
        /// </summary>
        /// <param name="specification">
        /// The SINTRAN file specification.
        /// </param>
        /// <param name="name">
        /// Receives the parsed name.
        /// </param>
        /// <param name="path">
        /// Receives the host path.
        /// </param>
        /// <returns>
        /// True when the name parsed AND resolved inside the served folder.
        /// </returns>
        private bool TryResolve(string? specification, out FaFileName? name, out string? path)
        {
            path = null;
            if (!FaFileName.TryParse(specification, out name) || name == null)
            {
                return false;
            }

            // A file name is a single flat name here - no directories. Anything carrying a separator
            // is refused outright rather than sanitised, because a "cleaned" traversal attempt is
            // still an attempt and should be visible as a refusal.
            if (name.Name.IndexOf('/') >= 0 || name.Name.IndexOf('\\') >= 0
                || name.Name.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                return false;
            }

            string hostName = name.Type == null ? name.Name : name.Name + "." + name.Type;
            string candidate = Path.GetFullPath(Path.Combine(_root, hostName));

            // Belt and braces: whatever the name looked like, the result must sit inside the root.
            string rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar)
                ? _root
                : _root + Path.DirectorySeparatorChar;

            if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            path = candidate;
            return true;
        }

        /// <summary>
        /// Maps a host path back to a SINTRAN name.
        /// </summary>
        /// <param name="path">
        /// The host path.
        /// </param>
        /// <returns>
        /// The name, or null when the host file name cannot be represented.
        /// </returns>
        private static FaFileName? FromHostPath(string path)
        {
            string fileName = Path.GetFileName(path);
            int dot = fileName.LastIndexOf('.');

            if (dot < 0)
            {
                return new FaFileName(null, fileName, null);
            }

            string bare = fileName.Substring(0, dot);
            string type = fileName.Substring(dot + 1);

            // A host extension longer than SINTRAN's 4-character type cannot be represented, so the
            // file is reported with its whole name and no type rather than being silently truncated
            // into a different file.
            if (bare.Length == 0 || type.Length > FaFileName.MaxTypeLength)
            {
                return new FaFileName(null, fileName, null);
            }

            return new FaFileName(null, bare, type);
        }

        /// <summary>
        /// One open file.
        /// </summary>
        private sealed class OpenFile
        {
            /// <summary>
            /// Initialises the open file.
            /// </summary>
            /// <param name="stream">
            /// The underlying stream.
            /// </param>
            /// <param name="forWrite">
            /// Whether it was opened for writing.
            /// </param>
            /// <param name="fileNumber">
            /// The file's stable number.
            /// </param>
            public OpenFile(FileStream stream, bool forWrite, ushort fileNumber)
            {
                Stream = stream;
                ForWrite = forWrite;
                FileNumber = fileNumber;
            }

            /// <summary>
            /// Gets the underlying stream.
            /// </summary>
            public FileStream Stream { get; }

            /// <summary>
            /// Gets a value indicating whether the file was opened for writing.
            /// </summary>
            public bool ForWrite { get; }

            /// <summary>
            /// Gets the file's stable number.
            /// </summary>
            public ushort FileNumber { get; }
        }
    }
}
