using RetroIO.Protocols.Xmsg.Api;

namespace RetroIO.Protocols.Xmsg.Chat
{
    /// <summary>
    /// The one place a chat message is turned into a kernel send.
    /// </summary>
    /// <remarks>
    /// <para><b>Why it exists</b></para>
    /// <para>
    /// <c>ChatClient.SendToServer</c> and <c>ChatServer.SendTo</c> were line-for-line identical
    /// apart from the destination they passed. Two copies of a buffer-reserve / encode / write /
    /// send sequence is exactly the shape that drifts: a fix to the reserve failure or the write
    /// flags would have had to be made twice, and nothing would have failed if it were made once.
    /// </para>
    /// </remarks>
    internal static class ChatWire
    {
        /// <summary>
        /// Encodes a message into a freshly reserved buffer and sends it to one destination.
        /// </summary>
        /// <param name="kernel">
        /// The kernel that owns the buffer pool and the port.
        /// </param>
        /// <param name="destination">
        /// The magic number to send to - the server for a client, one member for the server.
        /// </param>
        /// <param name="port">
        /// The sending port.
        /// </param>
        /// <param name="message">
        /// The message to encode.
        /// </param>
        /// <remarks>
        /// A buffer that cannot be reserved DROPS the message in silence, which is what both
        /// original copies did. That is preserved deliberately rather than quietly changed while
        /// merging them - chat has no retry path, and inventing one here would be a behaviour
        /// change hidden inside a de-duplication.
        /// </remarks>
        public static void Send(
            XmsgKernel kernel,
            XmsgMagicNumber destination,
            XmsgPortNumber port,
            ChatMessage message)
        {
            int size = message.ByteCount;

            XmsgMessageIdentifier buffer;
            XmsgStatus reserved = kernel.ReserveBuffer(size, XmsgBufferOptions.None, out buffer);
            if (reserved.IsError)
            {
                return;
            }

            byte[] bytes = new byte[size];
            message.Encode(bytes);

            int written;
            kernel.Write(buffer, bytes, 0, false, out written);
            kernel.Send(destination, port, XmsgSendFlags.None);
        }
    }
}
