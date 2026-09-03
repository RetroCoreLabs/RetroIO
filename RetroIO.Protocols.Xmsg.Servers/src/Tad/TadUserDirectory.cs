using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.Servers.Tad
{
    /// <summary>
    /// A TAD login account: a username and an optional password.
    /// </summary>
    /// <remarks>
    /// A user with an empty (or null) password does NOT require a password at login - after the
    /// username is entered the session logs straight in. A user with a non-empty password must enter
    /// it. Usernames are compared case-insensitively.
    /// </remarks>
    public sealed class TadUser
    {
        /// <summary>
        /// Initialises a user account.
        /// </summary>
        /// <param name="username">
        /// The login name (compared case-insensitively; must be non-empty).
        /// </param>
        /// <param name="password">
        /// The password, or null/empty for a passwordless account.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="username"/> is null or empty.
        /// </exception>
        public TadUser(string username, string? password)
        {
            if (string.IsNullOrEmpty(username))
            {
                throw new ArgumentException("Username must be non-empty.", nameof(username));
            }

            Username = username;
            Password = password ?? string.Empty;
        }

        /// <summary>
        /// Gets the login name.
        /// </summary>
        public string Username { get; }

        /// <summary>
        /// Gets the password (empty for a passwordless account).
        /// </summary>
        public string Password { get; }

        /// <summary>
        /// Gets a value indicating whether this account requires a password at login.
        /// </summary>
        public bool RequiresPassword
        {
            get { return Password.Length != 0; }
        }
    }

    /// <summary>
    /// The set of TAD login accounts the terminal server accepts, with case-insensitive lookup.
    /// </summary>
    /// <remarks>
    /// The live runner builds this from the <c>tadUsers</c> section of the topology config; tests and
    /// the default constructor use a single <c>SYSTEM</c>/<c>SYSTEM</c> account.
    /// </remarks>
    public sealed class TadUserDirectory
    {
        private readonly Dictionary<string, TadUser> _byName;

        /// <summary>
        /// Initialises the directory from a set of accounts.
        /// </summary>
        /// <param name="users">
        /// The accounts. When null or empty, a single <c>SYSTEM</c>/<c>SYSTEM</c> account is used so
        /// the server always has at least one login.
        /// </param>
        public TadUserDirectory(IReadOnlyList<TadUser>? users)
        {
            _byName = new Dictionary<string, TadUser>(StringComparer.OrdinalIgnoreCase);

            if (users == null || users.Count == 0)
            {
                TadUser fallback = new TadUser("SYSTEM", "SYSTEM");
                _byName[fallback.Username] = fallback;
                return;
            }

            for (int i = 0; i < users.Count; i++)
            {
                TadUser u = users[i];
                // Last definition wins on a duplicate username (config author's intent).
                _byName[u.Username] = u;
            }
        }

        /// <summary>
        /// Initialises the default directory: a single <c>SYSTEM</c>/<c>SYSTEM</c> account.
        /// </summary>
        public TadUserDirectory()
            : this(null)
        {
        }

        /// <summary>
        /// Gets the number of accounts.
        /// </summary>
        public int Count
        {
            get { return _byName.Count; }
        }

        /// <summary>
        /// Looks up an account by username (case-insensitive).
        /// </summary>
        /// <param name="username">
        /// The username to find.
        /// </param>
        /// <param name="user">
        /// On success, the matching account.
        /// </param>
        /// <returns>
        /// True when an account with that username exists.
        /// </returns>
        public bool TryGet(string username, out TadUser user)
        {
            if (username != null && _byName.TryGetValue(username, out TadUser? found))
            {
                user = found;
                return true;
            }

            user = null!;
            return false;
        }
    }
}
