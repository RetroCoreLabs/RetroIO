//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Buffers;

namespace RetroIO.Protocols.Tnfs.Server;

/// <summary>
/// IDisposable wrapper around pooled memory for TNFS message buffers.
/// Automatically returns buffer to pool on disposal.
/// </summary>
public sealed class TnfsBuffer : IDisposable
{
    private readonly IMemoryOwner<byte> _memoryOwner;
    private bool _disposed;

    private TnfsBuffer(IMemoryOwner<byte> memoryOwner)
    {
        _memoryOwner = memoryOwner;
    }

    /// <summary>
    /// Rented buffer as <see cref="Memory{T}"/> of <see cref="byte"/>.
    /// </summary>
    public Memory<byte> Memory => _memoryOwner.Memory;

    /// <summary>
    /// Rented buffer as <see cref="Span{T}"/> of <see cref="byte"/>.
    /// </summary>
    public Span<byte> Span => _memoryOwner.Memory.Span;

    /// <summary>
    /// Rent a buffer from the shared memory pool.
    /// IMPORTANT: Must dispose to return buffer to pool.
    /// </summary>
    public static TnfsBuffer Rent(int minimumSize = 1024)
    {
        var owner = MemoryPool<byte>.Shared.Rent(minimumSize);
        return new TnfsBuffer(owner);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _memoryOwner.Dispose();
        _disposed = true;
    }
}
