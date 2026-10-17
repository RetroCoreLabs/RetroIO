//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RetroIO.Protocols.Econet.Client;
using RetroIO.Storage.Core;

namespace RetroIO.Storage.Providers
{
  /// <summary>
  /// Econet/AUN volume provider implementing <see cref="IVolumeProvider"/>.
  /// Provides access to Acorn Level-3 file servers over UDP AUN.
  /// </summary>
  public sealed class EconetVolumeProvider : IVolumeProvider
  {
    private readonly Dictionary<string, EconetMount> _mounts;
    private bool _disposed;

    /// <summary>
    /// Creates the provider with no mounted volumes.
    /// </summary>
    public EconetVolumeProvider()
    {
      _mounts = new Dictionary<string, EconetMount>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the volume type, which is econet.
    /// </summary>
    public string Type => "econet";

    /// <summary>
    /// Gets the URI schemes this provider handles, econet and aun.
    /// </summary>
    public string[] SupportedSchemes => new[] { "econet", "aun" };

    /// <summary>
    /// Gets whether the volumes are remote, which is true.
    /// </summary>
    public bool IsRemote => true;

    /// <summary>
    /// Always reports that a mount entry can be restored.
    /// </summary>
    /// <param name="entry">
    /// The mount entry, which is not checked.
    /// </param>
    /// <param name="errorMessage">
    /// Always set to null.
    /// </param>
    /// <returns>
    /// Always true.
    /// </returns>
    public bool CanRestore(UnifiedMountEntry entry, out string? errorMessage)
    {
      errorMessage = null;
      return true;
    }

    /// <summary>
    /// Mounts a volume from a mount entry.
    /// </summary>
    /// <remarks>
    /// The host, port and optional credentials are taken from the source URI. A user name or password in the URI replaces the one in the entry.
    /// </remarks>
    /// <param name="entry">
    /// The mount entry.
    /// </param>
    /// <returns>
    /// Information about the mounted volume.
    /// </returns>
    /// <exception cref="System.ArgumentException">
    /// Thrown when the host or the volume identifier is empty, or the volume identifier is already mounted.
    /// </exception>
    /// <exception cref="System.IO.IOException">
    /// Thrown when the login fails.
    /// </exception>
    public MountInfo MountFromConfig(UnifiedMountEntry entry)
    {
      ParseMountUri(
        entry.SourceUri,
        entry.Username,
        entry.Password,
        out string host,
        out int port,
        out string? username,
        out string? password);

      return Mount(host, port, entry.VolumeId, entry.IsReadOnly, username, password);
    }

    /// <summary>
    /// Mounts a volume from a URI.
    /// </summary>
    /// <remarks>
    /// The URI has the form econet or aun, then ://, then optional user name and password followed by an at sign, then the host and an optional port. A path after the host is ignored. The port defaults to the default AUN UDP port.
    /// </remarks>
    /// <param name="uri">
    /// The URI of the file server.
    /// </param>
    /// <param name="volumeId">
    /// The identifier for the new volume.
    /// </param>
    /// <param name="readOnly">
    /// True to refuse writes.
    /// </param>
    /// <returns>
    /// Information about the mounted volume.
    /// </returns>
    /// <exception cref="System.ArgumentException">
    /// Thrown when the host or the volume identifier is empty, or the volume identifier is already mounted.
    /// </exception>
    /// <exception cref="System.IO.IOException">
    /// Thrown when the login fails.
    /// </exception>
    public MountInfo Mount(string uri, string volumeId, bool readOnly)
    {
      ParseMountUri(uri, null, null, out string host, out int port, out string? username, out string? password);
      return Mount(host, port, volumeId, readOnly, username, password);
    }

    /// <summary>
    /// Mounts an Econet file server volume.
    /// </summary>
    /// <remarks>
    /// A login is done only when a user name is given. When the login fails, the client is disposed.
    /// </remarks>
    /// <param name="host">
    /// The host name or address of the server.
    /// </param>
    /// <param name="port">
    /// The UDP port of the server.
    /// </param>
    /// <param name="volumeId">
    /// The identifier for the new volume.
    /// </param>
    /// <param name="readOnly">
    /// True to refuse writes.
    /// </param>
    /// <param name="username">
    /// The user name for the login, or null for no login.
    /// </param>
    /// <param name="password">
    /// The password for the login, or null.
    /// </param>
    /// <returns>
    /// Information about the mounted volume.
    /// </returns>
    /// <exception cref="System.ArgumentException">
    /// Thrown when <paramref name="host"/> or <paramref name="volumeId"/> is empty, or the volume identifier is already mounted.
    /// </exception>
    /// <exception cref="System.IO.IOException">
    /// Thrown when the login fails.
    /// </exception>
    public MountInfo Mount(
      string host,
      int port,
      string volumeId,
      bool readOnly,
      string? username = null,
      string? password = null)
    {
      if (string.IsNullOrWhiteSpace(host))
        throw new ArgumentException("Host cannot be null or empty.", nameof(host));

      if (string.IsNullOrWhiteSpace(volumeId))
        throw new ArgumentException("Volume ID cannot be null or empty.", nameof(volumeId));

      if (_mounts.ContainsKey(volumeId))
        throw new ArgumentException($"Volume ID '{volumeId}' is already mounted.");

      AunFileServerClient client = new AunFileServerClient(host, port);
      try
      {
        if (!string.IsNullOrWhiteSpace(username))
          client.Login(username, password);
      }
      catch (Exception ex)
      {
        client.Dispose();
        throw new IOException($"Failed to mount Econet server '{host}': {ex.Message}", ex);
      }

      EconetMount mount = new EconetMount
      {
        VolumeId = volumeId,
        Host = host,
        Port = port,
        Client = client,
        Username = username,
        ReadOnly = readOnly,
        MountedAt = DateTime.UtcNow,
      };

      _mounts[volumeId] = mount;

      return CreateMountInfo(mount);
    }

    /// <summary>
    /// Mounts using a pre-configured in-memory client (unit tests).
    /// </summary>
    internal MountInfo MountWithClient(AunFileServerClient client, string volumeId, bool readOnly, string? username)
    {
      if (string.IsNullOrWhiteSpace(volumeId))
        throw new ArgumentException("Volume ID cannot be null or empty.", nameof(volumeId));

      if (_mounts.ContainsKey(volumeId))
        throw new ArgumentException($"Volume ID '{volumeId}' is already mounted.");

      EconetMount mount = new EconetMount
      {
        VolumeId = volumeId,
        Host = "inmemory",
        Port = 0,
        Client = client,
        Username = username,
        ReadOnly = readOnly,
        MountedAt = DateTime.UtcNow,
      };

      _mounts[volumeId] = mount;
      return CreateMountInfo(mount);
    }

    /// <summary>
    /// Disposes the client of a volume and removes the volume.
    /// </summary>
    /// <param name="volumeId">
    /// The identifier of the volume.
    /// </param>
    /// <exception cref="System.ArgumentException">
    /// Thrown when the volume is not mounted.
    /// </exception>
    public void Unmount(string volumeId)
    {
      if (!_mounts.TryGetValue(volumeId, out EconetMount? mount))
        throw new ArgumentException($"Volume ID '{volumeId}' is not mounted.");

      mount.Client.Dispose();
      _mounts.Remove(volumeId);
    }

    /// <summary>
    /// Gets information about a mounted volume.
    /// </summary>
    /// <param name="volumeId">
    /// The identifier of the volume.
    /// </param>
    /// <returns>
    /// The mount information, or null when the volume is not mounted.
    /// </returns>
    public MountInfo? GetMountInfo(string volumeId)
    {
      if (!_mounts.TryGetValue(volumeId, out EconetMount? mount))
        return null;

      return CreateMountInfo(mount);
    }

    /// <summary>
    /// Lists the mounted volumes.
    /// </summary>
    /// <returns>
    /// An array with the mount information of each volume.
    /// </returns>
    public MountInfo[] ListMounts()
    {
      MountInfo[] mounts = new MountInfo[_mounts.Count];
      int index = 0;

      Dictionary<string, EconetMount>.Enumerator enumerator = _mounts.GetEnumerator();
      while (enumerator.MoveNext())
      {
        mounts[index++] = CreateMountInfo(enumerator.Current.Value);
      }
      enumerator.Dispose();

      return mounts;
    }

    /// <summary>
    /// Reads a file from the file server.
    /// </summary>
    /// <param name="context">
    /// The context that names the volume.
    /// </param>
    /// <param name="path">
    /// The path of the file.
    /// </param>
    /// <returns>
    /// The content of the file.
    /// </returns>
    /// <exception cref="System.ArgumentException">
    /// Thrown when the volume is not mounted.
    /// </exception>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when the volume is not logged in.
    /// </exception>
    public byte[] ReadFile(UrnContext context, string path)
    {
      EconetMount mount = GetMount(context.VolumeId);
      EnsureLoggedIn(mount);
      return mount.Client.ReadFile(path);
    }

    /// <summary>
    /// Not implemented. Throws after the read-only check.
    /// </summary>
    /// <param name="context">
    /// The context that names the volume.
    /// </param>
    /// <param name="path">
    /// The path of the file.
    /// </param>
    /// <param name="data">
    /// The data that would be written.
    /// </param>
    /// <exception cref="System.UnauthorizedAccessException">
    /// Thrown when the volume or the context is read-only.
    /// </exception>
    /// <exception cref="System.NotSupportedException">
    /// Thrown otherwise, because Econet writes are not implemented.
    /// </exception>
    public void WriteFile(UrnContext context, string path, byte[] data)
    {
      EconetMount mount = GetMount(context.VolumeId);
      if (mount.ReadOnly || context.ReadOnly)
        throw new UnauthorizedAccessException("Volume is mounted read-only.");

      throw new NotSupportedException("Econet write operations are not yet implemented.");
    }

    /// <summary>
    /// Not implemented. Throws after the read-only check.
    /// </summary>
    /// <param name="context">
    /// The context that names the volume.
    /// </param>
    /// <param name="path">
    /// The path of the file.
    /// </param>
    /// <exception cref="System.UnauthorizedAccessException">
    /// Thrown when the volume or the context is read-only.
    /// </exception>
    /// <exception cref="System.NotSupportedException">
    /// Thrown otherwise, because Econet deletes are not implemented.
    /// </exception>
    public void DeleteFile(UrnContext context, string path)
    {
      EconetMount mount = GetMount(context.VolumeId);
      if (mount.ReadOnly || context.ReadOnly)
        throw new UnauthorizedAccessException("Volume is mounted read-only.");

      throw new NotSupportedException("Econet delete operations are not yet implemented.");
    }

    /// <summary>
    /// Lists a directory on the file server.
    /// </summary>
    /// <param name="context">
    /// The context that names the volume.
    /// </param>
    /// <param name="path">
    /// The path of the directory.
    /// </param>
    /// <returns>
    /// An array with the entries of the directory.
    /// </returns>
    /// <exception cref="System.ArgumentException">
    /// Thrown when the volume is not mounted.
    /// </exception>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when the volume is not logged in.
    /// </exception>
    public DirectoryEntry[] ListDirectory(UrnContext context, string path)
    {
      EconetMount mount = GetMount(context.VolumeId);
      EnsureLoggedIn(mount);

      AunDirectoryEntry[] entries = mount.Client.ListDirectory(path);
      DirectoryEntry[] result = new DirectoryEntry[entries.Length];

      for (int i = 0; i < entries.Length; i++)
      {
        AunDirectoryEntry entry = entries[i];
        result[i] = new DirectoryEntry
        {
          Name = entry.Name,
          Metadata = new FileMetadata
          {
            Name = entry.Name,
            Size = entry.Size,
            IsDirectory = entry.IsDirectory,
          },
        };
      }

      return result;
    }

    /// <summary>
    /// Finds the metadata of a path by listing its parent directory.
    /// </summary>
    /// <remarks>
    /// The name is compared without regard to case.
    /// </remarks>
    /// <param name="context">
    /// The context that names the volume.
    /// </param>
    /// <param name="path">
    /// The path of the file or directory.
    /// </param>
    /// <returns>
    /// The metadata, or null when the parent listing has no entry with that name.
    /// </returns>
    /// <exception cref="System.ArgumentException">
    /// Thrown when the volume is not mounted.
    /// </exception>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when the volume is not logged in.
    /// </exception>
    public FileMetadata? GetMetadata(UrnContext context, string path)
    {
      EconetMount mount = GetMount(context.VolumeId);
      EnsureLoggedIn(mount);

      string normalized = NormalizePath(path);
      int lastSlash = normalized.LastIndexOf('/');
      string parent = lastSlash > 0 ? normalized.Substring(0, lastSlash) : "/";
      string name = lastSlash >= 0 ? normalized.Substring(lastSlash + 1) : normalized;

      if (string.IsNullOrEmpty(name))
        name = ".";

      DirectoryEntry[] entries = ListDirectory(context, parent);
      for (int i = 0; i < entries.Length; i++)
      {
        if (string.Equals(entries[i].Name, name, StringComparison.OrdinalIgnoreCase))
          return entries[i].Metadata;
      }

      return null;
    }

    /// <summary>
    /// Not supported. Always throws.
    /// </summary>
    /// <param name="context">
    /// The context that names the volume.
    /// </param>
    /// <param name="address">
    /// The block address.
    /// </param>
    /// <param name="buffer">
    /// The buffer that would receive the block.
    /// </param>
    /// <returns>
    /// Never returns.
    /// </returns>
    /// <exception cref="System.NotSupportedException">
    /// Always thrown, because Econet has no block-level access.
    /// </exception>
    public int ReadBlock(UrnContext context, BlockAddress address, Span<byte> buffer)
    {
      throw new NotSupportedException("Econet does not support block-level access.");
    }

    /// <summary>
    /// Not supported. Always throws.
    /// </summary>
    /// <param name="context">
    /// The context that names the volume.
    /// </param>
    /// <param name="address">
    /// The block address.
    /// </param>
    /// <param name="data">
    /// The data that would be written.
    /// </param>
    /// <exception cref="System.NotSupportedException">
    /// Always thrown, because Econet has no block-level access.
    /// </exception>
    public void WriteBlock(UrnContext context, BlockAddress address, ReadOnlySpan<byte> data)
    {
      throw new NotSupportedException("Econet does not support block-level access.");
    }

    /// <summary>
    /// Returns null, because there is no block access.
    /// </summary>
    /// <param name="context">
    /// The context that names the volume.
    /// </param>
    /// <returns>
    /// Always null.
    /// </returns>
    public BlockDeviceInfo? GetBlockDeviceInfo(UrnContext context)
    {
      return null;
    }

    /// <summary>
    /// Reads the whole file on a thread pool thread and returns it as a read-only memory stream.
    /// </summary>
    /// <param name="context">
    /// The context that names the volume.
    /// </param>
    /// <param name="path">
    /// The path of the file.
    /// </param>
    /// <param name="cancellationToken">
    /// A token that cancels waiting for the task to start.
    /// </param>
    /// <returns>
    /// A task with the stream.
    /// </returns>
    public Task<Stream> OpenReadAsync(UrnContext context, string path, CancellationToken cancellationToken)
    {
      return Task.Run(() =>
      {
        byte[] data = ReadFile(context, path);
        return (Stream)new MemoryStream(data, writable: false);
      }, cancellationToken);
    }

    /// <summary>
    /// Not implemented. Throws after the read-only check.
    /// </summary>
    /// <param name="context">
    /// The context that names the volume.
    /// </param>
    /// <param name="path">
    /// The path of the file.
    /// </param>
    /// <param name="data">
    /// The stream that would be written.
    /// </param>
    /// <param name="cancellationToken">
    /// A token that cancels the operation.
    /// </param>
    /// <returns>
    /// Never returns a task.
    /// </returns>
    /// <exception cref="System.UnauthorizedAccessException">
    /// Thrown when the volume or the context is read-only.
    /// </exception>
    /// <exception cref="System.NotSupportedException">
    /// Thrown otherwise, because Econet writes are not implemented.
    /// </exception>
    public Task WriteAsync(UrnContext context, string path, Stream data, CancellationToken cancellationToken)
    {
      if (GetMount(context.VolumeId).ReadOnly || context.ReadOnly)
        throw new UnauthorizedAccessException("Volume is mounted read-only.");

      throw new NotSupportedException("Econet write operations are not yet implemented.");
    }

    /// <summary>
    /// Lists a directory by running ListDirectory on a thread pool thread.
    /// </summary>
    /// <param name="context">
    /// The context that names the volume.
    /// </param>
    /// <param name="path">
    /// The path of the directory.
    /// </param>
    /// <param name="cancellationToken">
    /// A token that cancels waiting for the task to start.
    /// </param>
    /// <returns>
    /// A task with the directory entries.
    /// </returns>
    public Task<DirectoryEntry[]> ListDirectoryAsync(UrnContext context, string path, CancellationToken cancellationToken)
    {
      return Task.Run(() => ListDirectory(context, path), cancellationToken);
    }

    /// <summary>
    /// Gets metadata by running GetMetadata on a thread pool thread.
    /// </summary>
    /// <param name="context">
    /// The context that names the volume.
    /// </param>
    /// <param name="path">
    /// The path of the file or directory.
    /// </param>
    /// <param name="cancellationToken">
    /// A token that cancels waiting for the task to start.
    /// </param>
    /// <returns>
    /// A task with the metadata, or null.
    /// </returns>
    public Task<FileMetadata?> GetMetadataAsync(UrnContext context, string path, CancellationToken cancellationToken)
    {
      return Task.Run(() => GetMetadata(context, path), cancellationToken);
    }

    /// <summary>
    /// Unmounts every mounted volume.
    /// </summary>
    public void Dispose()
    {
      if (_disposed)
        return;

      string[] volumeIds = new string[_mounts.Count];
      int index = 0;
      Dictionary<string, EconetMount>.Enumerator enumerator = _mounts.GetEnumerator();
      while (enumerator.MoveNext())
      {
        volumeIds[index++] = enumerator.Current.Key;
      }
      enumerator.Dispose();

      for (int i = 0; i < volumeIds.Length; i++)
        Unmount(volumeIds[i]);

      _disposed = true;
    }

    private static MountInfo CreateMountInfo(EconetMount mount)
    {
      string scheme = mount.Port > 0 ? "econet" : "aun";
      string sourceUri = mount.Port > 0
        ? $"{scheme}://{mount.Host}:{mount.Port}/"
        : $"{scheme}://inmemory/";

      return new MountInfo
      {
        VolumeId = mount.VolumeId,
        FileSystemType = "Econet",
        VolumeLabel = mount.Host,
        IsReadOnly = mount.ReadOnly,
        HasBlockAccess = false,
        SourceUri = sourceUri,
        Type = "econet",
        TotalSize = null,
        FreeSpace = null,
      };
    }

    private static void ParseMountUri(
      string uri,
      string? configUsername,
      string? configPassword,
      out string host,
      out int port,
      out string? username,
      out string? password)
    {
      string working = uri;
      if (working.StartsWith("econet://", StringComparison.OrdinalIgnoreCase))
        working = working.Substring(9);
      else if (working.StartsWith("aun://", StringComparison.OrdinalIgnoreCase))
        working = working.Substring(6);

      int slashIndex = working.IndexOf('/');
      if (slashIndex >= 0)
        working = working.Substring(0, slashIndex);

      username = configUsername;
      password = configPassword;

      if (working.Contains('@'))
      {
        string[] authParts = working.Split('@', 2);
        string userInfo = authParts[0];
        working = authParts[1];

        if (userInfo.Contains(':'))
        {
          string[] credentials = userInfo.Split(':', 2);
          username = credentials[0];
          password = credentials[1];
        }
        else
        {
          username = userInfo;
        }
      }

      if (working.Contains(':'))
      {
        string[] hostParts = working.Split(':', 2);
        host = hostParts[0];
        if (!int.TryParse(hostParts[1], out port))
          port = EconetClientConstants.DefaultAunUdpPort;
      }
      else
      {
        host = working;
        port = EconetClientConstants.DefaultAunUdpPort;
      }
    }

    private static string NormalizePath(string path)
    {
      if (string.IsNullOrWhiteSpace(path) || path == ".")
        return "/";

      return path.Replace('\\', '/');
    }

    private static void EnsureLoggedIn(EconetMount mount)
    {
      if (!mount.Client.IsLoggedIn)
        throw new InvalidOperationException("Econet volume is not logged in. Mount with credentials.");
    }

    private EconetMount GetMount(string volumeId)
    {
      if (!_mounts.TryGetValue(volumeId, out EconetMount? mount))
        throw new ArgumentException($"Volume ID '{volumeId}' is not mounted.");

      return mount;
    }

    private sealed class EconetMount
    {
      public string VolumeId { get; set; } = string.Empty;
      public string Host { get; set; } = string.Empty;
      public int Port { get; set; }
      public AunFileServerClient Client { get; set; } = null!;
      public string? Username { get; set; }
      public bool ReadOnly { get; set; }
      public DateTime MountedAt { get; set; }
    }
  }
}
