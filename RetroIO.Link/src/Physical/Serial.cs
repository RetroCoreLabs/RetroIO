using System;
using System.IO.Ports;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace RetroIO.Link.Physical
{
    /// <summary>
    /// Serial transport for X.25 communication over RS-232/RS-485
    /// Handles serial port configuration, data transmission, and event management
    /// Supports mock mode for testing without hardware dependencies
    /// </summary>
    public class Serial : IDisposable
    {
        private readonly ILogger _logger;
        private readonly SerialConfig _config;
        private readonly bool _mockMode;
        private SerialPort? _serialPort;
        private Thread? _readerThread;
        private volatile bool _isRunning;
        private readonly object _lock = new object();
        
        // Mock mode data structures
        private readonly List<byte[]> _mockSentData = new();
        private readonly Queue<byte[]> _mockIncomingData = new();
        
        // Statistics
        private long _bytesTransmitted;
        private long _bytesReceived;
        private int _connectionErrors;
        
        /// <summary>
        /// Serial port configuration
        /// </summary>
        public SerialConfig Config => _config;
        
        /// <summary>
        /// Serial port status
        /// </summary>
        public bool IsActive { get; private set; }
        
        /// <summary>
        /// Event fired when data is received
        /// </summary>
        public event EventHandler<SerialDataEventArgs>? DataReceived;
        
        /// <summary>
        /// Event fired when status changes
        /// </summary>
        public event EventHandler<SerialStatusEventArgs>? StatusChanged;
        
        public Serial(ILogger logger, SerialConfig config, bool mockMode = false)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _mockMode = mockMode;
        }
        
        /// <summary>
        /// Start serial port communication
        /// </summary>
        public bool Start()
        {
            lock (_lock)
            {
                if (_isRunning)
                {
                    _logger.LogWarning("Serial: Already started");
                    return true;
                }
                
                try
                {
                    if (_mockMode)
                    {
                        // Mock mode - no actual serial port
                        _isRunning = true;
                        IsActive = true;
                        
                        // Start mock processing thread
                        _readerThread = new Thread(MockProcessingThread)
                        {
                            Name = "Mock-Serial-Processing",
                            IsBackground = true
                        };
                        _readerThread.Start();
                        
                        _logger.LogInformation("MockSerial: Started on mock {PortName} at {BaudRate} baud", 
                                             _config.PortName, _config.BaudRate);
                        
                        OnStatusChanged("Started", null);
                        return true;
                    }
                    else
                    {
                        // Real serial port mode
                        _serialPort = new SerialPort
                        {
                            PortName = _config.PortName,
                            BaudRate = _config.BaudRate,
                            DataBits = _config.DataBits,
                            Parity = _config.Parity,
                            StopBits = _config.StopBits,
                            Handshake = Handshake.None,
                            ReadTimeout = 1000,
                            WriteTimeout = 1000,
                            ReceivedBytesThreshold = 1,
                            DtrEnable = _config.DtrEnable,
                            RtsEnable = _config.RtsEnable
                        };
                        
                        _serialPort.Open();
                        _isRunning = true;
                        IsActive = true;
                        
                        // Start reader thread
                        _readerThread = new Thread(SerialReaderThread)
                        {
                            Name = "Serial-Reader",
                            IsBackground = true
                        };
                        _readerThread.Start();
                        
                        _logger.LogInformation("Serial: Started on {PortName} at {BaudRate} baud", 
                                             _config.PortName, _config.BaudRate);
                        
                        OnStatusChanged("Started", null);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Serial: Failed to start on {PortName}", _config.PortName);
                    _connectionErrors++;
                    IsActive = false;
                    OnStatusChanged("Failed to start", ex);
                    return false;
                }
            }
        }
        
        /// <summary>
        /// Stop serial port communication
        /// </summary>
        public void Stop()
        {
            lock (_lock)
            {
                if (!_isRunning)
                    return;
                
                _isRunning = false;
                
                try
                {
                    // Stop reader thread
                    _readerThread?.Join(2000);
                    
                    // Close serial port
                    if (_serialPort?.IsOpen == true)
                    {
                        _serialPort.Close();
                    }
                    
                    IsActive = false;
                    _logger.LogInformation("Serial: Stopped");
                    OnStatusChanged("Stopped", null);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Serial: Error stopping");
                    OnStatusChanged("Stop error", ex);
                }
            }
        }
        
        /// <summary>
        /// Send data via serial port
        /// </summary>
        public bool SendData(byte[] data)
        {
            if (data == null || data.Length == 0)
                return false;
                
            lock (_lock)
            {
                if (!_isRunning)
                {
                    _logger.LogWarning("Serial: Cannot send data - port not running");
                    return false;
                }
                
                try
                {
                    if (_mockMode)
                    {
                        // Mock mode - capture sent data for test validation
                        var dataCopy = new byte[data.Length];
                        Array.Copy(data, 0, dataCopy, 0, data.Length);
                        _mockSentData.Add(dataCopy);
                        
                        _bytesTransmitted += data.Length;
                        _logger.LogDebug("MockSerial: Sent {ByteCount} bytes", data.Length);
                        return true;
                    }
                    else
                    {
                        // Real serial port mode
                        if (_serialPort?.IsOpen != true)
                        {
                            _logger.LogWarning("Serial: Cannot send data - port not open");
                            return false;
                        }
                        
                        _serialPort.Write(data, 0, data.Length);
                        _bytesTransmitted += data.Length;
                        
                        _logger.LogDebug("Serial: Sent {ByteCount} bytes", data.Length);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Serial: Failed to send {ByteCount} bytes", data.Length);
                    _connectionErrors++;
                    return false;
                }
            }
        }
        
        /// <summary>
        /// Get serial transport statistics
        /// </summary>
        public SerialStatistics GetStatistics()
        {
            return new SerialStatistics
            {
                PortName = _config.PortName,
                BaudRate = _config.BaudRate,
                IsActive = IsActive,
                BytesTransmitted = _bytesTransmitted,
                BytesReceived = _bytesReceived,
                ConnectionErrors = _connectionErrors
            };
        }
        
        /// <summary>
        /// Serial reader thread
        /// </summary>
        private void SerialReaderThread()
        {
            var buffer = new byte[1024];
            
            _logger.LogDebug("Serial: Reader thread started");
            
            while (_isRunning && _serialPort?.IsOpen == true)
            {
                try
                {
                    int bytesToRead = _serialPort.BytesToRead;
                    if (bytesToRead > 0)
                    {
                        int bytesRead = _serialPort.Read(buffer, 0, Math.Min(bytesToRead, buffer.Length));
                        if (bytesRead > 0)
                        {
                            _bytesReceived += bytesRead;
                            
                            var data = new byte[bytesRead];
                            Array.Copy(buffer, 0, data, 0, bytesRead);
                            
                            _logger.LogDebug("Serial: Received {ByteCount} bytes", bytesRead);
                            OnDataReceived(data);
                        }
                    }
                    
                    Thread.Sleep(10); // Small delay to prevent busy waiting
                }
                catch (Exception ex) when (_isRunning)
                {
                    _logger.LogError(ex, "Serial: Reader thread error");
                    _connectionErrors++;
                    Thread.Sleep(1000); // Wait before retry
                }
            }
            
            _logger.LogDebug("Serial: Reader thread stopped");
        }
        
        /// <summary>
        /// Mock processing thread that handles injected incoming data
        /// </summary>
        private void MockProcessingThread()
        {
            _logger.LogDebug("MockSerial: Processing thread started");
            
            while (_isRunning)
            {
                try
                {
                    byte[]? dataToProcess = null;
                    
                    lock (_lock)
                    {
                        if (_mockIncomingData.Count > 0)
                        {
                            dataToProcess = _mockIncomingData.Dequeue();
                        }
                    }
                    
                    if (dataToProcess != null)
                    {
                        _bytesReceived += dataToProcess.Length;
                        _logger.LogDebug("MockSerial: Processing {ByteCount} injected bytes", dataToProcess.Length);
                        OnDataReceived(dataToProcess);
                    }
                    
                    Thread.Sleep(10); // Small delay to prevent busy waiting
                }
                catch (Exception ex) when (_isRunning)
                {
                    _logger.LogError(ex, "MockSerial: Processing thread error");
                    _connectionErrors++;
                    Thread.Sleep(1000); // Wait before retry
                }
            }
            
            _logger.LogDebug("MockSerial: Processing thread stopped");
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
                _logger.LogError(ex, "Serial: Error in DataReceived event handler");
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
                _logger.LogError(ex, "Serial: Error in StatusChanged event handler");
            }
        }
        
        /// <summary>
        /// Inject incoming data for testing (mock mode only)
        /// </summary>
        public void InjectIncomingData(byte[] data)
        {
            if (!_mockMode || data == null || data.Length == 0)
                return;
                
            lock (_lock)
            {
                if (!_isRunning)
                {
                    _logger.LogWarning("MockSerial: Cannot inject data - port not running");
                    return;
                }
                
                var dataCopy = new byte[data.Length];
                Array.Copy(data, 0, dataCopy, 0, data.Length);
                _mockIncomingData.Enqueue(dataCopy);
                
                _logger.LogDebug("MockSerial: Injected {ByteCount} bytes for processing", data.Length);
            }
        }
        
        /// <summary>
        /// Get captured sent data for test validation (mock mode only)
        /// </summary>
        public IReadOnlyList<byte[]> GetSentData()
        {
            if (!_mockMode)
                return new List<byte[]>().AsReadOnly();
                
            lock (_lock)
            {
                return _mockSentData.AsReadOnly();
            }
        }
        
        /// <summary>
        /// Clear captured sent data (mock mode only)
        /// </summary>
        public void ClearSentData()
        {
            if (!_mockMode)
                return;
                
            lock (_lock)
            {
                _mockSentData.Clear();
                _logger.LogDebug("MockSerial: Cleared captured sent data");
            }
        }
        
        /// <summary>
        /// Get the last sent data packet (mock mode only)
        /// </summary>
        public byte[]? GetLastSentData()
        {
            if (!_mockMode)
                return null;
                
            lock (_lock)
            {
                return _mockSentData.Count > 0 ? _mockSentData[_mockSentData.Count - 1] : null;
            }
        }
        
        /// <summary>
        /// Check if specific data was sent (mock mode only)
        /// </summary>
        public bool WasSent(byte[] expectedData)
        {
            if (!_mockMode || expectedData == null)
                return false;
                
            lock (_lock)
            {
                foreach (var sentPacket in _mockSentData)
                {
                    if (sentPacket.Length == expectedData.Length)
                    {
                        bool matches = true;
                        for (int i = 0; i < expectedData.Length; i++)
                        {
                            if (sentPacket[i] != expectedData[i])
                            {
                                matches = false;
                                break;
                            }
                        }
                        if (matches)
                            return true;
                    }
                }
                return false;
            }
        }

        public void Dispose()
        {
            Stop();
            _serialPort?.Dispose();
        }
    }
    
    /// <summary>
    /// Serial transport configuration
    /// </summary>
    public class SerialConfig
    {
        public string PortName { get; set; } = "COM1";
        public int BaudRate { get; set; } = 9600;
        public int DataBits { get; set; } = 8;
        public Parity Parity { get; set; } = Parity.None;
        public StopBits StopBits { get; set; } = StopBits.One;
        public bool DtrEnable { get; set; } = true;
        public bool RtsEnable { get; set; } = true;
    }
    
    /// <summary>
    /// Serial transport statistics
    /// </summary>
    public class SerialStatistics
    {
        public string PortName { get; set; } = "";
        public int BaudRate { get; set; }
        public bool IsActive { get; set; }
        public long BytesTransmitted { get; set; }
        public long BytesReceived { get; set; }
        public int ConnectionErrors { get; set; }
        
        public string GetSummary()
        {
            return $"Serial {PortName}: {(IsActive ? "Active" : "Inactive")}, " +
                   $"TX: {BytesTransmitted}, RX: {BytesReceived}, Errors: {ConnectionErrors}";
        }
    }
    
    /// <summary>
    /// Serial data received event arguments
    /// </summary>
    public class SerialDataEventArgs : EventArgs
    {
        public byte[] Data { get; }
        public DateTime Timestamp { get; }
        
        public SerialDataEventArgs(byte[] data)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
            Timestamp = DateTime.UtcNow;
        }
    }
    
    /// <summary>
    /// Serial status change event arguments
    /// </summary>
    public class SerialStatusEventArgs : EventArgs
    {
        public string Status { get; }
        public Exception? Error { get; }
        public DateTime Timestamp { get; }
        
        public SerialStatusEventArgs(string status, Exception? error)
        {
            Status = status ?? "";
            Error = error;
            Timestamp = DateTime.UtcNow;
        }
    }
}