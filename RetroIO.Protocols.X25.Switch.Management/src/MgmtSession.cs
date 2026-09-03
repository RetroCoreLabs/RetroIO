using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.X25.Switch.Management
{
    /// <summary>
    /// The authentication state of a management session.
    /// </summary>
    public enum MgmtAuthState
    {
        /// <summary>
        /// Awaiting the username.
        /// </summary>
        Username = 0,

        /// <summary>
        /// Awaiting the password.
        /// </summary>
        Password = 1,

        /// <summary>
        /// Authenticated; commands are accepted.
        /// </summary>
        Authenticated = 2
    }

    /// <summary>
    /// The output produced by feeding a line to a <see cref="MgmtSession"/>.
    /// </summary>
    public readonly struct MgmtResult
    {
        /// <summary>
        /// Bytes to send back to the client (may be empty).
        /// </summary>
        public string Output { get; }

        /// <summary>
        /// True if the output should be sent with the qualifier bit set (machine API response); false for
        /// plain text (banner, auth, human command output).
        /// </summary>
        public bool QBit { get; }

        /// <summary>
        /// True if the session should be cleared (the client asked to quit).
        /// </summary>
        public bool Clear { get; }

        /// <summary>
        /// Creates a session result.
        /// </summary>
        /// <param name="output">
        /// Bytes to send back.
        /// </param>
        /// <param name="qBit">
        /// Whether to send with the qualifier bit set.
        /// </param>
        /// <param name="clear">
        /// Whether to clear the session.
        /// </param>
        public MgmtResult(string output, bool qBit, bool clear)
        {
            Output = output;
            QBit = qBit;
            Clear = clear;
        }
    }

    /// <summary>
    /// One authenticated management session over an X.25 circuit: it runs the login handshake, then routes
    /// plain-text lines to the command CLI and qualifier-bit lines to the machine API, and it can enter a
    /// monitor mode that receives asynchronous events.
    /// </summary>
    public sealed class MgmtSession
    {
        /// <summary>
        /// The parameter-setting bytes sent (with the qualifier bit) ahead of the login banner.
        /// </summary>
        public static readonly byte[] ParameterSet = { 0x02, 0x02, 0x00, 0x03, 0x02, 0x04, 0x01 };

        private readonly SwitchCli _cli;
        private readonly SwitchApiProcessor _api;
        private readonly IReadOnlyList<UserConfig> _users;
        private readonly string _switchName;
        private readonly string _dnic;

        private string _username = string.Empty;

        /// <summary>
        /// The current authentication state.
        /// </summary>
        public MgmtAuthState State { get; private set; } = MgmtAuthState.Username;

        /// <summary>
        /// True once the session has subscribed to asynchronous events.
        /// </summary>
        public bool Subscribed { get; private set; }

        /// <summary>
        /// True once the session has entered monitor mode.
        /// </summary>
        public bool Monitoring { get; private set; }

        /// <summary>
        /// Creates a management session.
        /// </summary>
        /// <param name="cli">
        /// The text command processor.
        /// </param>
        /// <param name="api">
        /// The machine API processor.
        /// </param>
        /// <param name="users">
        /// The accounts accepted for login.
        /// </param>
        /// <param name="switchName">
        /// The switch name shown in the banner (blank falls back to a default).
        /// </param>
        /// <param name="dnic">
        /// The switch DNIC shown in the banner.
        /// </param>
        public MgmtSession(SwitchCli cli, SwitchApiProcessor api, IReadOnlyList<UserConfig> users, string switchName, string dnic)
        {
            _cli = cli ?? throw new ArgumentNullException(nameof(cli));
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _users = users ?? throw new ArgumentNullException(nameof(users));
            _switchName = switchName;
            _dnic = dnic;
        }

        /// <summary>
        /// Builds the login banner sent after the session is accepted.
        /// </summary>
        /// <returns>
        /// The banner text ending with the username prompt.
        /// </returns>
        public string Banner()
        {
            string name = string.IsNullOrEmpty(_switchName) ? "Switch" : _switchName;
            return name + " (DNIC " + _dnic + ") - X.25 Switch Management\r\nUsername: ";
        }

        /// <summary>
        /// Processes one completed line from the client.
        /// </summary>
        /// <param name="line">
        /// The line, with its terminator removed.
        /// </param>
        /// <param name="qBit">
        /// Whether the line arrived with the qualifier bit set (a machine API request).
        /// </param>
        /// <returns>
        /// What to send back and whether to clear the session.
        /// </returns>
        public MgmtResult FeedLine(string line, bool qBit)
        {
            if (State != MgmtAuthState.Authenticated)
            {
                if (qBit)
                {
                    // Qualifier-bit data before authentication is ignored.
                    return new MgmtResult(string.Empty, false, false);
                }

                if (State == MgmtAuthState.Username)
                {
                    _username = line;
                    State = MgmtAuthState.Password;
                    return new MgmtResult("\r\nPassword: ", false, false);
                }

                // Password stage.
                if (Validate(_username, line))
                {
                    State = MgmtAuthState.Authenticated;
                    return new MgmtResult("\r\nWelcome, " + _username + ".\r\n> ", false, false);
                }

                _username = string.Empty;
                State = MgmtAuthState.Username;
                return new MgmtResult("\r\nLogin incorrect.\r\nUsername: ", false, false);
            }

            if (qBit)
            {
                string apiResponse = _api.Process(line);
                if (line == "SUBSCRIBE")
                {
                    Subscribed = true;
                }
                else if (line == "UNSUBSCRIBE")
                {
                    Subscribed = false;
                }

                return new MgmtResult(apiResponse, true, false);
            }

            if (line == "quit" || line == "exit")
            {
                return new MgmtResult(string.Empty, false, true);
            }

            if (line == "monitor events")
            {
                Monitoring = true;
                return new MgmtResult(string.Empty, false, false);
            }

            return new MgmtResult(_cli.Process(line) + "> ", false, false);
        }

        private bool Validate(string username, string password)
        {
            for (int i = 0; i < _users.Count; i++)
            {
                if (_users[i].Name == username && _users[i].Password == password)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
