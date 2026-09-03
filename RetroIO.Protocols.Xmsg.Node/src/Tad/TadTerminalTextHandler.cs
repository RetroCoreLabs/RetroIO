namespace RetroIO.Protocols.Xmsg.Node.Tad
{
    /// <summary>
    /// Receives terminal text decoded from a TAD BDAT payload for display.
    /// </summary>
    /// <remarks>
    /// The text has already had bit 7 stripped (asker keystrokes carry even parity on the wire)
    /// and uses the TAD line convention: CR (<c>0x8D</c> on the wire) with no LF.
    /// </remarks>
    /// <param name="text">
    /// The concatenated BDAT text of one frame, ready to render.
    /// </param>
    public delegate void TadTerminalTextHandler(string text);
}
