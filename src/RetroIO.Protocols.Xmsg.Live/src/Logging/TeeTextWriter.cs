using System.IO;
using System.Text;

namespace RetroIO.Protocols.Xmsg.Live.Logging
{
    /// <summary>
    /// A <see cref="TextWriter"/> that fans every write out to two underlying writers, for example the
    /// console and a <see cref="RotatingFileWriter"/> log file.
    /// </summary>
    public sealed class TeeTextWriter : TextWriter
    {
        private readonly TextWriter _primary;
        private readonly TextWriter _secondary;

        /// <summary>
        /// Initialises a tee over two writers.
        /// </summary>
        /// <param name="primary">
        /// The first writer (for example the console); its encoding is reported as the tee's encoding.
        /// </param>
        /// <param name="secondary">
        /// The second writer (for example the log file).
        /// </param>
        public TeeTextWriter(TextWriter primary, TextWriter secondary)
        {
            _primary = primary;
            _secondary = secondary;
        }

        /// <summary>
        /// Gets the character encoding of the primary writer.
        /// </summary>
        public override Encoding Encoding
        {
            get { return _primary.Encoding; }
        }

        /// <summary>
        /// Writes a character to both underlying writers.
        /// </summary>
        /// <param name="value">
        /// The character to write.
        /// </param>
        public override void Write(char value)
        {
            _primary.Write(value);
            _secondary.Write(value);
        }

        /// <summary>
        /// Writes a string to both underlying writers.
        /// </summary>
        /// <param name="value">
        /// The string to write.
        /// </param>
        public override void Write(string? value)
        {
            _primary.Write(value);
            _secondary.Write(value);
        }

        /// <summary>
        /// Flushes both underlying writers.
        /// </summary>
        public override void Flush()
        {
            _primary.Flush();
            _secondary.Flush();
        }
    }
}
