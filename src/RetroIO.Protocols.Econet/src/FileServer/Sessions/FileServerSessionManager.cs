//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Bulk;
using RetroIO.Protocols.Econet.FileServer.Discs;
using RetroIO.Protocols.Econet.FileServer.Handles;
using RetroIO.Protocols.Econet.FileServer.Users;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Identity;
using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Protocols.Econet.FileServer.Sessions;

public sealed class FileServerSessionManager
{
    private readonly object _sync = new();
    private readonly FileServerSession?[] _sessions = new FileServerSession[256];
    private readonly EconetUserDirectory _users;
    private readonly DiscCatalog _discs;
    private readonly IFileSystemBackend _fileSystem;
    private readonly string _rootDirectory;

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

    public bool IsLoggedIn(byte net, byte station)
    {
        lock (_sync)
            return FindSession(net, station) != null;
    }

    public FileServerSession? GetSession(byte net, byte station)
    {
        lock (_sync)
            return FindSession(net, station);
    }

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

        session.CurrentDisc = disc.Index;
        session.HomeDirectoryUnix = homeUnix;
        session.CurrentDirectoryUnix = homeUnix;
        session.LibraryDirectoryUnix = homeUnix;
        return true;
    }

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

    public byte AllocateHandle()
    {
        lock (_sync)
        {
            byte handle = _nextHandle;
            _nextHandle++;
            if (_nextHandle > 32)
                _nextHandle = 4;
            return handle;
        }
    }

    private byte _nextHandle = 4;

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

    public ushort GetSessionIndex(FileServerSession session)
    {
        lock (_sync)
        {
            for (int i = 0; i < _sessions.Length; i++)
            {
                if (ReferenceEquals(_sessions[i], session))
                    return (ushort)i;
            }

            return 0;
        }
    }

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
