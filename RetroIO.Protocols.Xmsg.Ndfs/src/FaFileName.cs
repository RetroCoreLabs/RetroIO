using System;

namespace RetroIO.Protocols.Xmsg.Ndfs
{
    /// <summary>
    /// A SINTRAN file name as a remote client sends it: <c>(USER)NAME:TYPE</c>, where the user and
    /// the type are optional.
    /// </summary>
    /// <remarks>
    /// <para>
    /// File requests cross the wire BY NAME, not by file number, so this is the identifier the file
    /// server actually has to understand. (A file number is a stable handle for a file that already
    /// exists; it is not how a file is asked for.)
    /// </para>
    /// <para>
    /// The type is up to FOUR characters. That is not a guess: an NDFS object entry stores the type
    /// as the 4 ASCII characters at bytes 18-21. Bytes 32-33 of the same entry are the LAST
    /// RESERVING USER and were long mistaken for a type code - do not reintroduce that.
    /// </para>
    /// </remarks>
    public sealed class FaFileName
    {
        /// <summary>
        /// Longest file type SINTRAN stores (4 ASCII characters in the object entry).
        /// </summary>
        public const int MaxTypeLength = 4;

        /// <summary>
        /// Initialises a parsed name.
        /// </summary>
        /// <param name="user">
        /// The user part without brackets, or null when the client gave none.
        /// </param>
        /// <param name="name">
        /// The bare file name.
        /// </param>
        /// <param name="type">
        /// The file type without the colon, or null when the client gave none.
        /// </param>
        public FaFileName(string? user, string name, string? type)
        {
            User = user;
            Name = name;
            Type = type;
        }

        /// <summary>
        /// Gets the user part without brackets, or null when none was given.
        /// </summary>
        public string? User { get; }

        /// <summary>
        /// Gets the bare file name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the file type without the colon, or null when none was given.
        /// </summary>
        public string? Type { get; }

        /// <summary>
        /// Parses a SINTRAN file specification.
        /// </summary>
        /// <param name="specification">
        /// The specification, for example <c>(SYSTEM)MYFILE:DATA</c>, <c>MYFILE:DATA</c> or
        /// <c>MYFILE</c>. Surrounding quotes and whitespace are stripped.
        /// </param>
        /// <param name="parsed">
        /// Receives the parsed name when parsing succeeds.
        /// </param>
        /// <returns>
        /// True when the specification could be parsed.
        /// </returns>
        /// <remarks>
        /// When a specification is quoted, the quotes wrap the WHOLE thing including the user part -
        /// <c>"(USER)NAME:TYPE"</c>, never <c>("USER")NAME</c>. That is why the quotes are stripped
        /// before anything else is looked at.
        /// </remarks>
        public static bool TryParse(string? specification, out FaFileName? parsed)
        {
            parsed = null;
            if (string.IsNullOrWhiteSpace(specification))
            {
                return false;
            }

            string s = specification.Trim();

            // The quotes wrap the ENTIRE specification, user part included.
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
            {
                s = s.Substring(1, s.Length - 2).Trim();
            }

            if (s.Length == 0)
            {
                return false;
            }

            string? user = null;
            if (s[0] == '(')
            {
                int close = s.IndexOf(')');
                if (close < 0)
                {
                    return false;
                }

                user = s.Substring(1, close - 1).Trim();
                s = s.Substring(close + 1).Trim();
                if (user.Length == 0 || s.Length == 0)
                {
                    return false;
                }
            }

            string? type = null;
            int colon = s.IndexOf(':');
            if (colon >= 0)
            {
                type = s.Substring(colon + 1).Trim();
                s = s.Substring(0, colon).Trim();

                if (type.Length > MaxTypeLength)
                {
                    return false;
                }

                if (type.Length == 0)
                {
                    type = null;
                }
            }

            if (s.Length == 0)
            {
                return false;
            }

            parsed = new FaFileName(user, s, type);
            return true;
        }

        /// <summary>
        /// Formats the name back into a SINTRAN specification.
        /// </summary>
        /// <returns>
        /// The specification.
        /// </returns>
        public override string ToString()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(32);
            if (User != null)
            {
                sb.Append('(').Append(User).Append(')');
            }

            sb.Append(Name);
            if (Type != null)
            {
                sb.Append(':').Append(Type);
            }

            return sb.ToString();
        }
    }
}
