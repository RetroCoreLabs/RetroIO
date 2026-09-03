using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace RetroIO.Protocols.Xmsg.Node
{
    /// <summary>
    /// A file-backed <see cref="ILinkSeedStore"/>: one line per remote node,
    /// <c>nodeDecimal=seedHex</c>, for example <c>100=14</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately a SEPARATE file from the datagram-sequence state. The two answer different
    /// questions and go wrong in different ways: a wrong sequence is refused loudly with a XENSE
    /// and recovers, while a wrong seed produces a Counter the peer cannot make sense of. Keeping
    /// them apart means one can be thrown away without disturbing the other.
    /// </remarks>
    public sealed class FileLinkSeedStore : ILinkSeedStore
    {
        private readonly string _path;
        private readonly Dictionary<ushort, byte> _seeds;

        /// <summary>
        /// Opens (and loads) the store at the given path.
        /// </summary>
        /// <param name="path">
        /// The state file path.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="path"/> is null.
        /// </exception>
        public FileLinkSeedStore(string path)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _seeds = new Dictionary<ushort, byte>();
            Load();
        }

        /// <inheritdoc />
        public bool TryLoadSeed(ushort remoteNode, out byte seed)
        {
            return _seeds.TryGetValue(remoteNode, out seed);
        }

        /// <inheritdoc />
        public void SaveSeed(ushort remoteNode, byte seed)
        {
            byte existing;
            if (_seeds.TryGetValue(remoteNode, out existing) && existing == seed)
            {
                return;
            }

            _seeds[remoteNode] = seed;
            Persist();
        }

        /// <summary>
        /// Reads the file into the map; tolerant of a missing or garbled file.
        /// </summary>
        private void Load()
        {
            if (!File.Exists(_path))
            {
                return;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(_path);
            }
            catch (IOException)
            {
                return;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0 || eq >= line.Length - 1)
                {
                    continue;
                }

                ushort node;
                byte seed;
                if (ushort.TryParse(line.Substring(0, eq).Trim(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out node)
                    && byte.TryParse(line.Substring(eq + 1).Trim(), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out seed))
                {
                    _seeds[node] = seed;
                }
            }
        }

        /// <summary>
        /// Rewrites the whole file from the map.
        /// </summary>
        private void Persist()
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            List<string> lines = new List<string>(_seeds.Count);
            Dictionary<ushort, byte>.Enumerator e = _seeds.GetEnumerator();
            while (e.MoveNext())
            {
                KeyValuePair<ushort, byte> entry = e.Current;
                lines.Add(entry.Key.ToString(CultureInfo.InvariantCulture)
                    + "=" + entry.Value.ToString("X2", CultureInfo.InvariantCulture));
            }

            try
            {
                File.WriteAllLines(_path, lines);
            }
            catch (IOException)
            {
                // Best-effort; a transient write failure must not crash the live node.
            }
        }
    }
}
