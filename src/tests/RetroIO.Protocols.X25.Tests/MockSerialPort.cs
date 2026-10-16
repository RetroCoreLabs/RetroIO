using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Physical;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Mock serial port implementation for unit testing
    /// Captures transmitted data and can simulate received data
    /// </summary>
    public class MockSerialPort : IDisposable
    {
        private readonly ILogger _logger;
        private readonly List<byte[]> _transmittedData = new List<byte[]>();
        private readonly Queue<byte[]> _simulatedIncomingData = new Queue<byte[]>();
        private readonly object _lock = new object();
        private bool _isOpen = false;
        private volatile bool _isRunning = false;
        private Thread? _readerThread;
        
        // Statistics
        private long _bytesTransmitted;
        private long _bytesReceived;
        
        /// <summary>
        /// Serial configuration
        /// </summary>
        public SerialConfig Config { get; }
        
        /// <summary>
        /// Is the mock serial port open
        /// </summary>
        public bool IsOpen => _isOpen;
        
        /// <summary>
        /// Get all transmitted data
        /// </summary>
        public List<byte[]> TransmittedData 
        { 
            get 
            { 
                lock (_transmittedData) 
                { 
                    return new List<byte[]>(_transmittedData); 
                } 
            } 
        }
        
        /// <summary>
        /// Get total bytes transmitted
        /// </summary>
        public long BytesTransmitted => _bytesTransmitted;
        
        /// <summary>
        /// Get total bytes received
        /// </summary>
        public long BytesReceived => _bytesReceived;
        
        /// <summary>
        /// Event fired when data is received
        /// </summary>
        public event EventHandler<SerialDataEventArgs>? DataReceived;
        
        /// <summary>
        /// Event fired when status changes
        /// </summary>
        public event EventHandler<SerialStatusEventArgs>? StatusChanged;
        
        public MockSerialPort(ILogger logger, SerialConfig config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            Config = config ?? throw new ArgumentNullException(nameof(config));
        }
        
        /// <summary>
        /// Open the mock serial port
        /// </summary>
        public bool Start()
        {
            lock (_lock)
            {
                if (_isOpen)
                {
                    _logger.LogWarning("MockSerial: Already open");
                    return true;
                }
                
                try
                {
                    _isOpen = true;
                    _isRunning = true;
                    
                    // Start reader thread to process simulated incoming data
                    _readerThread = new Thread(ReaderThread)
                    {
                        Name = "MockSerial-Reader",
                        IsBackground = true
                    };
                    _readerThread.Start();
                    
                    _logger.LogInformation("MockSerial: Started on {PortName} at {BaudRate} baud", 
                                         Config.PortName, Config.BaudRate);
                    
                    OnStatusChanged("Started", null);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "MockSerial: Failed to start on {PortName}", Config.PortName);
                    OnStatusChanged("Failed to start", ex);
                    return false;
                }
            }
        }
        
        /// <summary>
        /// Close the mock serial port
        /// </summary>
        public void Stop()
        {
            lock (_lock)
            {
                if (!_isOpen)
                    return;
                
                _isRunning = false;
                _isOpen = false;
                
                // Stop reader thread
                _readerThread?.Join(1000);
                
                _logger.LogInformation("MockSerial: Stopped");
                OnStatusChanged("Stopped", null);
            }
        }
        
        /// <summary>
        /// Send data via mock serial port (captures the data)
        /// </summary>
        public bool SendData(byte[] data)
        {
            if (data == null || data.Length == 0)
                return false;
                
            lock (_transmittedData)
            {
                if (!_isOpen)
                {
                    _logger.LogWarning("MockSerial: Cannot send data - port not open");
                    return false;
                }
                
                // Capture transmitted data
                var dataCopy = new byte[data.Length];
                Array.Copy(data, dataCopy, data.Length);
                _transmittedData.Add(dataCopy);
                _bytesTransmitted += data.Length;
                
                _logger.LogDebug("MockSerial: Captured {ByteCount} transmitted bytes: {Hex}", 
                               data.Length, Convert.ToHexString(data));
                return true;
            }
        }
        
        /// <summary>
        /// Simulate receiving data from the serial port
        /// </summary>
        public void SimulateReceivedData(byte[] data)
        {
            if (data == null || data.Length == 0 || !_isOpen)
                return;
                
            lock (_simulatedIncomingData)
            {
                var dataCopy = new byte[data.Length];
                Array.Copy(data, dataCopy, data.Length);
                _simulatedIncomingData.Enqueue(dataCopy);
                
                _logger.LogDebug("MockSerial: Queued {ByteCount} bytes for simulation: {Hex}", 
                               data.Length, Convert.ToHexString(data));
            }
        }
        
        /// <summary>
        /// Clear all captured transmitted data
        /// </summary>
        public void ClearTransmittedData()
        {
            lock (_transmittedData)
            {
                _transmittedData.Clear();
                _bytesTransmitted = 0;
            }
        }
        
        /// <summary>
        /// Get serial transport statistics
        /// </summary>
        public SerialStatistics GetStatistics()
        {
            return new SerialStatistics
            {
                PortName = Config.PortName,
                BaudRate = Config.BaudRate,
                IsActive = _isOpen,
                BytesTransmitted = _bytesTransmitted,
                BytesReceived = _bytesReceived,
                ConnectionErrors = 0
            };
        }
        
        /// <summary>
        /// Reader thread that processes simulated incoming data
        /// </summary>
        private void ReaderThread()
        {
            _logger.LogDebug("MockSerial: Reader thread started");
            
            while (_isRunning)
            {
                try
                {
                    byte[]? data = null;
                    
                    lock (_simulatedIncomingData)
                    {
                        if (_simulatedIncomingData.Count > 0)
                        {
                            data = _simulatedIncomingData.Dequeue();
                        }
                    }
                    
                    if (data != null)
                    {
                        _bytesReceived += data.Length;
                        _logger.LogDebug("MockSerial: Processing {ByteCount} simulated bytes", data.Length);
                        OnDataReceived(data);
                    }
                    
                    Thread.Sleep(10); // Small delay
                }
                catch (Exception ex) when (_isRunning)
                {
                    _logger.LogError(ex, "MockSerial: Reader thread error");
                    Thread.Sleep(100);
                }
            }
            
            _logger.LogDebug("MockSerial: Reader thread stopped");
        }
        
        /// <summary>
        /// Fire DataReceived event
        /// </summary>
        private void OnDataReceived(byte[] data)
        {
            try
            {
                DataReceived?.Invoke(this, new SerialDataEventArgs(data));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MockSerial: Error in DataReceived event handler");
            }
        }
        
        /// <summary>
        /// Fire StatusChanged event
        /// </summary>
        private void OnStatusChanged(string status, Exception? error)
        {
            try
            {
                StatusChanged?.Invoke(this, new SerialStatusEventArgs(status, error));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MockSerial: Error in StatusChanged event handler");
            }
        }
        
        public void Dispose()
        {
            Stop();
        }
    }
    
    /// <summary>
    /// Helper class to format captured serial data for analysis
    /// </summary>
    public static class SerialDataFormatter
    {
        public static string FormatHexDump(byte[] data, string label = "")
        {
            if (data == null || data.Length == 0)
                return $"{label}: (empty)";
                
            var hex = Convert.ToHexString(data);
            var formatted = string.Join("-", Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));
            
            return $"{label}: {formatted} ({data.Length} bytes)";
        }
        
        public static string FormatAllTransmissions(List<byte[]> transmissions)
        {
            var result = new StringBuilder();
            result.AppendLine($"Total transmissions: {transmissions.Count}");
            
            for (int i = 0; i < transmissions.Count; i++)
            {
                result.AppendLine($"  [{i + 1}] {FormatHexDump(transmissions[i])}");
            }
            
            return result.ToString();
        }
        
        public static bool ContainsHdlcFrame(byte[] data)
        {
            // Check for HDLC frame markers (0x7E flag)
            return data.Contains((byte)0x7E);
        }
        
        public static bool ContainsSabmFrame(byte[] data)
        {
            // Look for SABM pattern in HDLC frame
            // SABM has control field 0x3F (when P/F bit is set)
            if (data.Length < 4) return false;
            
            // Look for pattern: 0x7E address 0x3F ...
            for (int i = 0; i < data.Length - 3; i++)
            {
                if (data[i] == 0x7E && data[i + 2] == 0x3F)
                {
                    return true;
                }
            }
            return false;
        }
        
        public static bool ContainsUaFrame(byte[] data)
        {
            // Look for UA pattern in HDLC frame  
            // UA has control field 0x73 (when P/F bit is set)
            if (data.Length < 4) return false;
            
            for (int i = 0; i < data.Length - 3; i++)
            {
                if (data[i] == 0x7E && data[i + 2] == 0x73)
                {
                    return true;
                }
            }
            return false;
        }
    }
}