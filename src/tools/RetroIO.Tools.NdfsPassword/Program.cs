//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Ndfs.BlockDevices;
using RetroIO.Storage.Ndfs.FileSystems;

namespace RetroIO.Tools.NdfsPassword
{
    class Program
    {
        static int Main(string[] args)
        {
            Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║      NDFS Password Management Tool v1.0                   ║");
            Console.WriteLine("║      RetroCore Labs - RetroFS Project                     ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            if (args.Length == 0)
            {
                ShowHelp();
                return 0;
            }

            string command = args[0].ToLower();

            switch (command)
            {
                case "list":
                    return ListUsers(args);
                case "clear":
                    return ClearPassword(args);
                case "instructions":
                case "manual":
                    return GetInstructions(args);
                case "hexedit":
                case "hex":
                    return GetHexEditInfo(args);
                case "help":
                case "--help":
                case "-h":
                case "?":
                    ShowHelp();
                    return 0;
                default:
                    Console.WriteLine($"Unknown command: {command}");
                    Console.WriteLine("Use 'help' to see available commands.");
                    return 1;
            }
        }

        static void ShowHelp()
        {
            Console.WriteLine("USAGE:");
            Console.WriteLine("  ndfs-password <command> <image-file> [options]");
            Console.WriteLine();
            Console.WriteLine("COMMANDS:");
            Console.WriteLine("  list <image>              List all users in NDFS image");
            Console.WriteLine("  clear <image> <user>      Clear user password (programmatic)");
            Console.WriteLine("  instructions <image> <user>  Get all clearing instructions");
            Console.WriteLine("  manual <image> <user>     Same as 'instructions'");
            Console.WriteLine("  hexedit <image> <user>    Show hex editor offset only");
            Console.WriteLine("  help                      Show this help");
            Console.WriteLine();
            Console.WriteLine("EXAMPLES:");
            Console.WriteLine("  ndfs-password list disk.img");
            Console.WriteLine("  ndfs-password clear disk.img SYSTEM");
            Console.WriteLine("  ndfs-password clear disk.img 0");
            Console.WriteLine("  ndfs-password instructions disk.img ADMIN");
            Console.WriteLine("  ndfs-password manual disk.img ADMIN > clear-admin.txt");
            Console.WriteLine("  ndfs-password hexedit disk.img SYSTEM");
            Console.WriteLine();
            Console.WriteLine("NOTES:");
            Console.WriteLine("  - User can be specified by name or index (0-255)");
            Console.WriteLine("  - 'clear' modifies the image file (requires write access)");
            Console.WriteLine("  - 'instructions' only reads the image (no modifications)");
            Console.WriteLine();
        }

        static int ListUsers(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("ERROR: Missing image file path");
                Console.WriteLine("USAGE: ndfs-password list <image-file>");
                return 1;
            }

            string imagePath = args[1];

            if (!File.Exists(imagePath))
            {
                Console.WriteLine($"ERROR: File not found: {imagePath}");
                return 1;
            }

            try
            {
                using (var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read))
                {
                    var descriptor = NdfsBlockDeviceDescriptor.Create(stream.Length);
                    var blockDevice = new GenericBlockDevice(descriptor);
                    blockDevice.Mount(stream, readOnly: true);

                    var ndfs = new NdfsFileSystem();
                    ndfs.Initialize(blockDevice);

                    var users = ndfs.GetUsers();

                    if (users.Count == 0)
                    {
                        Console.WriteLine("No users found in NDFS image.");
                        return 0;
                    }

                    Console.WriteLine($"Found {users.Count} user(s) in {Path.GetFileName(imagePath)}:");
                    Console.WriteLine();
                    Console.WriteLine("┌──────┬──────────────────┬──────────┬──────────┬────────────┐");
                    Console.WriteLine("│ Index│ Username         │ Reserved │ Used     │ Password   │");
                    Console.WriteLine("├──────┼──────────────────┼──────────┼──────────┼────────────┤");

                    for (int i = 0; i < users.Count; i++)
                    {
                        var user = users[i];
                        string passwordStatus = user.Password == 0 ? "CLEARED" : $"0x{user.Password:X4}";
                        Console.WriteLine($"│ {user.UserIndex,4} │ {user.UserName,-16} │ {user.PagesReserved,8} │ {user.PagesUsed,8} │ {passwordStatus,-10} │");
                    }

                    Console.WriteLine("└──────┴──────────────────┴──────────┴──────────┴────────────┘");
                    Console.WriteLine();
                    Console.WriteLine($"Total: {users.Count} user(s)");
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: {ex.Message}");
                return 1;
            }
        }

        static int ClearPassword(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("ERROR: Missing arguments");
                Console.WriteLine("USAGE: ndfs-password clear <image-file> <username-or-index>");
                return 1;
            }

            string imagePath = args[1];
            string userIdentifier = args[2];

            if (!File.Exists(imagePath))
            {
                Console.WriteLine($"ERROR: File not found: {imagePath}");
                return 1;
            }

            try
            {
                using (var stream = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite))
                {
                    var descriptor = NdfsBlockDeviceDescriptor.Create(stream.Length);
                    var blockDevice = new GenericBlockDevice(descriptor);
                    blockDevice.Mount(stream, readOnly: false);

                    var ndfs = new NdfsFileSystem();
                    ndfs.Initialize(blockDevice);

                    bool success;
                    string userName;
                    ushort oldPassword;

                    if (int.TryParse(userIdentifier, out int userIndex))
                    {
                        var user = ndfs.GetUser(userIndex);
                        if (user == null)
                        {
                            Console.WriteLine($"ERROR: User index {userIndex} not found");
                            return 1;
                        }
                        userName = user.UserName;
                        oldPassword = user.Password;
                        success = ndfs.ClearUserPassword(userIndex);
                    }
                    else
                    {
                        userName = userIdentifier;
                        var users = ndfs.GetUsers();
                        int foundIndex = -1;
                        for (int i = 0; i < users.Count; i++)
                        {
                            if (string.Equals(users[i].UserName, userIdentifier, StringComparison.OrdinalIgnoreCase))
                            {
                                foundIndex = users[i].UserIndex;
                                break;
                            }
                        }
                        var user = foundIndex >= 0 ? ndfs.GetUser(foundIndex) : null;
                        oldPassword = user?.Password ?? 0;
                        success = ndfs.ClearUserPassword(userIdentifier);
                    }

                    if (success)
                    {
                        Console.WriteLine($"✓ Password cleared for user '{userName}'");
                        Console.WriteLine($"  Old password: {oldPassword,5} (decimal) = {Convert.ToString(oldPassword, 8),6} (octal) = 0x{oldPassword:X4} (hex)");
                        Console.WriteLine($"  New password: 0 (cleared)");
                        Console.WriteLine($"✓ Changes saved to {Path.GetFileName(imagePath)}");
                        return 0;
                    }
                    else
                    {
                        Console.WriteLine($"ERROR: Failed to clear password for '{userIdentifier}'");
                        Console.WriteLine("  Possible reasons:");
                        Console.WriteLine("  - User not found");
                        Console.WriteLine("  - Disk is read-only");
                        return 1;
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine($"ERROR: Access denied to {imagePath}");
                Console.WriteLine("  Make sure the file is not read-only or in use by another process.");
                return 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: {ex.Message}");
                return 1;
            }
        }

        static int GetInstructions(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("ERROR: Missing arguments");
                Console.WriteLine("USAGE: ndfs-password instructions <image-file> <username-or-index>");
                return 1;
            }

            string imagePath = args[1];
            string userIdentifier = args[2];

            if (!File.Exists(imagePath))
            {
                Console.WriteLine($"ERROR: File not found: {imagePath}");
                return 1;
            }

            try
            {
                using (var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read))
                {
                    var descriptor = NdfsBlockDeviceDescriptor.Create(stream.Length);
                    var blockDevice = new GenericBlockDevice(descriptor);
                    blockDevice.Mount(stream, readOnly: true);

                    var ndfs = new NdfsFileSystem();
                    ndfs.Initialize(blockDevice);

                    string? instructions;

                    if (int.TryParse(userIdentifier, out int userIndex))
                    {
                        instructions = ndfs.GetPasswordClearInstructions(userIndex);
                    }
                    else
                    {
                        instructions = ndfs.GetPasswordClearInstructions(userIdentifier);
                    }

                    if (instructions != null)
                    {
                        Console.WriteLine(instructions);
                        return 0;
                    }
                    else
                    {
                        Console.WriteLine($"ERROR: User '{userIdentifier}' not found");
                        return 1;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: {ex.Message}");
                return 1;
            }
        }

        static int GetHexEditInfo(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("ERROR: Missing arguments");
                Console.WriteLine("USAGE: ndfs-password hexedit <image-file> <username-or-index>");
                return 1;
            }

            string imagePath = args[1];
            string userIdentifier = args[2];

            if (!File.Exists(imagePath))
            {
                Console.WriteLine($"ERROR: File not found: {imagePath}");
                return 1;
            }

            try
            {
                using (var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read))
                {
                    var descriptor = NdfsBlockDeviceDescriptor.Create(stream.Length);
                    var blockDevice = new GenericBlockDevice(descriptor);
                    blockDevice.Mount(stream, readOnly: true);

                    var ndfs = new NdfsFileSystem();
                    ndfs.Initialize(blockDevice);

                    // Get full instructions first
                    string? instructions;
                    if (int.TryParse(userIdentifier, out int userIndex))
                    {
                        instructions = ndfs.GetPasswordClearInstructions(userIndex);
                    }
                    else
                    {
                        instructions = ndfs.GetPasswordClearInstructions(userIdentifier);
                    }

                    if (instructions == null)
                    {
                        Console.WriteLine($"ERROR: User '{userIdentifier}' not found");
                        return 1;
                    }

                    // Extract just the hex editor section
                    int hexStart = instructions.IndexOf("=== HEX EDITOR INSTRUCTIONS");
                    if (hexStart >= 0)
                    {
                        string hexSection = instructions.Substring(hexStart);
                        Console.WriteLine(hexSection);
                    }
                    else
                    {
                        Console.WriteLine(instructions);
                    }

                    return 0;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: {ex.Message}");
                return 1;
            }
        }
    }
}
