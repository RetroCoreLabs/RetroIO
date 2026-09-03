using System;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;

namespace RetroIO.Protocols.X25.Diagnostics
{
    /// <summary>
    /// Logging extensions for enriched logging with layer and direction tags
    /// </summary>
    public static class LoggingExtensions
    {
        /// <summary>
        /// Log HDLC frame with enriched tags
        /// </summary>
        public static void LogHdlcFrame(this Microsoft.Extensions.Logging.ILogger logger, HdlcFrameEventArgs args, string? message = null)
        {
            if (logger == null || args == null) return;

            string tags = $"Layer=HDLC Direction={args.Direction} FrameId={args.FrameId}";
            string logMessage = message ?? $"HDLC frame {args.GetFrameSummary()}";
            
            logger.LogDebug($"[{tags}] {logMessage}");
        }

        /// <summary>
        /// Log LAPB frame with enriched tags
        /// </summary>
        public static void LogLapbFrame(this Microsoft.Extensions.Logging.ILogger logger, LapbFrameEventArgs args, string? message = null)
        {
            if (logger == null || args == null) return;

            string tags = $"Layer=LAPB Direction={args.Direction} FrameType={args.FrameType} FrameId={args.FrameId}";
            string logMessage = message ?? $"LAPB frame {args.GetFrameSummary()}";
            
            logger.LogDebug($"[{tags}] {logMessage}");
        }

        /// <summary>
        /// Log X.25 packet with enriched tags
        /// </summary>
        public static void LogX25Packet(this Microsoft.Extensions.Logging.ILogger logger, X25PacketEventArgs args, string? message = null)
        {
            if (logger == null || args == null) return;

            string tags = $"Layer=X25 Direction={args.Direction} PacketType={args.Packet.PacketType} LCN={args.Packet.LCN} PacketId={args.PacketId}";
            string logMessage = message ?? $"X.25 packet {args.GetPacketSummary()}";
            
            logger.LogDebug($"[{tags}] {logMessage}");
        }

        /// <summary>
        /// Log with layer and direction tags
        /// </summary>
        public static void LogWithTags(this Microsoft.Extensions.Logging.ILogger logger, string component, LayerType layer, Direction direction, string message, long? frameId = null, long? packetId = null)
        {
            if (logger == null) return;

            string tags = $"Layer={layer} Direction={direction}";
            if (frameId.HasValue)
                tags += $" FrameId={frameId.Value}";
            if (packetId.HasValue)
                tags += $" PacketId={packetId.Value}";
            
            logger.LogDebug($"[{tags}] {message}");
        }

        /// <summary>
        /// Log runtime state change
        /// </summary>
        public static void LogRuntimeStateChange(this Microsoft.Extensions.Logging.ILogger logger, RuntimeStateChangedEventArgs args)
        {
            if (logger == null || args == null) return;

            logger.LogInformation("State change: {summary}", args.GetSummary());
        }

        /// <summary>
        /// Log module binding
        /// </summary>
        public static void LogModuleBinding(this Microsoft.Extensions.Logging.ILogger logger, ushort lcn, string moduleName, bool isBinding)
        {
            if (logger == null) return;

            string action = isBinding ? "bound" : "unbound";
            logger.LogInformation("LCN {lcn} {action} to module '{moduleName}'", lcn, action, moduleName);
        }

        /// <summary>
        /// Log routing decision
        /// </summary>
        public static void LogRoutingDecision(this Microsoft.Extensions.Logging.ILogger logger, string calledAddress, string callingAddress, string targetModule, bool accepted)
        {
            if (logger == null) return;

            string result = accepted ? "accepted" : "rejected";
            logger.LogInformation("Call routing: Called={calledAddress} Calling={callingAddress} -> {targetModule} ({result})", calledAddress, callingAddress, targetModule, result);
        }

        /// <summary>
        /// Log statistics update
        /// </summary>
        public static void LogStatisticsUpdate(this Microsoft.Extensions.Logging.ILogger logger, StatisticsSnapshot stats)
        {
            if (logger == null || stats == null) return;

            logger.LogDebug("Stats update: {summary}", stats.GetSummary());
        }

        /// <summary>
        /// Log history item added
        /// </summary>
        public static void LogHistoryItemAdded(this Microsoft.Extensions.Logging.ILogger logger, HistoryItem item)
        {
            if (logger == null || item == null) return;

            logger.LogDebug("History item added: {summary}", item.GetSummary());
        }

        /// <summary>
        /// Log timer event
        /// </summary>
        public static void LogTimerEvent(this Microsoft.Extensions.Logging.ILogger logger, string timerId, string action)
        {
            if (logger == null) return;

            logger.LogDebug("Timer {timerId}: {action}", timerId, action);
        }

        /// <summary>
        /// Log error with context
        /// </summary>
        public static void LogErrorWithContext(this Microsoft.Extensions.Logging.ILogger logger, string component, LayerType layer, Direction direction, string message, Exception? exception = null, long? frameId = null, long? packetId = null)
        {
            if (logger == null) return;

            string tags = $"Layer={layer} Direction={direction}";
            if (frameId.HasValue)
                tags += $" FrameId={frameId.Value}";
            if (packetId.HasValue)
                tags += $" PacketId={packetId.Value}";
            
            string logMessage = $"[{tags}] {message}";
            if (exception != null)
                logMessage += $" Exception: {exception.Message}";
            
            logger.LogError(exception, logMessage);
        }

        /// <summary>
        /// Log warning with context
        /// </summary>
        public static void LogWarningWithContext(this Microsoft.Extensions.Logging.ILogger logger, string component, LayerType layer, Direction direction, string message, long? frameId = null, long? packetId = null)
        {
            if (logger == null) return;

            string tags = $"Layer={layer} Direction={direction}";
            if (frameId.HasValue)
                tags += $" FrameId={frameId.Value}";
            if (packetId.HasValue)
                tags += $" PacketId={packetId.Value}";
            
            logger.LogWarning($"[{tags}] {message}");
        }
    }
}
