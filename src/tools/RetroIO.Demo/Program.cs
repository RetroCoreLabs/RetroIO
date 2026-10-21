//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using RetroIO.Server.Core.FileSystem;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local;
using RetroIO.Storage.Local.Drivers;
using RetroIO.Storage.Ndfs.Drivers;
using RetroIO.Storage.Providers;
using RetroIO.Storage.Utilities;

namespace RetroIO.Demo
{
    class Program
    {
        private static RetroVFS? _vfs;

        static void Main(string[] args)
        {
            PrintBanner();

            // Initialize
            TestImagesSetup.CreateDirectoryStructure();
            string testImagesRoot = PathHelper.GetTestImagesPath();
            Console.WriteLine($"Test images directory: {testImagesRoot}");
            Console.WriteLine();

            // Setup mount manager with unified persistence
            _vfs = new RetroVFS(enablePersistence: true);

            // Setup factory and register providers
            DriverFactory factory = new DriverFactory();
            factory.Register(new CommodoreDiskDriverProvider());  // Handles D64/D71/D81
            factory.Register(new T64DriverProvider());
            factory.Register(new PcFloppyDriverProvider());       // Handles PC floppies (360K-2.88M, FAT12)
            factory.Register(new DragonVdkDriverProvider());      // Handles Dragon/CoCo VDK (DragonDOS)
            factory.Register(new DragonDskDriverProvider());      // Handles Dragon/CoCo DSK/JVC (DragonDOS)
            factory.Register(new DragonDmkDriverProvider());      // Handles Dragon/CoCo DMK (DragonDOS, DECB, OS-9)
            factory.Register(new NdfsDriverProvider());           // Handles NDFS (Nord Disk File System)
            factory.Register(new V7DriverProvider());             // Handles UNIX V7 filesystem (1979)
            factory.Register(new FfsDriverProvider());            // Handles Berkeley Fast File System (FFS/UFS)

            _vfs.RegisterProvider(new DiskImageVolumeProvider(factory));
            _vfs.RegisterProvider(new NativeVolumeProvider());
            _vfs.RegisterProvider(new TnfsVolumeProvider());
            _vfs.RegisterProvider(new RetroIO.Storage.Azure.AzureBlobVolumeProvider());
            _vfs.RegisterProvider(new FtpVolumeProvider());
            _vfs.RegisterProvider(new SftpVolumeProvider());

            // Load all persisted mounts
            _vfs.LoadPersistedMounts();

            // Main menu loop
            bool running = true;
            while (running)
            {
                ShowMainMenu();
                string? choice = Console.ReadLine();

                switch (choice)
                {
                    case "1":
                        MountLocalImage();
                        break;
                    case "2":
                        MountTnfsServer();
                        break;
                    case "3":
                        MountLocalDirectory();
                        break;
                    case "4":
                        MountAzureBlobStorage();
                        break;
                    case "5":
                        MountFtpServer();
                        break;
                    case "6":
                        MountSftpServer();
                        break;
                    case "7":
                        ListAllMounts();
                        break;
                    case "8":
                        BrowseMount();
                        break;
                    case "9":
                        UnmountVolume();
                        break;
                    case "10":
                        CompareFormats();
                        break;
                    case "11":
                        ReadFileDemo();
                        break;
                    case "12":
                        ReadBlockDemo();
                        break;
                    case "13":
                        ShowStatistics();
                        break;
                    case "0":
                        running = false;
                        break;
                    default:
                        Console.WriteLine("Invalid choice. Please try again.");
                        PressAnyKey();
                        break;
                }
            }

            // Cleanup
            Cleanup();
            Console.WriteLine();
            Console.WriteLine("Thank you for using RetroFS!");
        }

        static void PrintBanner()
        {
            Console.Clear();
            Console.WriteLine("╔═══════════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║                        RETROFS DEMO v1.0                              ║");
            Console.WriteLine("║              Accessing yesterday's data with today's standards       ║");
            Console.WriteLine("╠═══════════════════════════════════════════════════════════════════════╣");
            Console.WriteLine("║  Multi-Protocol Retro Filesystem Access                              ║");
            Console.WriteLine("║  • Commodore: D64, D71, D81 (CBM DOS), T64 (Tape Archive)            ║");
            Console.WriteLine("║  • PC Floppy: 360K-2.88M (FAT12/FAT16)                               ║");
            Console.WriteLine("║  • Dragon/CoCo: VDK, DSK/JVC (DragonDOS)                             ║");
            Console.WriteLine("║  • Nord Data: NDFS (Nord Disk File System) with user management      ║");
            Console.WriteLine("║  • Amiga: ADF 880KB/1.76MB (OFS/FFS) [planned]                       ║");
            Console.WriteLine("║  • Network: TNFS, FTP/FTPS, SFTP/SSH                                 ║");
            Console.WriteLine("║  • Archives: ZIP, TAR (plugin-based)                                 ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════════════════╝");
            Console.WriteLine();
        }

        static void ShowMainMenu()
        {
            Console.Clear();
            PrintBanner();

            Console.WriteLine("╔════════════════════════ MAIN MENU ═══════════════════════════════════╗");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("║  MOUNTING                                                             ║");
            Console.WriteLine("║    1. Mount local disk image (D64/D71/D81/T64/IMG/VDK)               ║");
            Console.WriteLine("║    2. Mount TNFS server (network)                                    ║");
            Console.WriteLine("║    3. Mount local directory (Windows/Linux/UNC)                      ║");
            Console.WriteLine("║    4. Mount Azure Blob Storage (cloud - Azurite)                    ║");
            Console.WriteLine("║    5. Mount FTP/FTPS server (network)                                ║");
            Console.WriteLine("║    6. Mount SFTP/SSH server (network)                                ║");
            Console.WriteLine("║    7. List all mounted volumes                                       ║");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("║  OPERATIONS                                                           ║");
            Console.WriteLine("║    8. Browse mounted volume                                          ║");
            Console.WriteLine("║    9. Unmount volume                                                 ║");
            Console.WriteLine("║   10. Compare filesystem formats                                     ║");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("║  ADVANCED                                                             ║");
            Console.WriteLine("║   11. Read file (hex dump)                                           ║");
            Console.WriteLine("║   12. Read block/sector (low-level)                                  ║");
            Console.WriteLine("║   13. Show statistics                                                ║");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("║    0. Exit                                                           ║");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════════════════╝");
            Console.WriteLine();
            Console.Write("Enter choice: ");
        }

        static void MountLocalImage()
        {
            Console.Clear();
            Console.WriteLine("╔══════════════════════ MOUNT LOCAL IMAGE ═════════════════════════════╗");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("║  Commodore:  D64 (170KB), D71 (340KB), D81 (800KB), T64 (tape)       ║");
            Console.WriteLine("║  PC Floppy:  IMG/IMA/VFD/FLP (360KB, 720KB, 1.2MB, 1.44MB, 2.88MB)   ║");
            Console.WriteLine("║  Dragon:     VDK/DSK/JVC (160KB-720KB, DragonDOS)                    ║");
            Console.WriteLine("║  Nord Data:  ND/NDFS/NORD (Nord Disk File System, multi-user)        ║");
            Console.WriteLine("║  Amiga:      ADF (880KB, 1.76MB) [planned]                           ║");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            Console.Write("Enter path or filename: ");
            string? input = Console.ReadLine();

            if (string.IsNullOrEmpty(input))
            {
                Console.WriteLine("Cancelled.");
                PressAnyKey();
                return;
            }

            // Try to resolve path
            string imagePath = ResolvePath(input);
            if (!File.Exists(imagePath))
            {
                Console.WriteLine($"Error: File not found: {imagePath}");
                PressAnyKey();
                return;
            }

            Console.Write("Volume ID (unique identifier): ");
            string? volumeId = Console.ReadLine();
            if (string.IsNullOrEmpty(volumeId))
            {
                volumeId = $"local-{_vfs!.GetAllMounts().Length + 1}";
                Console.WriteLine($"Using default: {volumeId}");
            }

            Console.Write("Read-only? (Y/n): ");
            bool readOnly = (Console.ReadLine()?.ToLower() != "n");

            try
            {
                Console.WriteLine();
                Console.WriteLine("Mounting...");

                MountInfo mount = _vfs!.Mount("diskimage", imagePath, volumeId, readOnly);

                Console.WriteLine();
                Console.WriteLine("✓ Mount successful!");
                Console.WriteLine($"  Volume ID:     {mount.VolumeId}");
                Console.WriteLine($"  Filesystem:    {mount.FileSystemType}");
                Console.WriteLine($"  Label:         {mount.VolumeLabel}");
                Console.WriteLine($"  Volume Type:   {mount.Type}");
                Console.WriteLine($"  Read-Only:     {mount.IsReadOnly}");
                Console.WriteLine($"  Block Access:  {mount.HasBlockAccess}");
                if (mount.TotalSize.HasValue)
                {
                    Console.WriteLine($"  Total Size:    {FormatBytes(mount.TotalSize.Value)}");
                }
                if (mount.FreeSpace.HasValue)
                {
                    Console.WriteLine($"  Free Space:    {FormatBytes(mount.FreeSpace.Value)}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"✗ Mount failed: {ex.Message}");

                if (ex.InnerException != null)
                {
                    Console.WriteLine($"  Inner error: {ex.InnerException.Message}");
                }

                Console.WriteLine($"  Error type: {ex.GetType().Name}");
                Console.WriteLine($"  Stack trace:");
                Console.WriteLine($"  {ex.StackTrace?.Replace(Environment.NewLine, Environment.NewLine + "  ")}");
            }

            PressAnyKey();
        }

        static void MountTnfsServer()
        {
            Console.Clear();
            Console.WriteLine("╔════════════════════ MOUNT TNFS SERVER ═══════════════════════════════╗");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("║  TNFS (Trivial Network File System) - Network protocol for retro    ║");
            Console.WriteLine("║  computers. Public servers host thousands of disk images.            ║");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("║  Popular servers:                                                     ║");
            Console.WriteLine("║    • fujinet.online (FujiNet project - Atari, Commodore, Apple)     ║");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            Console.Write("Enter server address (or press Enter for fujinet.online): ");
            string? server = Console.ReadLine();
            if (string.IsNullOrEmpty(server))
            {
                server = "fujinet.online";
                Console.WriteLine($"Using default: {server}");
            }

            Console.Write("Volume ID (unique identifier): ");
            string? volumeId = Console.ReadLine();
            if (string.IsNullOrEmpty(volumeId))
            {
                volumeId = $"tnfs-{_vfs!.GetAllMounts().Length + 1}";
                Console.WriteLine($"Using default: {volumeId}");
            }

            try
            {
                Console.WriteLine();
                Console.WriteLine($"Connecting to {server}...");
                Console.WriteLine("(This may take a few seconds)");

                MountInfo mount = _vfs!.Mount("tnfs", server, volumeId, readOnly: true);

                Console.WriteLine();
                Console.WriteLine("✓ Connected successfully!");
                Console.WriteLine($"  Volume ID:     {mount.VolumeId}");
                Console.WriteLine($"  Server:        {mount.VolumeLabel}");
                Console.WriteLine($"  Volume Type:   {mount.Type}");
                Console.WriteLine($"  Source URI:    {mount.SourceUri}");
                Console.WriteLine($"  Read-Only:     {mount.IsReadOnly}");
                Console.WriteLine();
                Console.WriteLine("Note: Most public TNFS servers are read-only.");
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"✗ Connection failed: {ex.Message}");
                Console.WriteLine();
                Console.WriteLine("Troubleshooting:");
                Console.WriteLine("  • Check internet connection");
                Console.WriteLine("  • Verify server address");
                Console.WriteLine("  • Some servers may be offline");
            }

            PressAnyKey();
        }

        static void MountLocalDirectory()
        {
            Console.Clear();
            Console.WriteLine("╔═════════════════ MOUNT LOCAL DIRECTORY ══════════════════════════════╗");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("║  Mount a local Windows/Linux directory or UNC network share          ║");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("║  Supported paths:                                                     ║");
            Console.WriteLine("║    • Windows drive:     C:\\data, D:\\files                            ║");
            Console.WriteLine("║    • Linux path:        /mnt/data, /home/user/files                  ║");
            Console.WriteLine("║    • UNC share:         \\\\server\\share\\folder                         ║");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("║  NOTE: All file operations run under application identity            ║");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            Console.Write("Enter directory path: ");
            string? path = Console.ReadLine();

            if (string.IsNullOrEmpty(path))
            {
                Console.WriteLine("Cancelled.");
                PressAnyKey();
                return;
            }

            if (!Directory.Exists(path))
            {
                Console.WriteLine($"Error: Directory not found: {path}");
                PressAnyKey();
                return;
            }

            Console.Write("Volume ID (unique identifier): ");
            string? volumeId = Console.ReadLine();
            if (string.IsNullOrEmpty(volumeId))
            {
                volumeId = $"localfs-{_vfs!.GetAllMounts().Length + 1}";
                Console.WriteLine($"Using default: {volumeId}");
            }

            try
            {
                Console.WriteLine();
                Console.WriteLine("Mounting...");

                // Mount using RetroVFS
                MountInfo mount = _vfs!.Mount("native", path, volumeId, readOnly: false);

                Console.WriteLine();
                Console.WriteLine("✓ Mount successful!");
                Console.WriteLine($"  Volume ID:     {mount.VolumeId}");
                Console.WriteLine($"  Filesystem:    {mount.FileSystemType}");
                Console.WriteLine($"  Root Path:     {mount.VolumeLabel}");
                Console.WriteLine($"  Volume Type:   {mount.Type}");
                Console.WriteLine();
                Console.WriteLine("You can now browse this directory using option 6.");
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"✗ Mount failed: {ex.Message}");

                if (ex.InnerException != null)
                {
                    Console.WriteLine($"  Inner error: {ex.InnerException.Message}");
                }

                Console.WriteLine($"  Error type: {ex.GetType().Name}");
                Console.WriteLine($"  Stack trace:");
                Console.WriteLine($"  {ex.StackTrace?.Replace(Environment.NewLine, Environment.NewLine + "  ")}");
            }

            PressAnyKey();
        }

        static void MountAzureBlobStorage()
        {
            Console.Clear();
            Console.WriteLine("╔══════════════ MOUNT AZURE BLOB STORAGE ══════════════════════════════╗");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("║  Azure Blob Storage provides cloud-based file storage                ║");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("║  Local Development (Azurite):                                        ║");
            Console.WriteLine("║    1. Install: npm install -g azurite                                ║");
            Console.WriteLine("║    2. Run: azurite --silent                                          ║");
            Console.WriteLine("║    3. Default connection string configured automatically             ║");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("║  Production Azure:                                                    ║");
            Console.WriteLine("║    • Enter your Azure Storage connection string                      ║");
            Console.WriteLine("║    • Format: DefaultEndpointsProtocol=https;AccountName=...         ║");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            Console.WriteLine("Connection options:");
            Console.WriteLine("  1. Azurite (local emulator) - Default");
            Console.WriteLine("  2. Azure Storage (production)");
            Console.WriteLine();
            Console.Write("Choose (1/2) or press Enter for Azurite: ");
            string? option = Console.ReadLine();

            string connectionString;
            string containerName = "retrofs";

            if (option == "2")
            {
                Console.Write("Enter Azure Storage connection string: ");
                connectionString = Console.ReadLine() ?? "";
                if (string.IsNullOrEmpty(connectionString))
                {
                    Console.WriteLine("Cancelled.");
                    PressAnyKey();
                    return;
                }

                Console.Write("Container name (default: retrofs): ");
                string? container = Console.ReadLine();
                if (!string.IsNullOrEmpty(container))
                {
                    containerName = container;
                }
            }
            else
            {
                // Azurite default
                connectionString = "UseDevelopmentStorage=true";
                Console.WriteLine();
                Console.WriteLine("Using Azurite (local emulator)");
                Console.WriteLine($"Connection: {connectionString}");
                Console.WriteLine($"Container: {containerName}");
            }

            // Build URI for Azure mount (format: connection_string|container_name)
            string uri = $"{connectionString}|{containerName}";

            Console.Write("Volume ID (unique identifier): ");
            string? volumeId = Console.ReadLine();
            if (string.IsNullOrEmpty(volumeId))
            {
                volumeId = $"azure-{_vfs!.GetAllMounts().Length + 1}";
                Console.WriteLine($"Using default: {volumeId}");
            }

            try
            {
                Console.WriteLine();
                Console.WriteLine("Mounting Azure Blob Storage...");

                if (connectionString == "UseDevelopmentStorage=true")
                {
                    Console.WriteLine();
                    Console.WriteLine("NOTE: Using Azurite (local emulator)");
                    Console.WriteLine("Make sure Azurite is running:");
                    Console.WriteLine("  azurite --silent");
                    Console.WriteLine();
                    Console.Write("Press Enter when Azurite is ready...");
                    Console.ReadLine();
                }

                MountInfo mount = _vfs!.Mount("azure", uri, volumeId, readOnly: false);

                Console.WriteLine();
                Console.WriteLine("✓ Mount successful!");
                Console.WriteLine($"  Volume ID:     {mount.VolumeId}");
                Console.WriteLine($"  Filesystem:    {mount.FileSystemType}");
                Console.WriteLine($"  Container:     {mount.VolumeLabel}");
                Console.WriteLine($"  Volume Type:   {mount.Type}");
                Console.WriteLine($"  Source URI:    {mount.SourceUri}");
                Console.WriteLine();
                Console.WriteLine("You can now browse this Azure container using option 6.");
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"✗ Mount failed: {ex.Message}");

                if (ex.InnerException != null)
                {
                    Console.WriteLine($"  Inner error: {ex.InnerException.Message}");
                }

                if (connectionString == "UseDevelopmentStorage=true")
                {
                    Console.WriteLine();
                    Console.WriteLine("Troubleshooting:");
                    Console.WriteLine("  • Make sure Azurite is running: azurite --silent");
                    Console.WriteLine("  • Check if port 10000 is available");
                    Console.WriteLine("  • Try: azurite --location c:\\azurite --silent");
                }
            }

            PressAnyKey();
        }

        static void MountFtpServer()
        {
            Console.Clear();
            Console.WriteLine("╔════════════════ MOUNT FTP/FTPS SERVER ═══════════════════════════════╗");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("║  FTP (File Transfer Protocol) provides network file access           ║");
            Console.WriteLine("║  FTPS adds TLS/SSL encryption for secure transfers                   ║");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("║  URN Format: ftp://user:pass@host:port/path                          ║");
            Console.WriteLine("║              ftps://user:pass@host:port/path                         ║");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            Console.Write("Enter FTP server (e.g., ftp://user:pass@ftp.example.com/path): ");
            string? server = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(server))
            {
                Console.WriteLine("Cancelled.");
                PressAnyKey();
                return;
            }

            Console.Write("Enter Volume ID (e.g., 'my-ftp-server'): ");
            string? volumeId = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(volumeId))
            {
                Console.WriteLine("Cancelled.");
                PressAnyKey();
                return;
            }

            try
            {
                Console.WriteLine();
                Console.WriteLine("Mounting...");

                MountInfo mount = _vfs!.Mount("ftp", server, volumeId, readOnly: false);

                Console.WriteLine();
                Console.WriteLine("✓ Mount successful!");
                Console.WriteLine($"  Volume ID:     {mount.VolumeId}");
                Console.WriteLine($"  Filesystem:    {mount.FileSystemType}");
                Console.WriteLine($"  Server:        {mount.VolumeLabel}");
                Console.WriteLine($"  Volume Type:   {mount.Type}");
                Console.WriteLine();
                Console.WriteLine("You can now browse this FTP server using option 8.");
                Console.WriteLine();
                Console.WriteLine("Tip: Use 'ftps://' for secure FTPS connections.");
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"✗ Mount failed: {ex.Message}");

                if (ex.InnerException != null)
                {
                    Console.WriteLine($"  Inner error: {ex.InnerException.Message}");
                }

                Console.WriteLine();
                Console.WriteLine("Troubleshooting:");
                Console.WriteLine("  • Check server address and port");
                Console.WriteLine("  • Verify username and password");
                Console.WriteLine("  • Ensure firewall allows FTP (port 21)");
                Console.WriteLine("  • Try passive mode if behind NAT/firewall");
            }

            PressAnyKey();
        }

        static void MountSftpServer()
        {
            Console.Clear();
            Console.WriteLine("╔═══════════════ MOUNT SFTP/SSH SERVER ════════════════════════════════╗");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("║  SFTP (SSH File Transfer Protocol) provides secure file access       ║");
            Console.WriteLine("║  Uses SSH encryption for all transfers                               ║");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("║  URN Format: sftp://user:pass@host:port/path                         ║");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("║  Note: SSH key authentication not yet implemented in demo            ║");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            Console.Write("Enter SFTP server (e.g., sftp://user:pass@ssh.example.com:22/path): ");
            string? server = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(server))
            {
                Console.WriteLine("Cancelled.");
                PressAnyKey();
                return;
            }

            Console.Write("Enter Volume ID (e.g., 'my-sftp-server'): ");
            string? volumeId = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(volumeId))
            {
                Console.WriteLine("Cancelled.");
                PressAnyKey();
                return;
            }

            try
            {
                Console.WriteLine();
                Console.WriteLine("Mounting...");

                MountInfo mount = _vfs!.Mount("sftp", server, volumeId, readOnly: false);

                Console.WriteLine();
                Console.WriteLine("✓ Mount successful!");
                Console.WriteLine($"  Volume ID:     {mount.VolumeId}");
                Console.WriteLine($"  Filesystem:    {mount.FileSystemType}");
                Console.WriteLine($"  Server:        {mount.VolumeLabel}");
                Console.WriteLine($"  Volume Type:   {mount.Type}");
                Console.WriteLine();
                Console.WriteLine("You can now browse this SFTP server using option 8.");
                Console.WriteLine();
                Console.WriteLine("Tip: SSH uses port 22 by default.");
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"✗ Mount failed: {ex.Message}");

                if (ex.InnerException != null)
                {
                    Console.WriteLine($"  Inner error: {ex.InnerException.Message}");
                }

                Console.WriteLine();
                Console.WriteLine("Troubleshooting:");
                Console.WriteLine("  • Check server address and port (default: 22)");
                Console.WriteLine("  • Verify username and password");
                Console.WriteLine("  • Ensure SSH server is running");
                Console.WriteLine("  • Check firewall allows SSH (port 22)");
                Console.WriteLine("  • Password authentication must be enabled on server");
            }

            PressAnyKey();
        }

        static void ListAllMounts()
        {
            Console.Clear();
            Console.WriteLine("╔════════════════════ MOUNTED VOLUMES ═════════════════════════════════╗");
            Console.WriteLine();

            MountInfo[] mounts = _vfs!.GetAllMounts();

            if (mounts.Length == 0)
            {
                Console.WriteLine("  No volumes mounted.");
                Console.WriteLine();
                Console.WriteLine("  Use menu options 1 or 2 to mount volumes.");
            }
            else
            {
                for (int i = 0; i < mounts.Length; i++)
                {
                    MountInfo mount = mounts[i];
                    Console.WriteLine($"  [{i + 1}] {mount.VolumeId}");
                    Console.WriteLine($"      Filesystem:   {mount.FileSystemType}");
                    Console.WriteLine($"      Label:        {mount.VolumeLabel}");
                    Console.WriteLine($"      Volume Type:  {mount.Type}");
                    Console.WriteLine($"      Source:       {mount.SourceUri ?? "(local)"}");
                    Console.WriteLine($"      Access:       {(mount.IsReadOnly ? "Read-Only" : "Read-Write")}");
                    Console.WriteLine();
                }
            }

            Console.WriteLine("╚═══════════════════════════════════════════════════════════════════════╝");
            PressAnyKey();
        }

        static void BrowseMount()
        {
            MountInfo? mount = SelectMount("Browse");
            if (mount == null)
            {
                return;
            }

            try
            {
                UrnContext context = new UrnContext { VolumeId = mount.VolumeId };
                IVolumeProvider? provider = _vfs!.GetProvider(mount.VolumeId);
                if (provider == null)
                {
                    Console.WriteLine("Error: Provider not found.");
                    PressAnyKey();
                    return;
                }

                string currentPath = "/";
                bool browsing = true;

                while (browsing)
                {
                    Console.Clear();
                    string header = $"BROWSE: {mount.VolumeId}";
                    int padding = 71 - header.Length;
                    int leftPad = padding / 2;
                    int rightPad = padding - leftPad;
                    Console.WriteLine($"╔═{new string('═', leftPad)}{header}{new string('═', rightPad)}╗");
                    Console.WriteLine($"  Path: {currentPath}");
                    Console.WriteLine("╚═══════════════════════════════════════════════════════════════════════╝");
                    Console.WriteLine();

                    DirectoryEntry[] entries = provider.ListDirectory(context, currentPath);

                    if (entries.Length == 0)
                    {
                        Console.WriteLine("  (empty directory)");
                    }
                    else
                    {
                        Console.WriteLine("  #   Type  Name                            Size         ");
                        Console.WriteLine("  ─────────────────────────────────────────────────────────");

                        for (int i = 0; i < entries.Length; i++)
                        {
                            DirectoryEntry entry = entries[i];
                            string type = entry.Metadata.IsDirectory ? "[DIR]" : entry.Metadata.FileType ?? "[???]";
                            string name = entry.Name.PadRight(30);
                            if (name.Length > 30) name = name.Substring(0, 27) + "...";
                            string size = entry.Metadata.IsDirectory ? "" : FormatBytes(entry.Metadata.Size);

                            Console.WriteLine($"  {(i + 1),2}  {type,-5} {name}  {size,10}");
                        }
                    }

                    Console.WriteLine();
                    if (mount.IsReadOnly)
                    {
                        Console.WriteLine("Commands: [#] Read, [d#] Enter dir, [..] Parent, [q] Quit");
                    }
                    else
                    {
                        Console.WriteLine("Commands: [#] Read, [d#] Enter dir, [..] Parent");
                        Console.WriteLine("          [mkdir] Create dir, [mkfile] Create file, [upload] Upload file, [q] Quit");
                    }
                    Console.Write("> ");

                    string? cmd = Console.ReadLine();
                    if (string.IsNullOrEmpty(cmd))
                    {
                        continue;
                    }

                    cmd = cmd.ToLower().Trim();  // Make case-insensitive and remove whitespace

                    if (cmd == "q" || cmd == "quit")
                    {
                        browsing = false;
                    }
                    else if (cmd == "..")
                    {
                        // Go to parent directory
                        if (currentPath != "/")
                        {
                            int lastSlash = currentPath.LastIndexOf('/');
                            currentPath = lastSlash > 0 ? currentPath.Substring(0, lastSlash) : "/";
                        }
                        else
                        {
                            Console.WriteLine("Already at root directory.");
                            PressAnyKey();
                        }
                    }
                    else if (cmd.StartsWith("d") && int.TryParse(cmd.Substring(1), out int dirIndex))
                    {
                        // Enter directory
                        if (dirIndex > 0 && dirIndex <= entries.Length)
                        {
                            DirectoryEntry entry = entries[dirIndex - 1];
                            if (entry.Metadata.IsDirectory)
                            {
                                currentPath = currentPath == "/"
                                    ? "/" + entry.Name
                                    : currentPath + "/" + entry.Name;
                            }
                            else
                            {
                                Console.WriteLine("Not a directory.");
                                PressAnyKey();
                            }
                        }
                    }
                    else if (cmd == "mkdir" || cmd.StartsWith("mkdir "))
                    {
                        if (mount.IsReadOnly)
                        {
                            Console.WriteLine("Error: Volume is read-only.");
                            PressAnyKey();
                        }
                        else
                        {
                            string? dirName = null;
                            if (cmd.Length > 6)
                            {
                                dirName = cmd.Substring(6).Trim();
                            }
                            CreateDirectory(provider, context, currentPath, dirName);
                        }
                    }
                    else if (cmd == "mkfile" || cmd.StartsWith("mkfile "))
                    {
                        if (mount.IsReadOnly)
                        {
                            Console.WriteLine("Error: Volume is read-only.");
                            PressAnyKey();
                        }
                        else
                        {
                            string? fileName = null;
                            int? fileSize = null;

                            if (cmd.Length > 7)
                            {
                                string[] parts = cmd.Substring(7).Trim().Split(' ', 2);
                                fileName = parts[0];
                                if (parts.Length > 1 && int.TryParse(parts[1], out int size))
                                {
                                    fileSize = size;
                                }
                            }
                            CreateFile(provider, context, currentPath, fileName, fileSize);
                        }
                    }
                    else if (cmd == "upload" || cmd.StartsWith("upload "))
                    {
                        if (mount.IsReadOnly)
                        {
                            Console.WriteLine("Error: Volume is read-only.");
                            PressAnyKey();
                        }
                        else
                        {
                            string? localPath = null;
                            if (cmd.Length > 7)
                            {
                                localPath = cmd.Substring(7).Trim();
                            }
                            UploadFile(provider, context, currentPath, localPath);
                        }
                    }
                    else if (int.TryParse(cmd, out int fileIndex))
                    {
                        // Read file
                        if (fileIndex > 0 && fileIndex <= entries.Length)
                        {
                            DirectoryEntry entry = entries[fileIndex - 1];
                            if (!entry.Metadata.IsDirectory)
                            {
                                ShowFileContents(provider, context, currentPath, entry);
                            }
                        }
                    }
                    else
                    {
                        // Unknown command
                        Console.WriteLine();
                        Console.WriteLine($"Unknown command: '{cmd}'");
                        Console.WriteLine();
                        Console.WriteLine("Valid commands:");
                        Console.WriteLine("  q, quit            - Exit browse mode");
                        Console.WriteLine("  ..                 - Go to parent directory");
                        Console.WriteLine("  d#                 - Enter directory (e.g., d1)");
                        Console.WriteLine("  #                  - Read file (e.g., 3)");
                        if (!mount.IsReadOnly)
                        {
                            Console.WriteLine("  mkdir [name]       - Create directory");
                            Console.WriteLine("  mkfile [name] [size] - Create file with random data");
                            Console.WriteLine("  upload [path]      - Upload local file");
                        }
                        PressAnyKey();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"Error: {ex.Message}");
                PressAnyKey();
            }
        }

        static void ShowFileContents(IVolumeProvider provider, UrnContext context, string path, DirectoryEntry entry)
        {
            try
            {
                string filePath = path == "/" ? "/" + entry.Name : path + "/" + entry.Name;

                Console.WriteLine();
                Console.WriteLine($"Reading: {filePath}");

                byte[] data = provider.ReadFile(context, filePath);

                Console.WriteLine($"Size: {data.Length} bytes");
                Console.WriteLine();
                Console.WriteLine("Hex dump (first 512 bytes):");
                Console.WriteLine("Offset   00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F  ASCII");
                Console.WriteLine("───────────────────────────────────────────────────────────────────");

                int displayBytes = Math.Min(data.Length, 512);
                for (int i = 0; i < displayBytes; i += 16)
                {
                    Console.Write($"{i:X6}   ");

                    // Hex values
                    for (int j = 0; j < 16; j++)
                    {
                        if (i + j < displayBytes)
                        {
                            Console.Write($"{data[i + j]:X2} ");
                        }
                        else
                        {
                            Console.Write("   ");
                        }
                    }

                    Console.Write(" ");

                    // ASCII representation
                    for (int j = 0; j < 16 && i + j < displayBytes; j++)
                    {
                        byte b = data[i + j];
                        char c = (b >= 32 && b <= 126) ? (char)b : '.';
                        Console.Write(c);
                    }

                    Console.WriteLine();
                }

                Console.WriteLine();
                Console.Write("Save to file? (y/N): ");
                if (Console.ReadLine()?.ToLower() == "y")
                {
                    Console.Write("Output filename: ");
                    string? outFile = Console.ReadLine();
                    if (!string.IsNullOrEmpty(outFile))
                    {
                        File.WriteAllBytes(outFile, data);
                        Console.WriteLine($"✓ Saved to {outFile}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"Error reading file: {ex.Message}");
            }

            PressAnyKey();
        }

        static void CreateDirectory(IVolumeProvider provider, UrnContext context, string currentPath, string? dirName = null)
        {
            if (string.IsNullOrEmpty(dirName))
            {
                Console.WriteLine();
                Console.Write("Directory name: ");
                dirName = Console.ReadLine();

                if (string.IsNullOrEmpty(dirName))
                {
                    Console.WriteLine("Cancelled.");
                    PressAnyKey();
                    return;
                }
            }

            try
            {
                string dirPath = currentPath == "/" ? "/" + dirName : currentPath + "/" + dirName;
                string placeholderPath = dirPath + "/.placeholder";
                byte[] emptyData = new byte[0];

                provider.WriteFile(context, placeholderPath, emptyData);

                Console.WriteLine();
                Console.WriteLine($"✓ Created directory: {dirPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"✗ Error creating directory: {ex.Message}");
            }

            PressAnyKey();
        }

        static void CreateFile(IVolumeProvider provider, UrnContext context, string currentPath, string? fileName = null, int? fileSize = null)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                Console.WriteLine();
                Console.Write("File name: ");
                fileName = Console.ReadLine();

                if (string.IsNullOrEmpty(fileName))
                {
                    Console.WriteLine("Cancelled.");
                    PressAnyKey();
                    return;
                }
            }

            int size;
            if (fileSize.HasValue)
            {
                size = fileSize.Value;
            }
            else
            {
                Console.Write("File size (bytes): ");
                string? sizeStr = Console.ReadLine();

                if (!int.TryParse(sizeStr, out size) || size < 0)
                {
                    Console.WriteLine("Invalid size.");
                    PressAnyKey();
                    return;
                }
            }

            if (size > 10 * 1024 * 1024)
            {
                Console.WriteLine("Size too large (max 10 MB for demo).");
                PressAnyKey();
                return;
            }

            try
            {
                Console.WriteLine();
                Console.WriteLine($"Creating {size} bytes of random data...");

                byte[] data = new byte[size];
                Random random = new Random();
                random.NextBytes(data);

                string filePath = currentPath == "/" ? "/" + fileName : currentPath + "/" + fileName;
                provider.WriteFile(context, filePath, data);

                Console.WriteLine();
                Console.WriteLine($"✓ Created file: {filePath}");
                Console.WriteLine($"  Size: {FormatBytes(size)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"✗ Error creating file: {ex.Message}");
            }

            PressAnyKey();
        }

        static void UploadFile(IVolumeProvider provider, UrnContext context, string currentPath, string? localPath = null)
        {
            if (string.IsNullOrEmpty(localPath))
            {
                Console.WriteLine();
                Console.Write("Local file path: ");
                localPath = Console.ReadLine();

                if (string.IsNullOrEmpty(localPath))
                {
                    Console.WriteLine("Cancelled.");
                    PressAnyKey();
                    return;
                }
            }

            if (!File.Exists(localPath))
            {
                Console.WriteLine($"Error: File not found: {localPath}");
                PressAnyKey();
                return;
            }

            try
            {
                FileInfo fileInfo = new FileInfo(localPath);
                string fileName = fileInfo.Name;

                Console.WriteLine();
                Console.WriteLine($"File: {fileName}");
                Console.WriteLine($"Size: {FormatBytes(fileInfo.Length)}");
                Console.Write("Upload? (Y/n): ");

                string? confirm = Console.ReadLine();
                if (confirm?.ToLower() == "n")
                {
                    Console.WriteLine("Cancelled.");
                    PressAnyKey();
                    return;
                }

                Console.WriteLine();
                Console.WriteLine("Uploading...");

                byte[] data = File.ReadAllBytes(localPath);
                string remotePath = currentPath == "/" ? "/" + fileName : currentPath + "/" + fileName;
                provider.WriteFile(context, remotePath, data);

                Console.WriteLine();
                Console.WriteLine($"✓ Uploaded to: {remotePath}");
                Console.WriteLine($"  Size: {FormatBytes(data.Length)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"✗ Error uploading file: {ex.Message}");
            }

            PressAnyKey();
        }

        static void UnmountVolume()
        {
            MountInfo? mount = SelectMount("Unmount");
            if (mount == null)
            {
                return;
            }

            try
            {
                _vfs!.Unmount(mount.VolumeId);

                Console.WriteLine();
                Console.WriteLine($"✓ Unmounted: {mount.VolumeId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"Error: {ex.Message}");
            }

            PressAnyKey();
        }

        static void CompareFormats()
        {
            Console.Clear();
            Console.WriteLine("╔══════════════════ FILESYSTEM COMPARISON ═════════════════════════════╗");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("║  COMMODORE FORMATS                                                    ║");
            Console.WriteLine("║  ─────────────────────────────────────────────────────────────────── ║");
            Console.WriteLine("║  D64     35 trk   683 sec    170 KB   CBM DOS  Block: T/S (ZBR)      ║");
            Console.WriteLine("║  D71     70 trk  1366 sec    340 KB   CBM DOS  Block: T/S (ZBR)      ║");
            Console.WriteLine("║  D81     80 trk  3200 sec    800 KB   CBM DOS  Block: T/S (Flat)     ║");
            Console.WriteLine("║  T64     N/A     N/A         Variable  Archive  No block access      ║");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("║  PC FLOPPY FORMATS                                                    ║");
            Console.WriteLine("║  ─────────────────────────────────────────────────────────────────── ║");
            Console.WriteLine("║  360K    40 cyl   720 blk    360 KB   FAT12    Block: CHS (9 SPT)    ║");
            Console.WriteLine("║  720K    80 cyl  1440 blk    720 KB   FAT12    Block: CHS (9 SPT)    ║");
            Console.WriteLine("║  1.2M    80 cyl  2400 blk    1.2 MB   FAT12    Block: CHS (15 SPT)   ║");
            Console.WriteLine("║  1.44M   80 cyl  2880 blk    1.44 MB  FAT12    Block: CHS (18 SPT)   ║");
            Console.WriteLine("║  2.88M   80 cyl  5760 blk    2.88 MB  FAT12    Block: CHS (36 SPT)   ║");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("║  DRAGON/COCO FORMATS                                                  ║");
            Console.WriteLine("║  ─────────────────────────────────────────────────────────────────── ║");
            Console.WriteLine("║  VDK SS40  40 trk   720 blk    180 KB   DragonOS Block: CHS (18 SPT)║");
            Console.WriteLine("║  VDK DS40  40 trk  1440 blk    360 KB   DragonOS Block: CHS (36 SPT)║");
            Console.WriteLine("║  VDK SS80  80 trk  1440 blk    360 KB   DragonOS Block: CHS (18 SPT)║");
            Console.WriteLine("║  VDK DS80  80 trk  2880 blk    720 KB   DragonOS Block: CHS (36 SPT)║");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("║  NORD DATA FORMATS                                                    ║");
            Console.WriteLine("║  ─────────────────────────────────────────────────────────────────── ║");
            Console.WriteLine("║  NDFS    N/A      Variable  Variable  NDFS      Block: LBA (2KB)     ║");
            Console.WriteLine("║          Multi-user support, quotas, bitmap allocation               ║");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("║  OTHER FORMATS (planned)                                              ║");
            Console.WriteLine("║  ─────────────────────────────────────────────────────────────────── ║");
            Console.WriteLine("║  ADF     80 cyl  1760 blk    880 KB   OFS/FFS  Block: CHS (11 SPT)   ║");
            Console.WriteLine("║  TNFS    N/A     N/A         Variable  Network  No block access      ║");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("╠═══════════════════════════════════════════════════════════════════════╣");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("║  ADDRESSING MODES:                                                    ║");
            Console.WriteLine("║    T/S (Track/Sector)  - Commodore formats with variable geometry    ║");
            Console.WriteLine("║    CHS (Cyl/Head/Sect) - PC, Amiga, Dragon with uniform geometry     ║");
            Console.WriteLine("║    LBA (Linear Block)  - Modern sequential addressing                ║");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("║  GEOMETRY TYPES:                                                      ║");
            Console.WriteLine("║    ZBR (Zone Bit Recording) - Variable sectors/track (D64: 21/19/18) ║");
            Console.WriteLine("║    Flat Geometry            - Fixed sectors/track (D81: 40, PC: 9-36)║");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("║  ARCHITECTURE:                                                        ║");
            Console.WriteLine("║    • Generic Block Device with configuration descriptors             ║");
            Console.WriteLine("║    • Filesystem layer separated from block device layer              ║");
            Console.WriteLine("║    • Single GenericBlockDevice handles all geometries                ║");
            Console.WriteLine("║    • Adding new format = adding descriptor data, not code            ║");
            Console.WriteLine("║                                                                       ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════════════════╝");
            PressAnyKey();
        }

        static void ReadFileDemo()
        {
            MountInfo? mount = SelectMount("Read file from");
            if (mount == null)
            {
                return;
            }

            Console.Write("Enter file path: ");
            string? path = Console.ReadLine();
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                UrnContext context = new UrnContext { VolumeId = mount.VolumeId };
                IVolumeProvider? provider = _vfs!.GetProvider(mount.VolumeId);
                if (provider == null)
                {
                    Console.WriteLine("Error: Provider not found.");
                    PressAnyKey();
                    return;
                }

                byte[] data = provider.ReadFile(context, path);

                Console.WriteLine();
                Console.WriteLine($"File: {path}");
                Console.WriteLine($"Size: {data.Length} bytes");
                Console.WriteLine();

                // Show hex dump (first 256 bytes)
                int displayBytes = Math.Min(data.Length, 256);
                Console.WriteLine("Hex dump (first 256 bytes):");
                Console.WriteLine("Offset   00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F  ASCII");
                Console.WriteLine("───────────────────────────────────────────────────────────────────");

                for (int i = 0; i < displayBytes; i += 16)
                {
                    Console.Write($"{i:X6}   ");

                    // Hex values
                    for (int j = 0; j < 16; j++)
                    {
                        if (i + j < displayBytes)
                        {
                            Console.Write($"{data[i + j]:X2} ");
                        }
                        else
                        {
                            Console.Write("   ");
                        }
                    }

                    Console.Write(" ");

                    // ASCII representation
                    for (int j = 0; j < 16 && i + j < displayBytes; j++)
                    {
                        byte b = data[i + j];
                        char c = (b >= 32 && b <= 126) ? (char)b : '.';
                        Console.Write(c);
                    }

                    Console.WriteLine();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"Error: {ex.Message}");
            }

            PressAnyKey();
        }

        static void ReadBlockDemo()
        {
            MountInfo? mount = SelectMount("Read block from");
            if (mount == null)
            {
                return;
            }

            if (!mount.HasBlockAccess)
            {
                Console.WriteLine();
                Console.WriteLine($"Error: {mount.FileSystemType} does not support block-level access.");
                Console.WriteLine("Block access is available for D64, D71, D81, PC floppy, and Dragon VDK/DSK formats.");
                PressAnyKey();
                return;
            }

            Console.Write("Enter track number: ");
            if (!byte.TryParse(Console.ReadLine(), out byte track))
            {
                return;
            }

            Console.Write("Enter sector number: ");
            if (!byte.TryParse(Console.ReadLine(), out byte sector))
            {
                return;
            }

            try
            {
                UrnContext context = new UrnContext { VolumeId = mount.VolumeId };
                IVolumeProvider? provider = _vfs!.GetProvider(mount.VolumeId);
                if (provider == null)
                {
                    Console.WriteLine("Error: Provider not found.");
                    PressAnyKey();
                    return;
                }

                BlockAddress address = BlockAddress.FromTrackSector(track, sector);
                byte[] buffer = new byte[256];
                int bytesRead = provider.ReadBlock(context, address, buffer);

                Console.WriteLine();
                Console.WriteLine($"Block T:{track} S:{sector}");
                Console.WriteLine($"Bytes read: {bytesRead}");
                Console.WriteLine();

                // Show hex dump
                Console.WriteLine("Hex dump:");
                Console.WriteLine("Offset   00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F  ASCII");
                Console.WriteLine("───────────────────────────────────────────────────────────────────");

                for (int i = 0; i < bytesRead; i += 16)
                {
                    Console.Write($"{i:X6}   ");

                    // Hex values
                    for (int j = 0; j < 16; j++)
                    {
                        if (i + j < bytesRead)
                        {
                            Console.Write($"{buffer[i + j]:X2} ");
                        }
                        else
                        {
                            Console.Write("   ");
                        }
                    }

                    Console.Write(" ");

                    // ASCII representation
                    for (int j = 0; j < 16 && i + j < bytesRead; j++)
                    {
                        byte b = buffer[i + j];
                        char c = (b >= 32 && b <= 126) ? (char)b : '.';
                        Console.Write(c);
                    }

                    Console.WriteLine();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"Error: {ex.Message}");
            }

            PressAnyKey();
        }

        static void ShowStatistics()
        {
            Console.Clear();
            Console.WriteLine("╔════════════════════════ STATISTICS ══════════════════════════════════╗");
            Console.WriteLine();

            MountInfo[] mounts = _vfs!.GetAllMounts();
            Console.WriteLine($"  Total mounts:       {mounts.Length}");

            int localCount = 0;
            int tnfsCount = 0;
            int readOnlyCount = 0;
            long totalSize = 0;

            for (int i = 0; i < mounts.Length; i++)
            {
                MountInfo mount = mounts[i];
                if (mount.Type == "diskimage") localCount++;
                if (mount.Type == "tnfs") tnfsCount++;
                if (mount.IsReadOnly) readOnlyCount++;
                if (mount.TotalSize.HasValue) totalSize += mount.TotalSize.Value;
            }

            Console.WriteLine($"    Local:            {localCount}");
            Console.WriteLine($"    TNFS:             {tnfsCount}");
            Console.WriteLine($"  Read-only:          {readOnlyCount}");
            Console.WriteLine($"  Total capacity:     {FormatBytes(totalSize)}");
            Console.WriteLine();

            Console.WriteLine("  Supported formats:");
            Console.WriteLine($"    Commodore:");
            Console.WriteLine($"      D64:            1541 disk (170 KB)");
            Console.WriteLine($"      D71:            1571 disk (340 KB)");
            Console.WriteLine($"      D81:            1581 disk (800 KB)");
            Console.WriteLine($"      T64:            Tape archive (variable)");
            Console.WriteLine($"    PC Floppy:");
            Console.WriteLine($"      360K-2.88M:     FAT12/FAT16 (5.25\"/3.5\")");
            Console.WriteLine($"    Dragon/CoCo:");
            Console.WriteLine($"      VDK:            DragonDOS (180KB-720KB)");
            Console.WriteLine($"    Network:");
            Console.WriteLine($"      TNFS:           Network filesystem");
            Console.WriteLine();

            Console.WriteLine("  Features:");
            Console.WriteLine($"    Automatic detection:     ✓");
            Console.WriteLine($"    Forced driver override:  ✓");
            Console.WriteLine($"    Block-level access:      ✓ (D64/D71/D81/PC/Dragon VDK/DSK)");
            Console.WriteLine($"    Network access:          ✓ (TNFS)");
            Console.WriteLine($"    URN-based addressing:    ✓");
            Console.WriteLine();
            Console.WriteLine("  Architecture:");
            Console.WriteLine($"    Generic block device with configuration descriptors");
            Console.WriteLine($"    Filesystem layer separated from block device layer");
            Console.WriteLine($"    Configuration-driven: add formats via data, not code");
            Console.WriteLine();
            Console.WriteLine("╚═══════════════════════════════════════════════════════════════════════╝");
            PressAnyKey();
        }

        // Helper methods

        static MountInfo? SelectMount(string action)
        {
            MountInfo[] mounts = _vfs!.GetAllMounts();

            if (mounts.Length == 0)
            {
                Console.WriteLine();
                Console.WriteLine("No volumes mounted. Please mount a volume first.");
                PressAnyKey();
                return null;
            }

            Console.WriteLine();
            Console.WriteLine($"═══ {action} ═══");
            Console.WriteLine();

            for (int i = 0; i < mounts.Length; i++)
            {
                MountInfo mount = mounts[i];
                string displayName = mount.VolumeId;

                if (!string.IsNullOrEmpty(mount.VolumeLabel))
                {
                    displayName = $"{mount.VolumeId} - {mount.VolumeLabel}";
                }

                Console.WriteLine($"  [{i + 1}] {displayName} ({mount.FileSystemType})");
            }

            Console.WriteLine();
            Console.Write($"Select volume (1-{mounts.Length}): ");

            if (int.TryParse(Console.ReadLine(), out int choice) && choice >= 1 && choice <= mounts.Length)
            {
                return mounts[choice - 1];
            }

            return null;
        }


        static string ResolvePath(string input)
        {
            if (Path.IsPathRooted(input) && File.Exists(input))
            {
                return input;
            }

            string testImagesRoot = PathHelper.GetTestImagesPath();

            // Try combining with test images root
            string testPath = Path.Combine(testImagesRoot, input);
            if (File.Exists(testPath))
            {
                return testPath;
            }

            // Try d64 subdirectory
            string d64Path = Path.Combine(testImagesRoot, "d64", input);
            if (File.Exists(d64Path))
            {
                return d64Path;
            }

            // Return original if nothing found
            return input;
        }

        static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
            {
                return $"{bytes} B";
            }
            else if (bytes < 1024 * 1024)
            {
                return $"{bytes / 1024.0:F1} KB";
            }
            else if (bytes < 1024 * 1024 * 1024)
            {
                return $"{bytes / (1024.0 * 1024.0):F1} MB";
            }
            else
            {
                return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
            }
        }

        static void PressAnyKey()
        {
            Console.WriteLine();
            Console.Write("Press any key to continue...");
            Console.ReadKey(true);
            Console.WriteLine();
        }

        static void Cleanup()
        {
            // Dispose RetroVFS - it will unmount all volumes cleanly
            // Persistence is preserved for next startup
            _vfs?.Dispose();
        }
    }
}
