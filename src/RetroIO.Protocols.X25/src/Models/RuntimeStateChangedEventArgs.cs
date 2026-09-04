using System;

using RetroIO.Link.Models;
namespace RetroIO.Protocols.X25.Models
{
    /// <summary>
    /// Event arguments for runtime state changes
    /// </summary>
    public class RuntimeStateChangedEventArgs : EventArgs
    {
        /// <summary>
        /// Previous state
        /// </summary>
        public RuntimeState PreviousState { get; }

        /// <summary>
        /// Current state
        /// </summary>
        public RuntimeState CurrentState { get; }

        /// <summary>
        /// State change timestamp
        /// </summary>
        public DateTime Timestamp { get; }

        /// <summary>
        /// Optional reason for state change
        /// </summary>
        public string Reason { get; }

        /// <summary>
        /// Create runtime state change event arguments
        /// </summary>
        public RuntimeStateChangedEventArgs(RuntimeState previousState, RuntimeState currentState, string? reason = null)
        {
            PreviousState = previousState;
            CurrentState = currentState;
            Reason = reason ?? "";
            Timestamp = DateTime.UtcNow;
        }

        /// <summary>
        /// Get state change summary
        /// </summary>
        public string GetSummary()
        {
            string summary = $"Runtime State: {PreviousState} -> {CurrentState}";
            if (!string.IsNullOrEmpty(Reason))
                summary += $" ({Reason})";
            return summary;
        }
    }

    /// <summary>
    /// Runtime states
    /// </summary>
    public enum RuntimeState
    {
        /// <summary>
        /// Runtime is stopped
        /// </summary>
        Stopped = 0,

        /// <summary>
        /// Runtime is starting
        /// </summary>
        Starting = 1,

        /// <summary>
        /// Runtime is running
        /// </summary>
        Running = 2,

        /// <summary>
        /// Runtime is stopping
        /// </summary>
        Stopping = 3,

        /// <summary>
        /// Runtime encountered an error
        /// </summary>
        Error = 4
    }
}
