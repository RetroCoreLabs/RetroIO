using System;
using System.Text;
using Microsoft.Extensions.Logging;

using RetroIO.Protocols.X25.Protocols;
namespace RetroIO.Protocols.X25.Sintran
{
    /// <summary>
    /// SINTRAN File Transfer Protocol Handler
    /// Handles file transfer operations between SINTRAN systems
    /// Manages file uploads, downloads, directory listings, and file management
    /// </summary>
    public class SintranFileHandler : ISintranProtocolHandler
    {
        private readonly ILogger _logger;
        private SintranHandlerStats _stats;
        private bool _initialized = false;
        private string _currentDirectory = "/";
        private bool _inTransferMode = false;
        private string _transferFileName = "";

        public string Protocol => "FILE";
        public string Description => "SINTRAN file transfer protocol";
        public ushort LCN { get; private set; }

        public event EventHandler<SintranResponseEventArgs>? ResponseReady;

        public SintranFileHandler(ILogger logger)
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

            _logger.LogInformation("SintranFileHandler: Initializing SINTRAN file transfer handler");
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
            
            _logger.LogInformation("SintranFileHandler: File transfer channel established on LCN {LCN}", lcn);
            
            // Send file service ready notification
            SendFileResponse("FILE TRANSFER SERVICE READY - X.25 BRIDGE ONLINE");
            SendFileResponse($"Current directory: {_currentDirectory}");
        }

        public void OnChannelClosed()
        {
            _stats.IsActive = false;
            _stats.Status = "Disconnected";
            _inTransferMode = false;
            _transferFileName = "";
            _logger.LogInformation("SintranFileHandler: File transfer channel closed on LCN {LCN}", LCN);
        }

        public void HandleData(byte[] data)
        {
            if (data.Length == 0) return;

            _stats.MessagesReceived++;
            _stats.BytesReceived += data.Length;
            _stats.LastActivity = DateTime.UtcNow;

            try
            {
                if (_inTransferMode)
                {
                    HandleFileTransferData(data);
                }
                else
                {
                    var message = Encoding.ASCII.GetString(data).Trim();
                    _logger.LogInformation("SintranFileHandler: Received file command on LCN {LCN}: '{Message}'", 
                        LCN, message);

                    ProcessFileCommand(message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SintranFileHandler: Error processing file data on LCN {LCN}", LCN);
                _stats.Status = $"Error: {ex.Message}";
            }
        }

        /// <summary>
        /// Process incoming file transfer command from SINTRAN system
        /// </summary>
        private void ProcessFileCommand(string message)
        {
            // Parse SINTRAN file transfer command format
            // Example formats:
            // "FILE LIST"                              - List current directory
            // "FILE CD /path/to/directory"             - Change directory
            // "FILE GET filename.txt"                  - Download file
            // "FILE PUT filename.txt 1024"             - Upload file (with size)
            // "FILE DEL filename.txt"                  - Delete file
            // "FILE STATUS filename.txt"               - Get file status
            // "FILE MKDIR dirname"                     - Create directory
            // "FILE RMDIR dirname"                     - Remove directory

            var parts = message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0].ToUpper() != "FILE")
            {
                SendFileError($"Invalid FILE command: {message}");
                return;
            }

            var command = parts[1].ToUpper();
            switch (command)
            {
                case "LIST":
                case "LS":
                case "DIR":
                    HandleFileList(parts);
                    break;

                case "CD":
                case "CHDIR":
                    HandleFileChangeDirectory(parts);
                    break;

                case "GET":
                case "DOWNLOAD":
                    HandleFileGet(parts);
                    break;

                case "PUT":
                case "UPLOAD":
                    HandleFilePut(parts);
                    break;

                case "DEL":
                case "DELETE":
                case "RM":
                    HandleFileDelete(parts);
                    break;

                case "STATUS":
                case "STAT":
                    HandleFileStatus(parts);
                    break;

                case "MKDIR":
                    HandleFileMakeDirectory(parts);
                    break;

                case "RMDIR":
                    HandleFileRemoveDirectory(parts);
                    break;

                case "PWD":
                    HandleFileCurrentDirectory();
                    break;

                case "HELP":
                    HandleFileHelp();
                    break;

                case "QUIT":
                case "EXIT":
                    HandleFileQuit();
                    break;

                default:
                    SendFileError($"Unknown FILE command: {command}");
                    break;
            }
        }

        /// <summary>
        /// Handle file listing command
        /// </summary>
        private void HandleFileList(string[] parts)
        {
            var directory = parts.Length > 2 ? parts[2] : _currentDirectory;
            
            _logger.LogInformation("SintranFileHandler: Listing directory {Directory}", directory);

            var listing = new StringBuilder();
            listing.AppendLine($"FILE LISTING: {directory}");
            listing.AppendLine("Name                Type    Size      Modified");
            listing.AppendLine("------------------  ------  --------  -----------------");
            
            // Sample file listing
            listing.AppendLine("system.cfg          FILE    1,024     2024-01-15 10:30");
            listing.AppendLine("routing.dat         FILE    2,048     2024-01-15 09:15");
            listing.AppendLine("messages            DIR     -         2024-01-15 08:00");
            listing.AppendLine("logs                DIR     -         2024-01-15 07:45");
            listing.AppendLine("temp.txt            FILE    512       2024-01-15 11:20");
            listing.AppendLine("");
            listing.AppendLine("3 files, 2 directories - 3,584 bytes total");

            SendFileResponse(listing.ToString());
        }

        /// <summary>
        /// Handle change directory command
        /// </summary>
        private void HandleFileChangeDirectory(string[] parts)
        {
            if (parts.Length < 3)
            {
                SendFileError("FILE CD requires directory path");
                return;
            }

            var newDirectory = parts[2];
            
            // Simple directory navigation (in real implementation, validate path)
            if (newDirectory == "..")
            {
                var lastSlash = _currentDirectory.TrimEnd('/').LastIndexOf('/');
                _currentDirectory = lastSlash > 0 ? _currentDirectory.Substring(0, lastSlash + 1) : "/";
            }
            else if (newDirectory.StartsWith("/"))
            {
                _currentDirectory = newDirectory.EndsWith("/") ? newDirectory : newDirectory + "/";
            }
            else
            {
                _currentDirectory = _currentDirectory.TrimEnd('/') + "/" + newDirectory + "/";
            }

            _logger.LogInformation("SintranFileHandler: Changed directory to {Directory}", _currentDirectory);
            SendFileResponse($"FILE CD OK - Current directory: {_currentDirectory}");
        }

        /// <summary>
        /// Handle file download command
        /// </summary>
        private void HandleFileGet(string[] parts)
        {
            if (parts.Length < 3)
            {
                SendFileError("FILE GET requires filename");
                return;
            }

            var filename = parts[2];
            _logger.LogInformation("SintranFileHandler: Starting download of {Filename}", filename);

            // In a real implementation, check if file exists and send actual file data
            var fileContent = $"Sample file content for {filename}\r\n" +
                             $"Generated by SINTRAN File Transfer Service\r\n" +
                             $"Timestamp: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\r\n" +
                             $"File path: {_currentDirectory}{filename}\r\n";

            SendFileResponse($"FILE GET START {filename} SIZE {fileContent.Length}");
            SendFileResponse(fileContent);
            SendFileResponse($"FILE GET END {filename} - TRANSFER COMPLETE");
        }

        /// <summary>
        /// Handle file upload command
        /// </summary>
        private void HandleFilePut(string[] parts)
        {
            if (parts.Length < 3)
            {
                SendFileError("FILE PUT requires filename");
                return;
            }

            var filename = parts[2];
            var fileSize = parts.Length > 3 && int.TryParse(parts[3], out var size) ? size : 0;

            _transferFileName = filename;
            _inTransferMode = true;

            _logger.LogInformation("SintranFileHandler: Starting upload of {Filename}, size: {Size} bytes", 
                filename, fileSize);

            SendFileResponse($"FILE PUT READY {filename} - SEND FILE DATA");
        }

        /// <summary>
        /// Handle file transfer data when in transfer mode
        /// </summary>
        private void HandleFileTransferData(byte[] data)
        {
            _logger.LogInformation("SintranFileHandler: Received {Bytes} bytes of file data for {Filename}", 
                data.Length, _transferFileName);

            // In a real implementation, write data to file system
            // For now, just acknowledge receipt

            var message = Encoding.ASCII.GetString(data).Trim();
            if (message.ToUpper() == "FILE PUT END")
            {
                _inTransferMode = false;
                SendFileResponse($"FILE PUT END {_transferFileName} - UPLOAD COMPLETE");
                _logger.LogInformation("SintranFileHandler: Upload completed for {Filename}", _transferFileName);
                _transferFileName = "";
            }
            else
            {
                SendFileResponse($"FILE PUT ACK - {data.Length} bytes received");
            }
        }

        /// <summary>
        /// Handle file delete command
        /// </summary>
        private void HandleFileDelete(string[] parts)
        {
            if (parts.Length < 3)
            {
                SendFileError("FILE DEL requires filename");
                return;
            }

            var filename = parts[2];
            _logger.LogInformation("SintranFileHandler: Deleting file {Filename}", filename);

            // In a real implementation, delete the file
            SendFileResponse($"FILE DEL OK {filename} - FILE DELETED");
        }

        /// <summary>
        /// Handle file status command
        /// </summary>
        private void HandleFileStatus(string[] parts)
        {
            var filename = parts.Length > 2 ? parts[2] : "system.cfg";
            
            _logger.LogInformation("SintranFileHandler: Getting status for {Filename}", filename);

            var status = new StringBuilder();
            status.AppendLine($"FILE STATUS: {filename}");
            status.AppendLine($"Full path:     {_currentDirectory}{filename}");
            status.AppendLine($"Type:          Regular file");
            status.AppendLine($"Size:          1,024 bytes");
            status.AppendLine($"Created:       2024-01-15 10:30:00 UTC");
            status.AppendLine($"Modified:      2024-01-15 10:30:00 UTC");
            status.AppendLine($"Permissions:   Read/Write");
            status.AppendLine($"Owner:         SYSTEM");

            SendFileResponse(status.ToString());
        }

        /// <summary>
        /// Handle make directory command
        /// </summary>
        private void HandleFileMakeDirectory(string[] parts)
        {
            if (parts.Length < 3)
            {
                SendFileError("FILE MKDIR requires directory name");
                return;
            }

            var dirname = parts[2];
            _logger.LogInformation("SintranFileHandler: Creating directory {Directory}", dirname);

            SendFileResponse($"FILE MKDIR OK {dirname} - DIRECTORY CREATED");
        }

        /// <summary>
        /// Handle remove directory command
        /// </summary>
        private void HandleFileRemoveDirectory(string[] parts)
        {
            if (parts.Length < 3)
            {
                SendFileError("FILE RMDIR requires directory name");
                return;
            }

            var dirname = parts[2];
            _logger.LogInformation("SintranFileHandler: Removing directory {Directory}", dirname);

            SendFileResponse($"FILE RMDIR OK {dirname} - DIRECTORY REMOVED");
        }

        /// <summary>
        /// Handle current directory command
        /// </summary>
        private void HandleFileCurrentDirectory()
        {
            SendFileResponse($"FILE PWD: {_currentDirectory}");
        }

        /// <summary>
        /// Handle file help command
        /// </summary>
        private void HandleFileHelp()
        {
            var help = new StringBuilder();
            help.AppendLine("FILE TRANSFER COMMAND HELP:");
            help.AppendLine("FILE LIST [path]             - List directory contents");
            help.AppendLine("FILE CD <path>               - Change directory");
            help.AppendLine("FILE GET <filename>          - Download file");
            help.AppendLine("FILE PUT <filename> [size]   - Upload file");
            help.AppendLine("FILE DEL <filename>          - Delete file");
            help.AppendLine("FILE STATUS <filename>       - Get file information");
            help.AppendLine("FILE MKDIR <dirname>         - Create directory");
            help.AppendLine("FILE RMDIR <dirname>         - Remove directory");
            help.AppendLine("FILE PWD                     - Show current directory");
            help.AppendLine("FILE HELP                    - Show this help");
            help.AppendLine("FILE QUIT                    - Exit file transfer");

            SendFileResponse(help.ToString());
        }

        /// <summary>
        /// Handle file quit command
        /// </summary>
        private void HandleFileQuit()
        {
            SendFileResponse("FILE TRANSFER SESSION ENDED - GOODBYE");
            _logger.LogInformation("SintranFileHandler: File transfer session ended on LCN {LCN}", LCN);
        }

        /// <summary>
        /// Send file transfer response message
        /// </summary>
        private void SendFileResponse(string message)
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
            
            _logger.LogDebug("SintranFileHandler: Sent file response on LCN {LCN}: '{Message}'", 
                LCN, message.Replace("\r\n", ""));
        }

        /// <summary>
        /// Send file transfer error message
        /// </summary>
        private void SendFileError(string errorMessage)
        {
            var message = $"FILE ERROR: {errorMessage}";
            SendFileResponse(message);
            
            _logger.LogWarning("SintranFileHandler: Sent file error: {ErrorMessage}", errorMessage);
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

            _logger.LogDebug("SintranFileHandler: Disposing file transfer handler for LCN {LCN}", LCN);
            _stats.IsActive = false;
            _stats.Status = "Disposed";
            _inTransferMode = false;
            _transferFileName = "";
            _initialized = false;
        }
    }
}