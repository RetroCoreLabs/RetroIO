using System;

namespace RetroIO.Protocols.Xmsg.Sync
{
    /// <summary>
    /// A local file as the planner sees it: where it is, and what is in it.
    /// </summary>
    /// <remarks>
    /// <para><b>A hash, not a timestamp</b></para>
    /// <para>
    /// The planner compares content because a timestamp cannot survive the trip: a packed ND date
    /// only spans 1950 to 2013, so a present-day stamp lands on the machine as zero. Content also
    /// answers the harder question - a file that came back from the machine looks new to a folder
    /// watcher, and only its content shows that it is the same file we sent.
    /// </para>
    /// <para>
    /// The planner never opens a file, so whoever builds these chooses how to hash. That keeps the
    /// decision testable with a byte or two standing in for a real file.
    /// </para>
    /// </remarks>
    public sealed class LocalFileState
    {
        /// <summary>
        /// Creates a local file state.
        /// </summary>
        /// <param name="path">
        /// The full path of the file.
        /// </param>
        /// <param name="hash">
        /// A hash of its content.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="path"/> or <paramref name="hash"/> is null.
        /// </exception>
        public LocalFileState(string path, byte[] hash)
        {
            if (path == null) { throw new ArgumentNullException(nameof(path)); }
            if (hash == null) { throw new ArgumentNullException(nameof(hash)); }

            Path = path;
            Hash = hash;
        }

        /// <summary>
        /// Gets the full path of the file.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// Gets the hash of its content.
        /// </summary>
        public byte[] Hash { get; }
    }
}
