namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// What a <c>*XFTRA</c> request asks the remote system to do. Carried as parameter 11.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the field that decides the whole request</b></para>
    /// <para>
    /// <c>*XFTRA</c> serves more than one operator command with ONE message shape and one
    /// parameter vocabulary. Parameter 11 selects which. A client that copies the value from a
    /// captured <c>TRANSFER-FILE</c> can only ever transfer, whatever else it fills in.
    /// </para>
    /// <para>
    /// Not a flags enum: the two known values are 2 and 3, consecutive small integers, and no
    /// capture shows them combined. Treat unknown values as unknown rather than as bit
    /// combinations.
    /// </para>
    /// <para>
    /// MEASURED from two live captures between SINTRAN K machines:
    /// </para>
    ///  - <c>TRANSFER-FILE</c> carries 2, from the 2026-07-28 capture.
    ///  - <c>APPEND-REMOTE-BATCH</c> carries 3, from the 2026-07-31 capture
    ///    (<c>DOC/XMSG-APPEND-REMOTE-BATCH-CAPTURED-2026-07-31.md</c>), node 102 to node 100.
    /// <para>
    /// The 2026-07-28 write-up recorded parameter 11 as a constant of unknown meaning, because
    /// only one operation had been seen. The second capture is what turned it into a selector -
    /// a value that never varies across one capture cannot be told from a constant.
    /// </para>
    /// <para>
    /// The operation also decides what parameter 8 MEANS. It is "the file", not "the destination
    /// file": for a transfer it is the destination, for a batch it is the job INPUT file. Do not
    /// hard-code either word into a decoder.
    /// </para>
    /// </remarks>
    public enum XftraOperation : ushort
    {
        /// <summary>
        /// Transfer a file (<c>TRANSFER-FILE</c>). Parameter 8 is the destination file.
        /// </summary>
        TransferFile = 2,

        /// <summary>
        /// Append a batch job on the remote system (<c>APPEND-REMOTE-BATCH</c>). Parameter 8 is
        /// the batch input file, and parameter 16 carries the batch output file.
        /// </summary>
        AppendRemoteBatch = 3,
    }
}
