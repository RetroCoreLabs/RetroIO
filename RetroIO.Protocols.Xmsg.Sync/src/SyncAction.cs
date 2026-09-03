using System;

namespace RetroIO.Protocols.Xmsg.Sync
{
    /// <summary>
    /// One thing the daemon has decided to do, or has decided not to do and why.
    /// </summary>
    /// <remarks>
    /// <para><b>A plan is made first and carried out afterwards</b></para>
    /// <para>
    /// Deciding and doing are split so the decision can be tested with no machine, no disk and no
    /// wire, and so a person can be shown what a run WOULD do before it does it. Everything that
    /// deletes on the far end is worth seeing in advance.
    /// </para>
    /// </remarks>
    public sealed class SyncAction
    {
        /// <summary>
        /// Creates an action.
        /// </summary>
        /// <param name="kind">
        /// What to do.
        /// </param>
        /// <param name="localPath">
        /// The local file this concerns. For a <see cref="SyncActionKind.Pull"/> this is where the
        /// file WILL be written and need not exist yet.
        /// </param>
        /// <param name="fileSpec">
        /// The remote filespec, quoted or bare as the kind requires, or an empty string when the
        /// file could not be addressed at all.
        /// </param>
        /// <param name="reason">
        /// Why, in a sentence a person can act on. Required for
        /// <see cref="SyncActionKind.Skip"/> and free to be empty otherwise.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="localPath"/>, <paramref name="fileSpec"/> or
        /// <paramref name="reason"/> is null.
        /// </exception>
        public SyncAction(SyncActionKind kind, string localPath, string fileSpec, string reason)
        {
            if (localPath == null) { throw new ArgumentNullException(nameof(localPath)); }
            if (fileSpec == null) { throw new ArgumentNullException(nameof(fileSpec)); }
            if (reason == null) { throw new ArgumentNullException(nameof(reason)); }

            Kind = kind;
            LocalPath = localPath;
            FileSpec = fileSpec;
            Reason = reason;
        }

        /// <summary>
        /// Gets what is to be done.
        /// </summary>
        public SyncActionKind Kind { get; }

        /// <summary>
        /// Gets the local file this concerns.
        /// </summary>
        public string LocalPath { get; }

        /// <summary>
        /// Gets the remote filespec.
        /// </summary>
        /// <remarks>
        /// Quoted for a <see cref="SyncActionKind.Create"/> and bare otherwise, because that is
        /// what makes SINTRAN create the file rather than complain about one that is not there.
        /// </remarks>
        public string FileSpec { get; }

        /// <summary>
        /// Gets the sentence explaining the decision.
        /// </summary>
        public string Reason { get; }
    }
}
