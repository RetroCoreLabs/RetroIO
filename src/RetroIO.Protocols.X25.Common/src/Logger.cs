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
    public class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public LogLevel Level { get; set; }
        public string Category { get; set; } = "";
        public string Message { get; set; } = "";
        public string FullMessage => $"[{Timestamp:HH:mm:ss.fff}] [{Level}] [{Category}] {Message}";
    }

    public class Logger
    {
        private static readonly object _lock = new object();
        private static Logger? _instance;
        private readonly List<LogEntry> _logEntries = new List<LogEntry>();
        private readonly int _maxEntries = 1000; // Keep last 1000 entries
        
        public event Action<LogEntry>? LogEntryAdded;
        
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
        
        public void Debug(string category, string message)
        {
            Log(LogLevel.Debug, category, message);
        }
        
        public void Info(string category, string message)
        {
            Log(LogLevel.Info, category, message);
        }
        
        public void Warning(string category, string message)
        {
            Log(LogLevel.Warning, category, message);
        }
        
        public void Error(string category, string message)
        {
            Log(LogLevel.Error, category, message);
        }
        
        public List<LogEntry> GetLogEntries()
        {
            lock (_logEntries)
            {
                return new List<LogEntry>(_logEntries);
            }
        }
        
        public List<LogEntry> GetLogEntries(LogLevel minLevel)
        {
            lock (_logEntries)
            {
                return _logEntries.FindAll(entry => entry.Level >= minLevel);
            }
        }
        
        public void Clear()
        {
            lock (_logEntries)
            {
                _logEntries.Clear();
            }
        }
        
        public int GetEntryCount()
        {
            lock (_logEntries)
            {
                return _logEntries.Count;
            }
        }
    }
}
