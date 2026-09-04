using System;

namespace RetroIO.Protocols.Xmsg.Qform
{
    /// <summary>
    /// Thrown when a QFORM body cannot be parsed.
    /// </summary>
    public sealed class QformFormatException : Exception
    {
        /// <summary>
        /// Initialises the exception.
        /// </summary>
        /// <param name="message">
        /// What went wrong.
        /// </param>
        public QformFormatException(string message)
            : base(message)
        {
        }
    }
}
