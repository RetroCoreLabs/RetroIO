//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.X25.Host
{
    /// <summary>
    /// The scripts that come with this package (the <c>scripts</c> folder), built into the DLL so a program published
    /// as one file can run them by name: <c>HostScripts.Get("altos-chat")</c> is <c>scripts/altos-chat.js</c>.
    /// </summary>
    public static class HostScripts
    {
        private const string Prefix = "RetroIO.Protocols.X25.Host.scripts.";

        /// <summary>The names of the built-in scripts, without <c>.js</c>.</summary>
        /// <returns>The names, sorted.</returns>
        public static List<string> Names()
        {
            string[] all = typeof(HostScripts).Assembly.GetManifestResourceNames();
            var names = new List<string>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].StartsWith(Prefix, StringComparison.Ordinal) && all[i].EndsWith(".js", StringComparison.Ordinal))
                {
                    names.Add(all[i].Substring(Prefix.Length, all[i].Length - Prefix.Length - 3));
                }
            }

            names.Sort(StringComparer.Ordinal);
            return names;
        }

        /// <summary>The text of one built-in script.</summary>
        /// <param name="name">Its name without <c>.js</c>, e.g. <c>altos-chat</c>.</param>
        /// <returns>The script, or null when there is none of that name.</returns>
        public static string? Get(string name)
        {
            ArgumentNullException.ThrowIfNull(name);
            using Stream? s = typeof(HostScripts).Assembly.GetManifestResourceStream(Prefix + name + ".js");
            if (s == null)
            {
                return null;
            }

            using var reader = new StreamReader(s);
            return reader.ReadToEnd();
        }
    }
}
