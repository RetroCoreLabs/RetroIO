//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using RetroIO.Protocols.Econet.FileServer.Operations;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Protocols.Econet.FileServer.Dispatch;

/// <summary>
/// Composition root for the file-server operation table: builds the <c>[256]</c> array of
/// <see cref="IFileServerOperation"/> indexed directly by fsop byte.
/// </summary>
/// <remarks>
/// The fsop counterpart of <c>OsCliCommandTable</c>. The crucial difference: OSCLI needs an ordered
/// registry because dot-abbreviations mean order-of-registration decides which command a prefix
/// matches ("order is behaviour"). Here the opcode byte is unique, so order is irrelevant — the table
/// is a plain array indexed by opcode and <see cref="Register"/> refuses to overwrite an occupied
/// slot, turning any accidental duplicate opcode into a loud failure at start-up rather than a silent
/// last-writer-wins.
/// <para>
/// This table grew, one <c>Register(...)</c> line per commit, out of the old <c>MiscOpcodeHandler</c>
/// big-switch as part of a strangler migration that is now complete: that handler is gone and all of
/// its opcodes are served here. Slots with no operation are null; the dispatcher returns null for
/// those and the caller falls through to <c>FileIoCommandHandler</c> (the channel opcodes it owns).
/// </para>
/// </remarks>
public static class FsOperationTable
{
    /// <summary>
    /// Builds the operation table. Empty slots are null and fall through to the legacy handlers.
    /// </summary>
    /// <param name="context">
    /// Server-wide services passed to each operation's constructor.
    /// </param>
    /// <returns>
    /// A 256-entry array indexed by fsop byte; null where no operation has been migrated yet.
    /// </returns>
    public static IFileServerOperation?[] Build(FileServerContext context)
    {
        IFileServerOperation?[] table = new IFileServerOperation?[256];

        // --- Migrated operations get registered here, one per strangler commit. ---
        Register(table, new CatHeaderOperation());        // 0x04
        Register(table, new GetByteOperation());         // 0x08
        Register(table, new PutByteOperation());          // 0x09
        Register(table, new GetBytesOperation());         // 0x0A
        Register(table, new PutBytesOperation());         // 0x0B
        Register(table, new GetRandomAccessInfoOperation()); // 0x0C
        Register(table, new SetRandomAccessInfoOperation()); // 0x0D
        Register(table, new ReadLoggedOnUsersOperation());   // 0x0F
        Register(table, new ReadTimeOperation());         // 0x10
        Register(table, new EndOfFileOperation());        // 0x11
        Register(table, new GetObjectInfoOperation());    // 0x12
        Register(table, new SetObjectInfoOperation());    // 0x13
        Register(table, new ReadUserEnvOperation());      // 0x15
        Register(table, new SetBootOptOperation());       // 0x16
        Register(table, new ReadUserInfoOperation());     // 0x18
        Register(table, new SetRtcOperation());           // 0x1C
        Register(table, new SetUserFreeSpaceOperation()); // 0x1F
        Register(table, new ReadClientIdOperation());     // 0x20

        return table;
    }

    /// <summary>
    /// Places an operation at its opcode index, refusing to overwrite an already-occupied slot.
    /// </summary>
    /// <param name="table">
    /// The table being built.
    /// </param>
    /// <param name="operation">
    /// The operation to register; its <see cref="IFileServerOperation.Opcode"/> is the index.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when two operations claim the same opcode — a programming error, caught at build time.
    /// </exception>
    internal static void Register(IFileServerOperation?[] table, IFileServerOperation operation)
    {
        if (table[operation.Opcode] != null)
            throw new InvalidOperationException($"Duplicate FSOP operation for opcode 0x{operation.Opcode:X2}.");

        table[operation.Opcode] = operation;
    }
}
