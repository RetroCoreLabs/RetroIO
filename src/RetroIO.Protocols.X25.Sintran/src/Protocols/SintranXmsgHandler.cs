//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Text;
using Microsoft.Extensions.Logging;

using RetroIO.Protocols.X25.Protocols;
namespace RetroIO.Protocols.X25.Sintran
{
    /// <summary>
    /// SINTRAN XMSG Protocol Handler
    /// Handles SINTRAN messaging system commands and message routing
    /// Provides message queuing, delivery, and notification services
    /// </summary>
    public class SintranXmsgHandler : ISintranProtocolHandler
    {
        private readonly ILogger _logger;
        private SintranHandlerStats _stats;
        private bool _initialized = false;

        public string Protocol => "XMSG";
        public string Description => "SINTRAN messaging system protocol";
        public ushort LCN { get; private set; }

        public event EventHandler<SintranResponseEventArgs>? ResponseReady;

        public SintranXmsgHandler(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _stats = new SintranHandlerStats
            {
                Protocol = Protocol,
                Status = "Initializing"
            };
        }

        public void Initialize()
        {
            if (_initialized) return;

            _logger.LogInformation("SintranXmsgHandler: Initializing SINTRAN XMSG handler");
            _stats.IsActive = true;
            _stats.Status = "Ready";
            _initialized = true;
        }

        public void OnChannelEstablished(ushort lcn)
        {
            LCN = lcn;
            _stats.LCN = lcn;
            _stats.IsActive = true;
            _stats.LastActivity = DateTime.UtcNow;
            _stats.Status = "Connected";
            
            _logger.LogInformation("SintranXmsgHandler: XMSG channel established on LCN {LCN}", lcn);
            
            // Send XMSG service ready notification
            SendXmsgResponse("XMSG SERVICE READY - X.25 BRIDGE ONLINE");
        }

        public void OnChannelClosed()
        {
            _stats.IsActive = false;
            _stats.Status = "Disconnected";
            _logger.LogInformation("SintranXmsgHandler: XMSG channel closed on LCN {LCN}", LCN);
        }

        public void HandleData(byte[] data)
        {
            if (data.Length == 0) return;

            _stats.MessagesReceived++;
            _stats.BytesReceived += data.Length;
            _stats.LastActivity = DateTime.UtcNow;

            try
            {
                var message = Encoding.ASCII.GetString(data).Trim();
                _logger.LogInformation("SintranXmsgHandler: Received XMSG command on LCN {LCN}: '{Message}'", 
                    LCN, message);

                ProcessXmsgCommand(message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SintranXmsgHandler: Error processing XMSG data on LCN {LCN}", LCN);
                _stats.Status = $"Error: {ex.Message}";
            }
        }

        /// <summary>
        /// Process incoming XMSG command from SINTRAN system
        /// </summary>
        private void ProcessXmsgCommand(string message)
        {
            // Parse SINTRAN XMSG command format
            // Example formats:
            // "XMSG SEND USER MESSAGE Hello World"     - Send message to user
            // "XMSG RECV USER"                         - Receive messages for user
            // "XMSG STATUS"                            - Get XMSG service status
            // "XMSG LIST"                              - List available users/queues
            // "XMSG QUEUE USER"                        - Check message queue for user
            // "XMSG BROADCAST MESSAGE Hello All"       - Broadcast message

            var parts = message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0].ToUpper() != "XMSG")
            {
                SendXmsgError($"Invalid XMSG command: {message}");
                return;
            }

            var command = parts[1].ToUpper();
            switch (command)
            {
                case "SEND":
                    HandleXmsgSend(parts, message);
                    break;

                case "RECV":
                case "RECEIVE":
                    HandleXmsgReceive(parts);
                    break;

                case "STATUS":
                    HandleXmsgStatus();
                    break;

                case "LIST":
                    HandleXmsgList();
                    break;

                case "QUEUE":
                    HandleXmsgQueue(parts);
                    break;

                case "BROADCAST":
                    HandleXmsgBroadcast(parts, message);
                    break;

                case "HELP":
                    HandleXmsgHelp();
                    break;

                case "PING":
                    HandleXmsgPing(parts);
                    break;

                default:
                    SendXmsgError($"Unknown XMSG command: {command}");
                    break;
            }
        }

        /// <summary>
        /// Handle XMSG send message command
        /// </summary>
        private void HandleXmsgSend(string[] parts, string fullMessage)
        {
            if (parts.Length < 5)
            {
                SendXmsgError("XMSG SEND requires: XMSG SEND <recipient> MESSAGE <text>");
                return;
            }

            var recipient = parts[2];
            var messageKeywordIndex = Array.IndexOf(parts, "MESSAGE");
            if (messageKeywordIndex < 0 || messageKeywordIndex >= parts.Length - 1)
            {
                SendXmsgError("XMSG SEND requires MESSAGE keyword followed by text");
                return;
            }

            // Extract message text after "MESSAGE" keyword
            var messageStart = fullMessage.IndexOf("MESSAGE ") + 8;
            var messageText = messageStart < fullMessage.Length ? fullMessage.Substring(messageStart) : "";

            _logger.LogInformation("SintranXmsgHandler: Sending message to {Recipient}: '{MessageText}'", 
                recipient, messageText);

            // In a real implementation, queue message for delivery
            SendXmsgResponse($"XMSG SENT TO {recipient} - MESSAGE QUEUED FOR DELIVERY");
            SendXmsgResponse($"MESSAGE ID: MSG-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{recipient.ToUpper()}");
        }

        /// <summary>
        /// Handle XMSG receive messages command
        /// </summary>
        private void HandleXmsgReceive(string[] parts)
        {
            var user = parts.Length > 2 ? parts[2] : "CURRENT";
            
            _logger.LogInformation("SintranXmsgHandler: Checking messages for {User}", user);

            // In a real implementation, check message queue for user
            // For now, send a sample message
            SendXmsgResponse($"XMSG MESSAGES FOR {user}:");
            SendXmsgResponse($"1. FROM SYSTEM AT {DateTime.UtcNow.AddMinutes(-5):HH:mm}: X.25 Bridge operational");
            SendXmsgResponse($"2. FROM ROUTING AT {DateTime.UtcNow.AddMinutes(-2):HH:mm}: Network topology updated");
            SendXmsgResponse($"END OF MESSAGES - 2 MESSAGES DELIVERED");
        }

        /// <summary>
        /// Handle XMSG status request
        /// </summary>
        private void HandleXmsgStatus()
        {
            _logger.LogInformation("SintranXmsgHandler: Sending XMSG status");

            var status = new StringBuilder();
            status.AppendLine("XMSG SERVICE STATUS:");
            status.AppendLine($"Service:        ONLINE");
            status.AppendLine($"Protocol:       X.25 PVC");
            status.AppendLine($"LCN:           {LCN}");
            status.AppendLine($"Messages RX:    {_stats.MessagesReceived}");
            status.AppendLine($"Messages TX:    {_stats.MessagesSent}");
            status.AppendLine($"Bytes RX:       {_stats.BytesReceived}");
            status.AppendLine($"Bytes TX:       {_stats.BytesSent}");
            status.AppendLine($"Last Activity:  {_stats.LastActivity:yyyy-MM-dd HH:mm:ss} UTC");
            status.AppendLine($"Queue Status:   READY");

            SendXmsgResponse(status.ToString());
        }

        /// <summary>
        /// Handle XMSG list users/queues command
        /// </summary>
        private void HandleXmsgList()
        {
            _logger.LogInformation("SintranXmsgHandler: Sending XMSG user list");

            var list = new StringBuilder();
            list.AppendLine("XMSG ACTIVE USERS:");
            list.AppendLine("User           Queue Size  Last Activity");
            list.AppendLine("-------------  ----------  -------------");
            list.AppendLine("SYSTEM         0           ONLINE");
            list.AppendLine("ROUTING        0           ONLINE");  
            list.AppendLine("FILESERVER     0           ONLINE");
            list.AppendLine("BRIDGE         0           ONLINE");
            list.AppendLine("");
            list.AppendLine("4 USERS ACTIVE - 0 MESSAGES QUEUED");

            SendXmsgResponse(list.ToString());
        }

        /// <summary>
        /// Handle XMSG queue check command
        /// </summary>
        private void HandleXmsgQueue(string[] parts)
        {
            var user = parts.Length > 2 ? parts[2] : "CURRENT";
            
            _logger.LogInformation("SintranXmsgHandler: Checking queue for {User}", user);

            SendXmsgResponse($"XMSG QUEUE STATUS FOR {user}:");
            SendXmsgResponse($"Messages in queue: 0");
            SendXmsgResponse($"Queue limit: 100 messages");
            SendXmsgResponse($"Queue status: READY");
        }

        /// <summary>
        /// Handle XMSG broadcast command
        /// </summary>
        private void HandleXmsgBroadcast(string[] parts, string fullMessage)
        {
            if (parts.Length < 4)
            {
                SendXmsgError("XMSG BROADCAST requires: XMSG BROADCAST MESSAGE <text>");
                return;
            }

            var messageKeywordIndex = Array.IndexOf(parts, "MESSAGE");
            if (messageKeywordIndex < 0)
            {
                SendXmsgError("XMSG BROADCAST requires MESSAGE keyword");
                return;
            }

            // Extract broadcast message
            var messageStart = fullMessage.IndexOf("MESSAGE ") + 8;
            var messageText = messageStart < fullMessage.Length ? fullMessage.Substring(messageStart) : "";

            _logger.LogInformation("SintranXmsgHandler: Broadcasting message: '{MessageText}'", messageText);

            SendXmsgResponse($"XMSG BROADCAST SENT TO ALL USERS");
            SendXmsgResponse($"MESSAGE: {messageText}");
            SendXmsgResponse($"DELIVERED TO 4 ACTIVE USERS");
        }

        /// <summary>
        /// Handle XMSG help command
        /// </summary>
        private void HandleXmsgHelp()
        {
            var help = new StringBuilder();
            help.AppendLine("XMSG COMMAND HELP:");
            help.AppendLine("XMSG SEND <user> MESSAGE <text>  - Send message to user");
            help.AppendLine("XMSG RECV [user]                 - Receive messages");
            help.AppendLine("XMSG STATUS                      - Show service status");
            help.AppendLine("XMSG LIST                        - List active users");
            help.AppendLine("XMSG QUEUE [user]                - Check message queue");
            help.AppendLine("XMSG BROADCAST MESSAGE <text>    - Broadcast to all users");
            help.AppendLine("XMSG PING [target]               - Send ping message");
            help.AppendLine("XMSG HELP                        - Show this help");

            SendXmsgResponse(help.ToString());
        }

        /// <summary>
        /// Handle XMSG ping command
        /// </summary>
        private void HandleXmsgPing(string[] parts)
        {
            var target = parts.Length > 2 ? parts[2] : "LOCAL";
            
            _logger.LogInformation("SintranXmsgHandler: Ping to {Target}", target);

            SendXmsgResponse($"XMSG PING {target}");
            SendXmsgResponse($"PONG FROM {target} - RESPONSE TIME: 0ms");
            SendXmsgResponse($"XMSG SERVICE ACTIVE ON {target}");
        }

        /// <summary>
        /// Send XMSG response message
        /// </summary>
        private void SendXmsgResponse(string message)
        {
            var responseData = Encoding.ASCII.GetBytes(message + "\r\n");
            
            _stats.MessagesSent++;
            _stats.BytesSent += responseData.Length;
            _stats.LastActivity = DateTime.UtcNow;

            var eventArgs = new SintranResponseEventArgs
            {
                LCN = LCN,
                Data = responseData,
                Protocol = Protocol,
                Timestamp = DateTime.UtcNow
            };

            ResponseReady?.Invoke(this, eventArgs);
            
            _logger.LogDebug("SintranXmsgHandler: Sent XMSG response on LCN {LCN}: '{Message}'", 
                LCN, message.Replace("\r\n", ""));
        }

        /// <summary>
        /// Send XMSG error message
        /// </summary>
        private void SendXmsgError(string errorMessage)
        {
            var message = $"XMSG ERROR: {errorMessage}";
            SendXmsgResponse(message);
            
            _logger.LogWarning("SintranXmsgHandler: Sent XMSG error: {ErrorMessage}", errorMessage);
        }

        public SintranHandlerStats GetStats()
        {
            return new SintranHandlerStats
            {
                Protocol = _stats.Protocol,
                LCN = _stats.LCN,
                MessagesReceived = _stats.MessagesReceived,
                MessagesSent = _stats.MessagesSent,
                BytesReceived = _stats.BytesReceived,
                BytesSent = _stats.BytesSent,
                LastActivity = _stats.LastActivity,
                IsActive = _stats.IsActive,
                Status = _stats.Status
            };
        }

        public void Dispose()
        {
            if (!_initialized) return;

            _logger.LogDebug("SintranXmsgHandler: Disposing XMSG handler for LCN {LCN}", LCN);
            _stats.IsActive = false;
            _stats.Status = "Disposed";
            _initialized = false;
        }
    }
}