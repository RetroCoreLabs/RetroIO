namespace RetroIO.Protocols.Xmsg
{
    /// <summary>
    /// Receives a human-readable diagnostic line produced by an XMSG component.
    /// </summary>
    /// <remarks>
    /// Named delegate rather than a bare <c>Action</c> so the parameter carries intent at the
    /// call site (project standard: no bare Action/Func for events or notification hooks).
    /// Lines are already formatted and carry no trailing newline.
    /// </remarks>
    /// <param name="message">
    /// The diagnostic line, conventionally prefixed with the emitting component in brackets
    /// (for example <c>[asker] connect-to D100</c>).
    /// </param>
    public delegate void XmsgLogHandler(string message);
}
