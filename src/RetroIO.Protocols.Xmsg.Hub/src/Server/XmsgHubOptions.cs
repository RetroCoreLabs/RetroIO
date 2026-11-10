//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Hub.Server
{
    /// <summary>
    /// Settings of the hosted COSMOS Ethernet hub.
    /// </summary>
    public sealed class XmsgHubOptions
    {
        /// <summary>
        /// Gets or sets the TCP port machines join on. The default is 5010, the port the
        /// standalone xmsghub and every RetroCore setup note use.
        /// </summary>
        /// <remarks>
        /// Zero picks a free port (tests use that). On some Windows machines ASUS Armoury Crate is
        /// known to take 5010 after a reboot; the service then reports Failed with the reason
        /// rather than stopping the whole server.
        /// </remarks>
        public int Port { get; set; } = 5010;

        /// <summary>
        /// Gets or sets the local address to listen on. The default is 127.0.0.1, this computer
        /// only. Use 0.0.0.0 to let machines on other computers join.
        /// </summary>
        public string BindAddress { get; set; } = "127.0.0.1";

        /// <summary>
        /// Gets or sets a remote hub to join, as host:port, or null for none.
        /// </summary>
        public string? Uplink { get; set; }

        /// <summary>
        /// Gets or sets a pcap file that receives every frame, or null for no capture file.
        /// </summary>
        /// <remarks>
        /// An existing file is replaced each time the hub starts. The buffer of recent frames
        /// (<see cref="FrameBufferSize"/>) is kept either way; this file is for a complete record.
        /// </remarks>
        public string? CaptureFile { get; set; }

        /// <summary>
        /// Gets or sets how many recent frames are kept for the frame list. The default is 4096.
        /// </summary>
        public int FrameBufferSize { get; set; } = 4096;

        /// <summary>
        /// Gets or sets how many recent log lines are kept for the log view. The default is 1000.
        /// </summary>
        public int LogBufferSize { get; set; } = 1000;

        /// <summary>
        /// Gets or sets the seconds between status lines in the log. The default is 10. A line is
        /// written only when a number in it changed, so an idle hub stays quiet.
        /// </summary>
        public int StatusIntervalSeconds { get; set; } = 10;
    }
}
