//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Threading;
using System.Threading.Tasks;
using RetroIO.Security.Authentication;

namespace RetroIO.Security.Authorization;

/// <summary>
/// File system operation enumeration
/// </summary>
public enum FileSystemOperation : byte
{
    /// <summary>Read file or directory metadata</summary>
    ReadMetadata = 0,

    /// <summary>Read file contents</summary>
    ReadFile = 1,

    /// <summary>Write to file</summary>
    WriteFile = 2,

    /// <summary>Create new file</summary>
    CreateFile = 3,

    /// <summary>Delete file</summary>
    DeleteFile = 4,

    /// <summary>Create directory</summary>
    CreateDirectory = 5,

    /// <summary>Delete directory</summary>
    DeleteDirectory = 6,

    /// <summary>Rename file or directory</summary>
    Rename = 7,

    /// <summary>List directory contents</summary>
    ListDirectory = 8,

    /// <summary>Execute file (if applicable)</summary>
    Execute = 9,

    /// <summary>Mount filesystem</summary>
    Mount = 10,

    /// <summary>Unmount filesystem</summary>
    Unmount = 11
}

/// <summary>
/// Path permissions flags
/// </summary>
[Flags]
public enum PathPermissions : ushort
{
    /// <summary>No permissions</summary>
    None = 0,

    /// <summary>Read file contents</summary>
    Read = 1 << 0,

    /// <summary>Write to file</summary>
    Write = 1 << 1,

    /// <summary>Delete file or directory</summary>
    Delete = 1 << 2,

    /// <summary>Create files or directories</summary>
    Create = 1 << 3,

    /// <summary>List directory contents</summary>
    List = 1 << 4,

    /// <summary>Execute file (if applicable)</summary>
    Execute = 1 << 5,

    /// <summary>Modify permissions or ownership</summary>
    ChangePermissions = 1 << 6,

    /// <summary>Read file or directory metadata</summary>
    ReadMetadata = 1 << 7,

    /// <summary>Write file or directory metadata</summary>
    WriteMetadata = 1 << 8,

    /// <summary>Full control (all permissions)</summary>
    FullControl = Read | Write | Delete | Create | List | Execute | ChangePermissions | ReadMetadata | WriteMetadata
}

/// <summary>
/// ACL inheritance flags
/// </summary>
[Flags]
public enum AclInheritanceFlags : byte
{
    /// <summary>No inheritance</summary>
    None = 0,

    /// <summary>Apply to files in this directory</summary>
    ApplyToFiles = 1 << 0,

    /// <summary>Apply to subdirectories</summary>
    ApplyToDirectories = 1 << 1,

    /// <summary>Inherit to child directories</summary>
    InheritToChildren = 1 << 2,

    /// <summary>Apply only to children, not this object</summary>
    InheritOnly = 1 << 3
}

/// <summary>
/// Access control entry for ACL
/// </summary>
public sealed class AccessControlEntry
{
    /// <summary>Principal identifier (user ID, role name, etc.)</summary>
    public string PrincipalIdentifier { get; set; } = string.Empty;

    /// <summary>Whether this is a role or user identifier</summary>
    public bool IsRole { get; set; }

    /// <summary>Allowed permissions</summary>
    public PathPermissions AllowedPermissions { get; set; }

    /// <summary>Denied permissions (takes precedence over allowed)</summary>
    public PathPermissions DeniedPermissions { get; set; }

    /// <summary>Inheritance flags</summary>
    public AclInheritanceFlags InheritanceFlags { get; set; }

    /// <summary>ACE priority (higher priority wins in conflicts)</summary>
    public int Priority { get; set; }
}

/// <summary>
/// Authorization request for a specific operation
/// </summary>
public sealed class AuthorizationRequest
{
    /// <summary>File system operation being requested</summary>
    public FileSystemOperation Operation { get; set; }

    /// <summary>Target path</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Destination path (for rename operations)</summary>
    public string? DestinationPath { get; set; }

    /// <summary>Additional operation flags</summary>
    public uint Flags { get; set; }

    /// <summary>Client IP address</summary>
    public string? ClientIpAddress { get; set; }
}

/// <summary>
/// Authorization result
/// </summary>
public sealed class AuthorizationResult
{
    /// <summary>Whether operation is authorized</summary>
    public bool Allowed { get; set; }

    /// <summary>Denial reason (if not allowed)</summary>
    public string? DenialReason { get; set; }

    /// <summary>Effective permissions for the path</summary>
    public PathPermissions EffectivePermissions { get; set; }

    /// <summary>Create allowed result</summary>
    public static AuthorizationResult Allow(PathPermissions effectivePermissions)
    {
        return new AuthorizationResult
        {
            Allowed = true,
            EffectivePermissions = effectivePermissions
        };
    }

    /// <summary>Create denied result</summary>
    public static AuthorizationResult Deny(string reason)
    {
        return new AuthorizationResult
        {
            Allowed = false,
            DenialReason = reason,
            EffectivePermissions = PathPermissions.None
        };
    }
}

/// <summary>
/// Authorization provider interface - implement to provide custom authorization
/// </summary>
public interface IAuthorizationProvider
{
    /// <summary>
    /// Authorize a file system operation
    /// </summary>
    /// <param name="principal">Security principal requesting the operation</param>
    /// <param name="request">Authorization request with operation details</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Authorization result indicating whether operation is allowed</returns>
    ValueTask<AuthorizationResult> AuthorizeAsync(
        SecurityPrincipal principal,
        AuthorizationRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Get effective permissions for a path
    /// </summary>
    /// <param name="principal">Security principal</param>
    /// <param name="path">Target path</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Effective permissions for the principal on the path</returns>
    ValueTask<PathPermissions> GetEffectivePermissionsAsync(
        SecurityPrincipal principal,
        string path,
        CancellationToken ct = default);

    /// <summary>
    /// Get ACL entries for a path
    /// </summary>
    /// <param name="path">Target path</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Array of ACL entries</returns>
    ValueTask<AccessControlEntry[]> GetAclAsync(
        string path,
        CancellationToken ct = default);

    /// <summary>
    /// Set ACL entries for a path
    /// </summary>
    /// <param name="path">Target path</param>
    /// <param name="entries">ACL entries to set</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if ACL was updated, false otherwise</returns>
    ValueTask<bool> SetAclAsync(
        string path,
        AccessControlEntry[] entries,
        CancellationToken ct = default);
}
