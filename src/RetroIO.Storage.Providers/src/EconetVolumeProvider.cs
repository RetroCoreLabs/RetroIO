//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RetroIO.Storage.Core;
using RetroIO.Protocols.Econet.Client;

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

    public EconetVolumeProvider()
    {
      _mounts = new Dictionary<string, EconetMount>(StringComparer.OrdinalIgnoreCase);
    }

    public string Type => "econet";

    public string[] SupportedSchemes => new[] { "econet", "aun" };

    public bool IsRemote => true;

    public bool CanRestore(UnifiedMountEntry entry, out string? errorMessage)
    {
      errorMessage = null;
      return true;
    }

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

    public MountInfo Mount(string uri, string volumeId, bool readOnly)
    {
      ParseMountUri(uri, null, null, out string host, out int port, out string? username, out string? password);
      return Mount(host, port, volumeId, readOnly, username, password);
    }

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

    public void Unmount(string volumeId)
    {
      if (!_mounts.TryGetValue(volumeId, out EconetMount? mount))
        throw new ArgumentException($"Volume ID '{volumeId}' is not mounted.");

      mount.Client.Dispose();
      _mounts.Remove(volumeId);
    }

    public MountInfo? GetMountInfo(string volumeId)
    {
      if (!_mounts.TryGetValue(volumeId, out EconetMount? mount))
        return null;

      return CreateMountInfo(mount);
    }

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

    public byte[] ReadFile(UrnContext context, string path)
    {
      EconetMount mount = GetMount(context.VolumeId);
      EnsureLoggedIn(mount);
      return mount.Client.ReadFile(path);
    }

    public void WriteFile(UrnContext context, string path, byte[] data)
    {
      EconetMount mount = GetMount(context.VolumeId);
      if (mount.ReadOnly || context.ReadOnly)
        throw new UnauthorizedAccessException("Volume is mounted read-only.");

      throw new NotSupportedException("Econet write operations are not yet implemented.");
    }

    public void DeleteFile(UrnContext context, string path)
    {
      EconetMount mount = GetMount(context.VolumeId);
      if (mount.ReadOnly || context.ReadOnly)
        throw new UnauthorizedAccessException("Volume is mounted read-only.");

      throw new NotSupportedException("Econet delete operations are not yet implemented.");
    }

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

    public int ReadBlock(UrnContext context, BlockAddress address, Span<byte> buffer)
    {
      throw new NotSupportedException("Econet does not support block-level access.");
    }

    public void WriteBlock(UrnContext context, BlockAddress address, ReadOnlySpan<byte> data)
    {
      throw new NotSupportedException("Econet does not support block-level access.");
    }

    public BlockDeviceInfo? GetBlockDeviceInfo(UrnContext context)
    {
      return null;
    }

    public Task<Stream> OpenReadAsync(UrnContext context, string path, CancellationToken cancellationToken)
    {
      return Task.Run(() =>
      {
        byte[] data = ReadFile(context, path);
        return (Stream)new MemoryStream(data, writable: false);
      }, cancellationToken);
    }

    public Task WriteAsync(UrnContext context, string path, Stream data, CancellationToken cancellationToken)
    {
      if (GetMount(context.VolumeId).ReadOnly || context.ReadOnly)
        throw new UnauthorizedAccessException("Volume is mounted read-only.");

      throw new NotSupportedException("Econet write operations are not yet implemented.");
    }

    public Task<DirectoryEntry[]> ListDirectoryAsync(UrnContext context, string path, CancellationToken cancellationToken)
    {
      return Task.Run(() => ListDirectory(context, path), cancellationToken);
    }

    public Task<FileMetadata?> GetMetadataAsync(UrnContext context, string path, CancellationToken cancellationToken)
    {
      return Task.Run(() => GetMetadata(context, path), cancellationToken);
    }

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
