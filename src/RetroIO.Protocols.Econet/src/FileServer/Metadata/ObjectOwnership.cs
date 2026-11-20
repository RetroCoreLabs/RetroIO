//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Econet.FileServer.Sessions;

namespace RetroIO.Protocols.Econet.FileServer.Metadata;

/// <summary>
/// Works out who owns an object and whether a session is that owner.
/// </summary>
/// <remarks>
/// The owner of each object is stored in its property bag (see AcornAttributeStore). With the AcornHome flag set, the owner of everything at or below a home directory is the user whose home it is.
/// </remarks>
public static class ObjectOwnership
{
    /// <summary>
    /// The name of the system manager account, which is user entry 0.
    /// </summary>
    public const string SystemManagerName = "SYST";

    /// <summary>
    /// Gets the owner of an object, with the Acorn home rule applied when the flag is set.
    /// </summary>
    /// <param name="context">
    /// The shared file server context.
    /// </param>
    /// <param name="unixPath">
    /// The host path of the object.
    /// </param>
    /// <returns>
    /// The stored owner, or the user number whose home directory holds the object when the AcornHome flag is set and a home directory is found on the way down to the object.
    /// </returns>
    /// <remarks>
    /// The first directory with a home owner, met from the disc root down and counting the object itself, gives its home owner to the object. Without the AcornHome flag, the stored owner stands.
    /// </remarks>
    public static ushort GetOwner(FileServerContext context, string unixPath)
    {
        AcornAttributeStore.ReadOwnership(context.FileSystem, unixPath, out ushort owner, out _);
        if (!context.Configuration.AcornHome)
            return owner;

        ushort homeFound = FindHomeOwner(context, unixPath);
        return homeFound != 0 ? homeFound : owner;
    }

    /// <summary>
    /// Tells whether the session is the owner of an object with this owner.
    /// </summary>
    /// <param name="session">
    /// The session of the user.
    /// </param>
    /// <param name="owner">
    /// The owner of the object.
    /// </param>
    /// <returns>
    /// True when the user number of the session is the owner.
    /// </returns>
    /// <remarks>
    /// The user number of the user is the owner.
    /// The system manager SYST is user number 0 (a new password file is made with SYST in entry 0), and 0 is the owner an object has when nothing is stored (also an unowned root directory belongs to SYST). So an object with the default owner belongs to SYST.
    /// The user numbers here come from the account (EconetUserProfile.UserIndex) and SYST has no fixed number, so the system manager (the administrator named SYST) counts as owner of an object with the default owner.
    /// </remarks>
    public static bool IsOwner(FileServerSession session, ushort owner)
    {
        if (session.UserIndex == owner)
            return true;

        return owner == AcornAttributeStore.DefaultOwner &&
            session.IsAdmin &&
            string.Equals(session.Username, SystemManagerName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tells whether the session counts as the owner of an object with this owner, which a system user always does.
    /// </summary>
    /// <param name="session">
    /// The session of the user.
    /// </param>
    /// <param name="owner">
    /// The owner of the object.
    /// </param>
    /// <returns>
    /// True when the user has system privilege or is the owner.
    /// </returns>
    public static bool IsEffectiveOwner(FileServerSession session, ushort owner)
    {
        return session.IsAdmin || IsOwner(session, owner);
    }

    /// <summary>
    /// Makes the session the owner of an object, keeping the home owner it has.
    /// </summary>
    /// <param name="context">
    /// The shared file server context.
    /// </param>
    /// <param name="session">
    /// The session of the user who made the object.
    /// </param>
    /// <param name="unixPath">
    /// The host path of the object.
    /// </param>
    public static void SetOwner(FileServerContext context, FileServerSession session, string unixPath)
    {
        AcornAttributeStore.ReadOwnership(context.FileSystem, unixPath, out _, out ushort homeOf);
        AcornAttributeStore.WriteOwnership(context.FileSystem, unixPath, session.UserIndex, homeOf);
    }

    /// <summary>
    /// Records the home directory of a user who has just logged in, when the AcornHome flag is set and the directory has no home owner.
    /// </summary>
    /// <param name="context">
    /// The shared file server context.
    /// </param>
    /// <param name="session">
    /// The session that was just created.
    /// </param>
    /// <remarks>
    /// At login, with the AcornHome flag set, a user directory whose homeof is 0 gets the user number of the user who logged in, and keeps its owner.
    /// </remarks>
    public static void ClaimHomeOnLogin(FileServerContext context, FileServerSession session)
    {
        if (!context.Configuration.AcornHome)
            return;

        string home = session.HomeDirectoryUnix;
        if (string.IsNullOrEmpty(home) || !context.FileSystem.DirectoryExists(home))
            return;

        AcornAttributeStore.ReadOwnership(context.FileSystem, home, out ushort owner, out ushort homeOf);
        if (homeOf == 0)
            AcornAttributeStore.WriteOwnership(context.FileSystem, home, owner, session.UserIndex);
    }

    /// <summary>
    /// Sets or clears the home owner of a directory, keeping its owner.
    /// </summary>
    /// <param name="context">
    /// The shared file server context.
    /// </param>
    /// <param name="unixPath">
    /// The host path of the directory.
    /// </param>
    /// <param name="homeOf">
    /// The user number whose home directory it is, 0 to clear.
    /// </param>
    /// <remarks>
    /// *SETHOME writes the new homeof with the owner it read.
    /// </remarks>
    public static void SetHomeOwner(FileServerContext context, string unixPath, ushort homeOf)
    {
        AcornAttributeStore.ReadOwnership(context.FileSystem, unixPath, out ushort owner, out _);
        AcornAttributeStore.WriteOwnership(context.FileSystem, unixPath, owner, homeOf);
    }

    /// <summary>
    /// Tells whether a host path is the root directory of a disc, which sits directly under the server root.
    /// </summary>
    /// <param name="context">
    /// The shared file server context.
    /// </param>
    /// <param name="unixPath">
    /// The host path to test.
    /// </param>
    /// <returns>
    /// True when the parent of the path is the server root directory.
    /// </returns>
    public static bool IsDiscRoot(FileServerContext context, string unixPath)
    {
        string parent = ParentOf(unixPath);
        return parent.Length > 0 &&
            string.Equals(parent, context.RootDirectory.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);
    }

    // Walks from the disc root down to the object (the disc root is the child of the server root) and returns the home owner of the first directory that has one.
    private static ushort FindHomeOwner(FileServerContext context, string unixPath)
    {
        string root = context.RootDirectory.TrimEnd('/', '\\');
        List<string> chain = new List<string>();
        string current = unixPath.TrimEnd('/', '\\');
        while (current.Length > root.Length &&
            current.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
            (current[root.Length] == '/' || current[root.Length] == '\\'))
        {
            chain.Add(current);
            current = ParentOf(current);
        }

        for (int i = chain.Count - 1; i >= 0; i--)
        {
            AcornAttributeStore.ReadOwnership(context.FileSystem, chain[i], out _, out ushort homeOf);
            if (homeOf != 0)
                return homeOf;
        }

        return 0;
    }

    private static string ParentOf(string path)
    {
        string trimmed = path.TrimEnd('/', '\\');
        int cut = trimmed.LastIndexOfAny(['/', '\\']);
        return cut <= 0 ? string.Empty : trimmed.Substring(0, cut);
    }
}
