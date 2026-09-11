//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Protocols.Econet.Common;
using RetroIO.Identity;

namespace RetroIO.Protocols.Econet.FileServer.Users;

/// <summary>
/// Resolves Econet logins against RetroIDP and exposes Acorn profile data.
/// <c>*I AM</c> uses the RetroIDP <see cref="RetroUser.Username"/> (case-insensitive).
/// </summary>
public sealed class EconetUserDirectory
{
    private readonly IRetroIDP _idp;

    public EconetUserDirectory(IRetroIDP idp)
    {
        _idp = idp ?? throw new ArgumentNullException(nameof(idp));
    }

    public ProtocolAuthResult TryAuthenticate(
        string loginUsername,
        string acornPassword,
        out RetroUser? user,
        out EconetUserProfile? profile)
    {
        user = null;
        profile = null;

        string paddedUsername = AcornPath.PadUsername(loginUsername.Trim());
        RetroUser? resolvedUser = FindUserByUsername(paddedUsername);
        if (resolvedUser == null)
            return ProtocolAuthResult.UnknownUser;

        if (!resolvedUser.IsActive)
        {
            RetroUserProtocolAuth.RecordFailedLogin(resolvedUser);
            PersistUser(resolvedUser);
            user = resolvedUser;
            return ProtocolAuthResult.Inactive;
        }

        if (!VerifyCredentials(resolvedUser, acornPassword))
        {
            RetroUserProtocolAuth.RecordFailedLogin(resolvedUser);
            PersistUser(resolvedUser);
            user = resolvedUser;
            return ProtocolAuthResult.WrongPassword;
        }

        if (!resolvedUser.EffectiveAllowEconet)
        {
            RetroUserProtocolAuth.RecordFailedLogin(resolvedUser);
            PersistUser(resolvedUser);
            user = resolvedUser;
            return ProtocolAuthResult.ProtocolNotAllowed;
        }

        RetroUserProtocolAuth.RecordSuccessfulLogin(resolvedUser, RetroFsProtocol.Econet);
        PersistUser(resolvedUser);
        user = resolvedUser;
        profile = EconetUserProfile.FromRetroUser(resolvedUser);
        return ProtocolAuthResult.Success;
    }

    public EconetUserProfile? GetProfile(Guid userId)
    {
        RetroUser? user = _idp.GetUserAsync(userId).AsTask().GetAwaiter().GetResult();
        if (user == null || !user.IsActive)
            return null;

        return EconetUserProfile.FromRetroUser(user);
    }

    /// <summary>
    /// Persist a user's boot option (PiFS fs_set_bootopt writes it to the user record), so it
    /// survives logout/login and is reported on the next login handshake.
    /// </summary>
    public bool TrySetBootOpt(Guid userId, byte bootOpt)
    {
        RetroUser? user = _idp.GetUserAsync(userId).AsTask().GetAwaiter().GetResult();
        if (user == null)
            return false;

        user.Metadata[EconetMetadataKeys.BootOpt] = bootOpt.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PersistUser(user);
        return true;
    }

    /// <summary>
    /// Persist a user's home (URD) directory as an Acorn path string, so <c>*SETHOME</c> takes effect
    /// at the next login (login reads it back via <see cref="EconetUserProfile.Home"/>). Metadata write
    /// only — no wire behaviour changes until the login path resolves it.
    /// </summary>
    public bool TrySetHome(Guid userId, string acornHome)
    {
        RetroUser? user = _idp.GetUserAsync(userId).AsTask().GetAwaiter().GetResult();
        if (user == null)
            return false;

        user.Metadata[EconetMetadataKeys.Home] = acornHome;
        PersistUser(user);
        return true;
    }

    /// <summary>
    /// Persist a user's library directory as an Acorn path string, so <c>*SETLIB</c> takes effect at
    /// the next login (login reads it back via <see cref="EconetUserProfile.Lib"/>). Metadata write
    /// only — no wire behaviour changes until the login path resolves it.
    /// </summary>
    public bool TrySetLib(Guid userId, string acornLib)
    {
        RetroUser? user = _idp.GetUserAsync(userId).AsTask().GetAwaiter().GetResult();
        if (user == null)
            return false;

        user.Metadata[EconetMetadataKeys.Lib] = acornLib;
        PersistUser(user);
        return true;
    }

    /// <summary>
    /// Persist a user's visible-disc bitmap for <c>*DISCMASK</c>. Stored as a decimal string (the form
    /// <see cref="EconetUserProfile"/> reads back and <c>DiscCatalog.IsDiscVisible</c> consults at
    /// login). Metadata write only.
    /// </summary>
    public bool TrySetDiscMask(Guid userId, ushort discMask)
    {
        RetroUser? user = _idp.GetUserAsync(userId).AsTask().GetAwaiter().GetResult();
        if (user == null)
            return false;

        user.Metadata[EconetMetadataKeys.DiscMask] = discMask.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PersistUser(user);
        return true;
    }

    /// <summary>
    /// Changes a user's own password after verifying the current one, for <c>*PASS</c>.
    /// </summary>
    /// <param name="userId">
    /// The user whose password is changing.
    /// </param>
    /// <param name="oldAcornPassword">
    /// The current password as typed (space-padded acceptable); an empty value means "no current
    /// password".
    /// </param>
    /// <param name="newAcornPassword">
    /// The new password; an empty value clears the password when the account permits empty passwords.
    /// </param>
    /// <returns>
    /// True on success. On failure, <paramref name="errorMessage"/> explains why.
    /// </returns>
    public bool TryChangePassword(Guid userId, string oldAcornPassword, string newAcornPassword, out string errorMessage)
    {
        errorMessage = string.Empty;

        RetroUser? user = _idp.GetUserAsync(userId).AsTask().GetAwaiter().GetResult();
        if (user == null)
        {
            errorMessage = "Who are you ?";
            return false;
        }

        // The current password must verify, using the same rules as login (including empty).
        if (!VerifyCredentials(user, oldAcornPassword))
        {
            errorMessage = "Wrong password";
            return false;
        }

        string newTrimmed = newAcornPassword.TrimEnd(' ');
        if (newTrimmed.Length == 0)
        {
            // Clearing the password is only allowed when the account permits an empty password.
            EconetUserProfile profile = EconetUserProfile.FromRetroUser(user);
            if (!profile.AllowEmptyPassword)
            {
                errorMessage = "Bad password";
                return false;
            }
        }

        _idp.SetPasswordAsync(user.UserId, newTrimmed).AsTask().GetAwaiter().GetResult();
        _idp.SaveAsync().AsTask().GetAwaiter().GetResult();
        return true;
    }

    /// <summary>
    /// Sets a user's password WITHOUT verifying the current one, for a System user's <c>*SETPASS</c>.
    /// Clearing (empty) is only allowed when the target account permits an empty password, mirroring
    /// <see cref="TryChangePassword"/>.
    /// </summary>
    /// <param name="userId">The user whose password is being set.</param>
    /// <param name="newAcornPassword">The new password; empty clears it (if permitted).</param>
    /// <param name="errorMessage">On failure, why.</param>
    /// <returns>True on success.</returns>
    public bool TrySetPassword(Guid userId, string newAcornPassword, out string errorMessage)
    {
        errorMessage = string.Empty;

        RetroUser? user = _idp.GetUserAsync(userId).AsTask().GetAwaiter().GetResult();
        if (user == null)
        {
            errorMessage = "Unknown user";
            return false;
        }

        string newTrimmed = newAcornPassword.TrimEnd(' ');
        if (newTrimmed.Length == 0)
        {
            EconetUserProfile profile = EconetUserProfile.FromRetroUser(user);
            if (!profile.AllowEmptyPassword)
            {
                errorMessage = "Bad password";
                return false;
            }
        }

        _idp.SetPasswordAsync(user.UserId, newTrimmed).AsTask().GetAwaiter().GetResult();
        _idp.SaveAsync().AsTask().GetAwaiter().GetResult();
        return true;
    }

    /// <summary>
    /// Sets a user's privilege STATE for <c>*PRIV</c>. PiFS treats System / Normal / Locked as mutually
    /// exclusive, so both flags are written together: System maps to <see cref="RetroUser.IsAdmin"/>,
    /// Locked to the <c>econet.locked</c> metadata (refused at login by
    /// <see cref="FileServerSessionManager"/>). Persisted immediately; a live session keeps its
    /// privilege until the user's next login.
    /// </summary>
    public bool TrySetPrivilege(Guid userId, bool isAdmin, bool isLocked)
    {
        RetroUser? user = _idp.GetUserAsync(userId).AsTask().GetAwaiter().GetResult();
        if (user == null)
            return false;

        user.IsAdmin = isAdmin;
        user.Metadata[EconetMetadataKeys.Locked] = isLocked ? "true" : "false";
        PersistUser(user);
        return true;
    }

    public bool UserExists(string loginUsername)
    {
        string paddedUsername = AcornPath.PadUsername(loginUsername.Trim());
        return FindUserByUsername(paddedUsername) != null;
    }

    public bool TryGetProfileByUsername(string loginUsername, out EconetUserProfile? profile)
    {
        profile = null;
        string paddedUsername = AcornPath.PadUsername(loginUsername.Trim());
        RetroUser? user = FindUserByUsername(paddedUsername);
        if (user == null)
            return false;

        profile = EconetUserProfile.FromRetroUser(user);
        return true;
    }

    private RetroUser? FindUserByUsername(string paddedUsername)
    {
        string trimmed = paddedUsername.TrimEnd();
        RetroUser? direct = _idp.GetUserByUsernameAsync(trimmed).AsTask().GetAwaiter().GetResult();
        if (direct != null)
            return direct;

        RetroUser[] users = _idp.ListUsersAsync().AsTask().GetAwaiter().GetResult();
        for (int i = 0; i < users.Length; i++)
        {
            RetroUser candidate = users[i];
            if (AcornPath.UsernamesEqual(candidate.Username, paddedUsername))
                return candidate;
        }

        return null;
    }

    private bool VerifyCredentials(RetroUser user, string acornPassword)
    {
        EconetUserProfile profile = EconetUserProfile.FromRetroUser(user);

        if (AcornPath.IsBlankPassword(acornPassword))
        {
            if (!profile.AllowEmptyPassword)
                return false;

            return string.IsNullOrEmpty(user.PasswordHash);
        }

        if (string.IsNullOrEmpty(user.PasswordHash))
            return false;

        string trimmed = acornPassword.TrimEnd(' ');
        if (_idp.VerifyPasswordAsync(user.UserId, trimmed).AsTask().GetAwaiter().GetResult())
            return true;

        string upper = trimmed.ToUpperInvariant();
        if (!string.Equals(upper, trimmed, StringComparison.Ordinal))
            return _idp.VerifyPasswordAsync(user.UserId, upper).AsTask().GetAwaiter().GetResult();

        return false;
    }

    private void PersistUser(RetroUser user)
    {
        _idp.UpdateUserAsync(user).AsTask().GetAwaiter().GetResult();
        _idp.SaveAsync().AsTask().GetAwaiter().GetResult();
    }
}
