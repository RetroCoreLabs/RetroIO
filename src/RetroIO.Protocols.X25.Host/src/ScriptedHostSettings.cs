//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.X25.Host
{
    /// <summary>
    /// Settings of one scripted machine (<see cref="ScriptedHost"/>): what the script reads as
    /// <c>machine.settings</c>, the line end, and the hard limits every call into the script runs under.
    /// </summary>
    public sealed class ScriptedHostSettings
    {
        /// <summary>
        /// The machine's own settings, given to the script as <c>machine.settings.&lt;key&gt;</c>, every value a
        /// string (the script parses numbers itself, as a host.ini value is text). Copied when the host is made.
        /// </summary>
        public Dictionary<string, string> Values { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>What each "\n" the script sends becomes on the wire. CR LF, as a PAD terminal expects.</summary>
        public string LineEnd { get; set; } = "\r\n";

        /// <summary>
        /// Most memory one call into the script may allocate, in bytes (Jint LimitMemory). A script that goes over
        /// is stopped; see <see cref="ScriptedHost"/> for what happens then.
        /// </summary>
        public long MemoryLimitBytes { get; set; } = 32L * 1024 * 1024;

        /// <summary>Longest one call into the script may run (Jint TimeoutInterval).</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>Most JavaScript statements one call into the script may run (Jint MaxStatements).</summary>
        public int MaxStatements { get; set; } = 2_000_000;

        /// <summary>Deepest function nesting a script may reach (Jint LimitRecursion).</summary>
        public int MaxRecursion { get; set; } = 256;

        /// <summary>Most timers (<c>machine.after</c>) waiting at one time; one more is refused with an error.</summary>
        public int MaxTimers { get; set; } = 10_000;
    }
}
