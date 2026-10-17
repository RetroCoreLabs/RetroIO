//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Sync
{
    /// <summary>
    /// How one transfer ended.
    /// </summary>
    /// <remarks>
    /// <para><b>A failure is a first-class outcome, not an exception</b></para>
    /// <para>
    /// Transfers against a real machine fail for ordinary reasons - the file is open, the name is
    /// refused, the peer restarted mid-conversation. The runner has to carry on with the rest of
    /// the queue when that happens, so a failure travels back as a value with a reason attached
    /// rather than as something thrown.
    /// </para>
    /// <para><b>The hash is what the ledger records, and only a push can supply it</b></para>
    /// <para>
    /// After a push, the bytes that went out are the bytes on disk, so the caller already knows
    /// the hash. After a PULL the file on disk has just been replaced, so its hash has to be taken
    /// from what actually landed - that is why the agent returns it rather than the runner
    /// assuming it.
    /// </para>
    /// </remarks>
    public sealed class SyncTransferResult
    {
        private SyncTransferResult(bool succeeded, string reason, byte[] hash, long byteCount)
            : this(succeeded, reason, hash, byteCount, 0)
        {
        }

        private SyncTransferResult(
            bool succeeded, string reason, byte[] hash, long byteCount, int sintranError)
        {
            Succeeded = succeeded;
            Reason = reason;
            Hash = hash;
            ByteCount = byteCount;
            SintranError = sintranError;
        }

        /// <summary>
        /// Gets whether the transfer worked.
        /// </summary>
        public bool Succeeded { get; }

        /// <summary>
        /// Gets why it failed, or an empty string when it did not.
        /// </summary>
        public string Reason { get; }

        /// <summary>
        /// Gets the hash of the transferred content, for the ledger. Empty on failure.
        /// </summary>
        public byte[] Hash { get; }

        /// <summary>
        /// Gets how many bytes moved. Zero on failure.
        /// </summary>
        public long ByteCount { get; }

        /// <summary>
        /// Gets the SINTRAN error number the far machine gave, or zero when there was none.
        /// </summary>
        /// <remarks>
        /// <para><b>Carried as a number so nobody has to read the log text to make a decision</b></para>
        /// <see cref="Reason"/> is written for a person. Matching on it in code would work until
        /// somebody improved the wording, and then fail quietly - which is the wrong failure for the
        /// one caller that needs this: a create refused with 62, "File already exists", is not a
        /// fault at all but the answer to a question the daemon could not otherwise ask.
        /// <para>
        /// Zero means "no number", not "success". A transfer can fail for reasons the machine was
        /// never asked about - a dead link, a name we refused ourselves - and those carry zero.
        /// </para>
        /// </remarks>
        public int SintranError { get; }

        /// <summary>
        /// Builds the outcome of a transfer that worked.
        /// </summary>
        /// <param name="hash">
        /// The hash of the content that moved, which the ledger will remember.
        /// </param>
        /// <param name="byteCount">
        /// How many bytes moved.
        /// </param>
        /// <returns>
        /// The result.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="hash"/> is null.
        /// </exception>
        public static SyncTransferResult Ok(byte[] hash, long byteCount)
        {
            if (hash == null) { throw new ArgumentNullException(nameof(hash)); }

            return new SyncTransferResult(true, string.Empty, hash, byteCount);
        }

        /// <summary>
        /// Builds the outcome of a transfer that did not work.
        /// </summary>
        /// <param name="reason">
        /// What went wrong, in words a person reading the log can act on.
        /// </param>
        /// <returns>
        /// The result.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="reason"/> is null.
        /// </exception>
        public static SyncTransferResult Failed(string reason)
        {
            if (reason == null) { throw new ArgumentNullException(nameof(reason)); }

            return new SyncTransferResult(false, reason, Array.Empty<byte>(), 0);
        }

        /// <summary>
        /// Builds the outcome of a transfer the far machine refused, with its error number.
        /// </summary>
        /// <param name="reason">
        /// What went wrong, in words a person reading the log can act on.
        /// </param>
        /// <param name="sintranError">
        /// The SINTRAN III error number the machine sent.
        /// </param>
        /// <returns>
        /// The result.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="reason"/> is null.
        /// </exception>
        public static SyncTransferResult Refused(string reason, int sintranError)
        {
            if (reason == null) { throw new ArgumentNullException(nameof(reason)); }

            return new SyncTransferResult(false, reason, Array.Empty<byte>(), 0, sintranError);
        }
    }
}
