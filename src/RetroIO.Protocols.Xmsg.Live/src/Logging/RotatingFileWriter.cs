//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using System.Text;

namespace RetroIO.Protocols.Xmsg.Live.Logging
{
    /// <summary>
    /// A <see cref="TextWriter"/> that appends to a log file and rotates it both once on construction (so
    /// every process restart begins a fresh empty file) and whenever the file grows past a size limit.
    /// </summary>
    /// <remarks>
    /// Rotation is delegated to <see cref="LogRotator"/> (Linux-syslog numbering). The byte counter is an
    /// approximation used only to decide when to roll over - it counts one byte per <see cref="char"/> and
    /// the exact UTF-8 length for string writes; log lines are ASCII, so the estimate is effectively exact.
    /// The writer flushes on every newline so a crash still leaves a complete log up to the last line.
    /// </remarks>
    public sealed class RotatingFileWriter : TextWriter
    {
        private readonly string _path;
        private readonly long _maxBytes;
        private readonly int _keep;
        private readonly Encoding _encoding;
        private StreamWriter _writer;
        private long _bytes;

        /// <summary>
        /// Opens a rotating writer, rotating any existing file out of the way first so the run starts empty.
        /// </summary>
        /// <param name="path">
        /// The live log file path.
        /// </param>
        /// <param name="maxBytes">
        /// The size in bytes at which the file is rotated; must be positive.
        /// </param>
        /// <param name="keep">
        /// The number of archived versions to retain.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="maxBytes"/> is not positive.
        /// </exception>
        public RotatingFileWriter(string path, long maxBytes, int keep)
        {
            if (maxBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBytes), "Maximum log size must be positive.");
            }

            _path = path;
            _maxBytes = maxBytes;
            _keep = keep;
            _encoding = new UTF8Encoding(false); // no BOM

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Startup rotation (user requirement): a new run immediately renames the old file and
            // starts a fresh empty one.
            LogRotator.Rotate(_path, _keep);
            _writer = OpenFresh();
        }

        /// <summary>
        /// Gets the character encoding the file is written with (UTF-8 without a byte-order mark).
        /// </summary>
        public override Encoding Encoding
        {
            get { return _encoding; }
        }

        /// <summary>
        /// Gets the full path of the live log file.
        /// </summary>
        public string FilePath
        {
            get { return _path; }
        }

        /// <summary>
        /// Writes a single character, flushing and rotating when a newline crosses the size limit.
        /// </summary>
        /// <param name="value">
        /// The character to write.
        /// </param>
        public override void Write(char value)
        {
            _writer.Write(value);
            _bytes += 1;
            if (value == '\n')
            {
                _writer.Flush();
                if (_bytes >= _maxBytes)
                {
                    RollOver();
                }
            }
        }

        /// <summary>
        /// Writes a string, flushing and rotating when it contains a newline and the size limit is crossed.
        /// </summary>
        /// <param name="value">
        /// The string to write; a null value writes nothing.
        /// </param>
        public override void Write(string? value)
        {
            if (value == null)
            {
                return;
            }

            _writer.Write(value);
            _bytes += _encoding.GetByteCount(value);
            if (value.IndexOf('\n') >= 0)
            {
                _writer.Flush();
                if (_bytes >= _maxBytes)
                {
                    RollOver();
                }
            }
        }

        /// <summary>
        /// Flushes any buffered output to the file.
        /// </summary>
        public override void Flush()
        {
            _writer.Flush();
        }

        /// <summary>
        /// Closes the current file, rotates the numbered set, and opens a fresh empty file.
        /// </summary>
        private void RollOver()
        {
            _writer.Flush();
            _writer.Dispose();
            LogRotator.Rotate(_path, _keep);
            _writer = OpenFresh();
        }

        /// <summary>
        /// Opens a new empty live file (truncating any leftover) and resets the byte counter.
        /// </summary>
        /// <returns>
        /// A writer over the fresh file.
        /// </returns>
        private StreamWriter OpenFresh()
        {
            FileStream fs = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read);
            StreamWriter sw = new StreamWriter(fs, _encoding);
            _bytes = 0;
            return sw;
        }

        /// <summary>
        /// Flushes and disposes the underlying file writer.
        /// </summary>
        /// <param name="disposing">
        /// <c>true</c> when called from <see cref="IDisposable.Dispose"/>.
        /// </param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _writer.Flush();
                _writer.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
