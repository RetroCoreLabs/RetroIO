//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace RetroIO.Protocols.X25.Common
{
    /// <summary>
    /// One entry in the log.
    /// </summary>
    public class LogEntry
    {
        /// <summary>
        /// Gets or sets the time the entry was created.
        /// </summary>
        public DateTime Timestamp { get; set; }
        /// <summary>
        /// Gets or sets the level of the entry.
        /// </summary>
        public LogLevel Level { get; set; }
        /// <summary>
        /// Gets or sets the category of the entry.
        /// </summary>
        public string Category { get; set; } = "";
        /// <summary>
        /// Gets or sets the message text.
        /// </summary>
        public string Message { get; set; } = "";
        /// <summary>
        /// Gets the entry as one line: the time, level and category each in square brackets, then the message.
        /// </summary>
        public string FullMessage => $"[{Timestamp:HH:mm:ss.fff}] [{Level}] [{Category}] {Message}";
    }

    /// <summary>
    /// A singleton logger that keeps the last 1000 entries in memory and raises an event for each new entry.
    /// </summary>
    public class Logger
    {
        private static readonly object _lock = new object();
        private static Logger? _instance;
        private readonly List<LogEntry> _logEntries = new List<LogEntry>();
        private readonly int _maxEntries = 1000; // Keep last 1000 entries
        
        /// <summary>
        /// Occurs for each new entry. It is raised on a thread pool thread.
        /// </summary>
        public event Action<LogEntry>? LogEntryAdded;
        
        /// <summary>
        /// Gets the single logger instance.
        /// </summary>
        public static Logger Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new Logger();
                    }
                }
                return _instance;
            }
        }
        
        private Logger() { }
        
        /// <summary>
        /// Adds an entry with the current local time.
        /// </summary>
        /// <remarks>
        /// When more than 1000 entries are stored, the oldest one is removed.
        /// </remarks>
        /// <param name="level">
        /// The level of the entry.
        /// </param>
        /// <param name="category">
        /// The category of the entry.
        /// </param>
        /// <param name="message">
        /// The message text.
        /// </param>
        public void Log(LogLevel level, string category, string message)
        {
            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Level = level,
                Category = category,
                Message = message
            };
            
            lock (_logEntries)
            {
                _logEntries.Add(entry);
                
                // Keep only the last maxEntries
                if (_logEntries.Count > _maxEntries)
                {
                    _logEntries.RemoveAt(0);
                }
            }
            
            // Notify subscribers on a background thread to avoid blocking
            ThreadPool.QueueUserWorkItem(_ => LogEntryAdded?.Invoke(entry));
        }
        
        /// <summary>
        /// Adds an entry with the Debug level.
        /// </summary>
        /// <param name="category">
        /// The category of the entry.
        /// </param>
        /// <param name="message">
        /// The message text.
        /// </param>
        public void Debug(string category, string message)
        {
            Log(LogLevel.Debug, category, message);
        }
        
        /// <summary>
        /// Adds an entry with the Info level.
        /// </summary>
        /// <param name="category">
        /// The category of the entry.
        /// </param>
        /// <param name="message">
        /// The message text.
        /// </param>
        public void Info(string category, string message)
        {
            Log(LogLevel.Info, category, message);
        }
        
        /// <summary>
        /// Adds an entry with the Warning level.
        /// </summary>
        /// <param name="category">
        /// The category of the entry.
        /// </param>
        /// <param name="message">
        /// The message text.
        /// </param>
        public void Warning(string category, string message)
        {
            Log(LogLevel.Warning, category, message);
        }
        
        /// <summary>
        /// Adds an entry with the Error level.
        /// </summary>
        /// <param name="category">
        /// The category of the entry.
        /// </param>
        /// <param name="message">
        /// The message text.
        /// </param>
        public void Error(string category, string message)
        {
            Log(LogLevel.Error, category, message);
        }
        
        /// <summary>
        /// Returns a copy of all stored entries.
        /// </summary>
        /// <returns>
        /// A new list with the entries, oldest first.
        /// </returns>
        public List<LogEntry> GetLogEntries()
        {
            lock (_logEntries)
            {
                return new List<LogEntry>(_logEntries);
            }
        }
        
        /// <summary>
        /// Returns a copy of the stored entries at or above a level.
        /// </summary>
        /// <param name="minLevel">
        /// The lowest level to include.
        /// </param>
        /// <returns>
        /// A new list with the matching entries, oldest first.
        /// </returns>
        public List<LogEntry> GetLogEntries(LogLevel minLevel)
        {
            lock (_logEntries)
            {
                return _logEntries.FindAll(entry => entry.Level >= minLevel);
            }
        }
        
        /// <summary>
        /// Removes all stored entries.
        /// </summary>
        public void Clear()
        {
            lock (_logEntries)
            {
                _logEntries.Clear();
            }
        }
        
        /// <summary>
        /// Returns the number of stored entries.
        /// </summary>
        /// <returns>
        /// The number of entries.
        /// </returns>
        public int GetEntryCount()
        {
            lock (_logEntries)
            {
                return _logEntries.Count;
            }
        }
    }
}
