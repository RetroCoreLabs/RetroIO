//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Identity;
using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Bulk;
using RetroIO.Protocols.Econet.FileServer.Discs;
using RetroIO.Protocols.Econet.FileServer.Handles;
using RetroIO.Protocols.Econet.FileServer.Users;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Protocols.Econet.FileServer.Sessions;

/// <summary>
/// Keeps the table of logged in sessions, one per station, and handles login and logout.
/// </summary>
public sealed class FileServerSessionManager
{
    private readonly object _sync = new();
    private readonly FileServerSession?[] _sessions = new FileServerSession[256];
    private readonly EconetUserDirectory _users;
    private readonly DiscCatalog _discs;
    private readonly IFileSystemBackend _fileSystem;
    private readonly string _rootDirectory;

    /// <summary>
    /// Creates the manager.
    /// </summary>
    /// <param name="fileSystem">
    /// The file system the discs are stored on.
    /// </param>
    /// <param name="users">
    /// The user directory used to check logins.
    /// </param>
    /// <param name="discs">
    /// The disc catalogue.
    /// </param>
    /// <param name="rootDirectory">
    /// The root directory that holds the disc directories.
    /// </param>
    public FileServerSessionManager(
        IFileSystemBackend fileSystem,
        EconetUserDirectory users,
        DiscCatalog discs,
        string rootDirectory)
    {
        _fileSystem = fileSystem;
        _users = users;
        _discs = discs;
        _rootDirectory = rootDirectory;
    }

    /// <summary>
    /// Reports whether a station has a session.
    /// </summary>
    /// <param name="net">
    /// The network number.
    /// </param>
    /// <param name="station">
    /// The station number.
    /// </param>
    /// <returns>
    /// True when the station is logged in.
    /// </returns>
    public bool IsLoggedIn(byte net, byte station)
    {
        lock (_sync)
            return FindSession(net, station) != null;
    }

    /// <summary>
    /// Gets the session of a station.
    /// </summary>
    /// <param name="net">
    /// The network number.
    /// </param>
    /// <param name="station">
    /// The station number.
    /// </param>
    /// <returns>
    /// The session, or null when the station is not logged in.
    /// </returns>
    public FileServerSession? GetSession(byte net, byte station)
    {
        lock (_sync)
            return FindSession(net, station);
    }

    /// <summary>
    /// Checks a user name and password and creates the session of the station.
    /// </summary>
    /// <param name="net">
    /// The network number.
    /// </param>
    /// <param name="station">
    /// The station number.
    /// </param>
    /// <param name="username">
    /// The user name.
    /// </param>
    /// <param name="password">
    /// The password.
    /// </param>
    /// <param name="session">
    /// Receives the new session, or null on failure.
    /// </param>
    /// <param name="errorCode">
    /// Receives the file server error code on failure.
    /// </param>
    /// <param name="errorMessage">
    /// Receives the error text on failure.
    /// </param>
    /// <returns>
    /// True when the session was created. False when the station is already logged in, the session table is full, the login is refused or the account is locked.
    /// </returns>
    public bool TryLogin(byte net, byte station, string username, string password, out FileServerSession? session, out byte errorCode, out string errorMessage)
    {
        session = null;
        errorCode = FileServerErrors.WrongPassword;
        errorMessage = "Wrong password";

        lock (_sync)
        {
            if (FindSession(net, station) != null)
            {
                errorCode = FileServerErrors.BadCommand;
                errorMessage = "Already logged in";
                return false;
            }

            int activeCount = 0;
            for (int i = 0; i < _sessions.Length; i++)
            {
                if (_sessions[i] != null)
                    activeCount++;
            }

            if (activeCount >= _sessions.Length)
            {
                errorCode = FileServerErrors.TooManyUsers;
                errorMessage = "Too many users";
                return false;
            }

            ProtocolAuthResult authResult = _users.TryAuthenticate(
                username, password, out RetroUser? retroUser, out EconetUserProfile? profile);
            if (authResult != ProtocolAuthResult.Success || retroUser == null || profile == null)
            {
                switch (authResult)
                {
                    case ProtocolAuthResult.UnknownUser:
                        errorMessage = "User not known";
                        break;
                    case ProtocolAuthResult.ProtocolNotAllowed:
                        errorCode = FileServerErrors.InsufficientAccess;
                        errorMessage = "Insufficient access";
                        break;
                    case ProtocolAuthResult.Inactive:
                        errorCode = FileServerErrors.InsufficientAccess;
                        errorMessage = "Account disabled";
                        break;
                }

                return false;
            }

            // PiFS (fs.c:4560): a valid password on a LOCKED account is refused with 0xBC "Account
            // locked" — checked AFTER the password, so a wrong password still reads "Wrong password".
            // Only accounts an admin has locked via *PRIV L carry this; a default user is unaffected,
            // so the golden login bytes are unchanged.
            if (profile.IsLocked)
            {
                errorCode = FileServerErrors.WrongPassword; // 0xBC, shared with "Wrong password" in PiFS
                errorMessage = "Account locked";
                return false;
            }

            int discIndex = profile.HomeDisc;
            DiscEntry? disc = _discs.GetByIndex(discIndex);
            if (disc == null)
                disc = _discs.GetByIndex(0);

            string discPath = disc != null
                ? _fileSystem.CombinePath(_rootDirectory, disc.DirectoryName)
                : _rootDirectory;

            string homeUnix = _fileSystem.CombinePath(discPath, "USER");
            if (!_fileSystem.DirectoryExists(homeUnix))
                homeUnix = discPath;

            string discAcorn = disc?.AcornName ?? "RetroFS";
            string discRootAcorn = $":{discAcorn}.$";
            string acornHome = BuildAcornPath(discRootAcorn, discPath, homeUnix);
            string libUnix = homeUnix;
            string acornLib = acornHome;

            session = new FileServerSession
            {
                ClientNet = net,
                ClientStation = station,
                RetroUserId = retroUser.UserId,
                UserIndex = profile.UserIndex,
                Username = profile.Username.TrimEnd(),
                IsAdmin = profile.IsAdmin,
                BootOpt = profile.BootOpt,
                CurrentDisc = disc?.Index ?? 0,
                HomeDirectoryUnix = homeUnix,
                CurrentDirectoryUnix = homeUnix,
                LibraryDirectoryUnix = libUnix,
                UrdHandle = 1,
                CwdHandle = 2,
                LibHandle = 3,
                AcornHomePath = acornHome,
                AcornCwdPath = acornHome,
                AcornLibPath = acornLib,
            };

            for (int i = 0; i < _sessions.Length; i++)
            {
                if (_sessions[i] == null)
                {
                    _sessions[i] = session;
                    return true;
                }
            }
        }

        errorCode = FileServerErrors.TooManyUsers;
        errorMessage = "Too many users";
        return false;
    }

    /// <summary>
    /// Moves a session to another disc and sets its home, current and library directories to the USER directory of that disc, or to the disc root when there is no USER directory. The matching Acorn paths move with them.
    /// </summary>
    /// <param name="session">
    /// The session to change.
    /// </param>
    /// <param name="profile">
    /// The user profile whose disc mask says which discs the user may see.
    /// </param>
    /// <param name="discReference">
    /// The name or index of the disc.
    /// </param>
    /// <param name="errorCode">
    /// Receives the file server error code on failure.
    /// </param>
    /// <param name="errorMessage">
    /// Receives the error text on failure.
    /// </param>
    /// <returns>
    /// True when the disc was found and is visible to the user, otherwise false.
    /// </returns>
    public bool TryChangeDisc(
        FileServerSession session,
        EconetUserProfile profile,
        string discReference,
        out byte errorCode,
        out string errorMessage)
    {
        errorCode = FileServerErrors.BadCommand;
        errorMessage = "Bad disc name";

        if (!_discs.TryFind(discReference, out DiscEntry? disc) || disc == null)
            return false;

        if (!DiscCatalog.IsDiscVisible(profile.DiscMask, disc.Index))
            return false;

        string discPath = _fileSystem.CombinePath(_rootDirectory, disc.DirectoryName);
        string homeUnix = _fileSystem.CombinePath(discPath, "USER");
        if (!_fileSystem.DirectoryExists(homeUnix))
            homeUnix = discPath;

        // The Acorn paths are what *DIR and *LIB resolve against, so they move with the Unix paths.
        // Built the same way TryLogin builds them for a user whose home is on this disc.
        string acornHome = BuildAcornPath($":{disc.AcornName}.$", discPath, homeUnix);

        session.CurrentDisc = disc.Index;
        session.HomeDirectoryUnix = homeUnix;
        session.CurrentDirectoryUnix = homeUnix;
        session.LibraryDirectoryUnix = homeUnix;
        session.AcornHomePath = acornHome;
        session.AcornCwdPath = acornHome;
        session.AcornLibPath = acornHome;
        return true;
    }

    /// <summary>
    /// Removes the session of a station, if there is one.
    /// </summary>
    /// <param name="net">
    /// The network number.
    /// </param>
    /// <param name="station">
    /// The station number.
    /// </param>
    public void Logout(byte net, byte station)
    {
        lock (_sync)
        {
            for (int i = 0; i < _sessions.Length; i++)
            {
                FileServerSession? session = _sessions[i];
                if (session != null && session.ClientNet == net && session.ClientStation == station)
                {
                    _sessions[i] = null;
                    return;
                }
            }
        }
    }

    private FileServerSession? FindSession(byte net, byte station)
    {
        for (int i = 0; i < _sessions.Length; i++)
        {
            FileServerSession? session = _sessions[i];
            if (session != null && session.ClientNet == net && session.ClientStation == station)
                return session;
        }

        return null;
    }

    /// <summary>
    /// Counts the sessions that are logged in.
    /// </summary>
    /// <returns>
    /// The number of sessions.
    /// </returns>
    public int GetActiveSessionCount()
    {
        lock (_sync)
        {
            int count = 0;
            for (int i = 0; i < _sessions.Length; i++)
            {
                if (_sessions[i] != null)
                    count++;
            }

            return count;
        }
    }

    /// <summary>
    /// Lists the sessions a user may see. An administrator sees all sessions, anyone else sees only their own.
    /// </summary>
    /// <param name="requester">
    /// The session that asks.
    /// </param>
    /// <param name="start">
    /// The number of sessions to skip.
    /// </param>
    /// <param name="count">
    /// The largest number of sessions to return.
    /// </param>
    /// <returns>
    /// The sessions found.
    /// </returns>
    public FileServerSession[] GetLoggedOnUsers(FileServerSession requester, byte start, byte count)
    {
        lock (_sync)
        {
            List<FileServerSession> visible = new List<FileServerSession>();
            for (int i = 0; i < _sessions.Length; i++)
            {
                FileServerSession? session = _sessions[i];
                if (session == null)
                    continue;

                if (!requester.IsAdmin &&
                    (session.ClientNet != requester.ClientNet || session.ClientStation != requester.ClientStation))
                {
                    continue;
                }

                visible.Add(session);
            }

            int skip = Math.Min(start, visible.Count);
            int take = Math.Min(count, visible.Count - skip);
            FileServerSession[] result = new FileServerSession[take];
            for (int i = 0; i < take; i++)
                result[i] = visible[skip + i];

            return result;
        }
    }

    /// <summary>
    /// Gets the slot number of a session in the session table.
    /// </summary>
    /// <param name="session">
    /// The session to look up.
    /// </param>
    /// <param name="index">
    /// Receives the slot number, or 0 when the session is not in the table.
    /// </param>
    /// <returns>
    /// <c>true</c> when the session is in the table, so slot 0 can be told from "not found"; otherwise
    /// <c>false</c>.
    /// </returns>
    public bool TryGetSessionIndex(FileServerSession session, out ushort index)
    {
        lock (_sync)
        {
            for (int i = 0; i < _sessions.Length; i++)
            {
                if (ReferenceEquals(_sessions[i], session))
                {
                    index = (ushort)i;
                    return true;
                }
            }

            index = 0;
            return false;
        }
    }

    /// <summary>
    /// Logs out every session of a user, closing the open files and bulk transfers of the stations.
    /// </summary>
    /// <param name="username">
    /// The user name. Case is ignored and trailing spaces are removed.
    /// </param>
    /// <param name="handles">
    /// The open file manager.
    /// </param>
    /// <param name="bulkTransfers">
    /// The bulk transfer manager.
    /// </param>
    /// <returns>
    /// The number of sessions removed.
    /// </returns>
    public int LogoutByUsername(string username, FileServerHandleManager handles, BulkTransferManager bulkTransfers)
    {
        string padded = username.TrimEnd();
        int count = 0;
        lock (_sync)
        {
            for (int i = 0; i < _sessions.Length; i++)
            {
                FileServerSession? session = _sessions[i];
                if (session == null)
                    continue;

                if (!session.Username.TrimEnd().Equals(padded, StringComparison.OrdinalIgnoreCase))
                    continue;

                handles.CloseAllForStation(session.ClientNet, session.ClientStation);
                bulkTransfers.ReleaseStation(session.ClientNet, session.ClientStation);
                _sessions[i] = null;
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Logs out every session whose user has a given user index, closing the open files and bulk transfers of the stations.
    /// </summary>
    /// <param name="userIndex">
    /// The user index from the user profile.
    /// </param>
    /// <param name="handles">
    /// The open file manager.
    /// </param>
    /// <param name="bulkTransfers">
    /// The bulk transfer manager.
    /// </param>
    /// <returns>
    /// The number of sessions removed.
    /// </returns>
    public int LogoutByUserIndex(ushort userIndex, FileServerHandleManager handles, BulkTransferManager bulkTransfers)
    {
        int count = 0;
        lock (_sync)
        {
            for (int i = 0; i < _sessions.Length; i++)
            {
                FileServerSession? session = _sessions[i];
                if (session == null)
                    continue;

                EconetUserProfile? profile = _users.GetProfile(session.RetroUserId);
                if (profile == null || profile.UserIndex != userIndex)
                    continue;

                handles.CloseAllForStation(session.ClientNet, session.ClientStation);
                bulkTransfers.ReleaseStation(session.ClientNet, session.ClientStation);
                _sessions[i] = null;
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Gets a copy of the list of logged in sessions.
    /// </summary>
    /// <returns>
    /// The sessions.
    /// </returns>
    public FileServerSession[] GetActiveSessions()
    {
        lock (_sync)
        {
            List<FileServerSession> list = new List<FileServerSession>();
            for (int i = 0; i < _sessions.Length; i++)
            {
                FileServerSession? session = _sessions[i];
                if (session != null)
                    list.Add(session);
            }

            return list.ToArray();
        }
    }

    private static string BuildAcornPath(string discRootAcorn, string discUnixPath, string targetUnixPath)
    {
        if (string.Equals(discUnixPath, targetUnixPath, StringComparison.OrdinalIgnoreCase))
            return discRootAcorn;

        string relative = targetUnixPath.StartsWith(discUnixPath, StringComparison.OrdinalIgnoreCase)
            ? targetUnixPath.Substring(discUnixPath.Length).TrimStart('/', '\\')
            : string.Empty;

        if (string.IsNullOrEmpty(relative))
            return discRootAcorn;

        string acornTail = relative.Replace('/', '.').Replace('\\', '.');
        return AcornPath.CombineAcorn(discRootAcorn, acornTail);
    }
}
