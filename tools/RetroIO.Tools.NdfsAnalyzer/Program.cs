//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;
using RetroIO.Storage.Ndfs.BlockDevices;
using RetroIO.Storage.Ndfs.Diagnostics;
using RetroIO.Storage.Ndfs.FileSystems;

namespace RetroIO.Tools.NdfsAnalyzer
{
    class Program
    {
        static int Main(string[] args)
        {
            Console.WriteLine("================================================================================");
            Console.WriteLine("NDFS Filesystem Analyzer & Repair Tool");
            Console.WriteLine("================================================================================");
            Console.WriteLine();

            if (args.Length == 0)
            {
                ShowUsage();
                return 1;
            }

            string command = args[0].ToLower();

            switch (command)
            {
                case "analyze":
                case "analyse":
                    if (args.Length < 2)
                    {
                        Console.WriteLine("Error: Missing image path");
                        ShowUsage();
                        return 1;
                    }
                    return AnalyzeFilesystem(args[1]);

                case "repair":
                    if (args.Length < 2)
                    {
                        Console.WriteLine("Error: Missing image path");
                        ShowUsage();
                        return 1;
                    }
                    bool autoApprove = args.Length > 2 && args[2] == "--auto";
                    return RepairFilesystem(args[1], autoApprove);

                case "list-initial-commands":
                case "show-initial-commands":
                    if (args.Length < 2)
                    {
                        Console.WriteLine("Error: Missing image path");
                        ShowUsage();
                        return 1;
                    }
                    return ShowInitialCommands(args[1]);

                case "repair-initial-commands":
                    if (args.Length < 2)
                    {
                        Console.WriteLine("Error: Missing image path");
                        ShowUsage();
                        return 1;
                    }
                    var repairCommands = new System.Collections.Generic.List<string>();
                    for (int i = 2; i < args.Length; i++)
                        repairCommands.Add(args[i]);
                    return RepairInitialCommands(args[1], repairCommands);

                case "list-files":
                    if (args.Length < 2) { Console.WriteLine("Error: Missing image path"); return 1; }
                    return ListFiles(args[1]);

                case "read-file":
                    if (args.Length < 3) { Console.WriteLine("Error: Usage: read-file <image> <USER/NAME:TYPE>"); return 1; }
                    return ReadFileText(args[1], args[2]);

                case "diagnose-initial-commands":
                    if (args.Length < 2)
                    {
                        Console.WriteLine("Error: Missing image path");
                        ShowUsage();
                        return 1;
                    }
                    return DiagnoseInitialCommands(args[1]);

                case "set-initial-commands":
                    if (args.Length < 2)
                    {
                        Console.WriteLine("Error: Missing image path");
                        ShowUsage();
                        return 1;
                    }
                    // Remaining args (args[2..]) are the new commands, in order. Zero commands clears the buffer.
                    var newCommands = new System.Collections.Generic.List<string>();
                    for (int i = 2; i < args.Length; i++)
                        newCommands.Add(args[i]);
                    return SetInitialCommands(args[1], newCommands);

                case "help":
                case "--help":
                case "-h":
                    ShowUsage();
                    return 0;

                default:
                    Console.WriteLine($"Error: Unknown command '{command}'");
                    ShowUsage();
                    return 1;
            }
        }

        static void ShowUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  NdfsAnalyzerTool analyze <image-path>                        Analyze NDFS filesystem");
            Console.WriteLine("  NdfsAnalyzerTool repair <image-path> [--auto]                Repair NDFS filesystem");
            Console.WriteLine("  NdfsAnalyzerTool list-initial-commands <image-path>          Show SINTRAN initial commands");
            Console.WriteLine("  NdfsAnalyzerTool set-initial-commands <image-path> \"CMD\" ...  Replace SINTRAN initial commands");
            Console.WriteLine("  NdfsAnalyzerTool repair-initial-commands <image> \"CMD\" ...    Restore a header-corrupted buffer");
            Console.WriteLine("  NdfsAnalyzerTool diagnose-initial-commands <image-path>      Dump every INIBU candidate (debug)");
            Console.WriteLine("  NdfsAnalyzerTool list-files <image-path>                     List object files");
            Console.WriteLine("  NdfsAnalyzerTool read-file <image-path> <USER/NAME:TYPE>     Print a file as 7-bit text");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --auto    Auto-approve all repairs (use with caution!)");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  NdfsAnalyzerTool analyze test.ndfs");
            Console.WriteLine("  NdfsAnalyzerTool repair test.ndfs --auto");
            Console.WriteLine("  NdfsAnalyzerTool list-initial-commands SMD0-L.IMG");
            Console.WriteLine("  NdfsAnalyzerTool set-initial-commands SMD0-L.IMG \"ENTER-DIR,,DI-75-1,0\" \"SET-AVAIL\" \"CC READY\"");
        }

        static int ShowInitialCommands(string imagePath)
        {
            if (!File.Exists(imagePath))
            {
                Console.WriteLine($"Error: File not found: {imagePath}");
                return 1;
            }

            try
            {
                using var stream = File.Open(imagePath, FileMode.Open, FileAccess.Read);
                var descriptor = NdfsBlockDeviceDescriptor.Create((int)stream.Length);
                using var device = new GenericBlockDevice(descriptor);
                device.Mount(stream, readOnly: true);

                using var fs = new NdfsFileSystem();
                fs.Initialize(device);

                var buffer = fs.ReadSintranInitialCommands();
                if (buffer == null)
                {
                    Console.WriteLine("No SINTRAN initial-command buffer found.");
                    Console.WriteLine("(Not a SINTRAN system pack, or an unrecognised SINTRAN version.)");
                    return 2;
                }

                Console.WriteLine($"SINTRAN {buffer.VersionLetter} initial commands from {buffer.SegfilPath}");
                Console.WriteLine($"(INIBU in segment {Convert.ToString(buffer.SegmentNumber, 8)}₈, SEGFIL0 byte 0x{buffer.ByteOffset:X}, {buffer.TextLength} text bytes)");
                Console.WriteLine("These run first at every restart, as '@LIST-INITIAL-COMMANDS' prints them:");
                Console.WriteLine();
                for (int i = 0; i < buffer.Commands.Count; i++)
                    Console.WriteLine($"  {i + 1,2}  {buffer.Commands[i]}");

                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        static int RepairInitialCommands(string imagePath, System.Collections.Generic.List<string> commands)
        {
            if (!File.Exists(imagePath))
            {
                Console.WriteLine($"Error: File not found: {imagePath}");
                return 1;
            }
            if (commands.Count == 0)
            {
                Console.WriteLine("Error: Provide the commands to restore, e.g.:");
                Console.WriteLine("  repair-initial-commands disk.img \"ENTER-DIRECTORY,,DISC-SCSI-1,0\" \"SET-AVAIL\"");
                return 1;
            }

            try
            {
                // Always back up before touching a bootable system pack.
                string backupPath = imagePath + ".backup";
                Console.WriteLine($"Creating backup: {backupPath}");
                File.Copy(imagePath, backupPath, true);

                long written;
                int score;
                using (var stream = File.Open(imagePath, FileMode.Open, FileAccess.ReadWrite))
                {
                    var descriptor = NdfsBlockDeviceDescriptor.Create((int)stream.Length);
                    using var device = new GenericBlockDevice(descriptor);
                    device.Mount(stream, readOnly: false);

                    using var fs = new NdfsFileSystem();
                    fs.Initialize(device);

                    Console.WriteLine($"Restoring {commands.Count} initial command(s):");
                    for (int i = 0; i < commands.Count; i++)
                        Console.WriteLine($"  {i + 1,2}  {commands[i]}");
                    Console.WriteLine();

                    bool ok = fs.RepairSintranInitialCommands(commands, out written, out score);
                    if (!ok)
                    {
                        Console.WriteLine("Could not find an unambiguous corrupted buffer to repair.");
                        Console.WriteLine("Nothing was written. Run 'diagnose-initial-commands' to inspect the candidates.");
                        Console.WriteLine($"Backup remains at: {backupPath}");
                        return 2;
                    }
                    Console.WriteLine($"✓ Wrote buffer at SEGFIL0 byte 0x{written:X} (match confidence score {score}).");
                }

                // Re-open from scratch and verify the repaired buffer reads back cleanly.
                using (var stream = File.Open(imagePath, FileMode.Open, FileAccess.Read))
                {
                    var descriptor = NdfsBlockDeviceDescriptor.Create((int)stream.Length);
                    using var device = new GenericBlockDevice(descriptor);
                    device.Mount(stream, readOnly: true);
                    using var fs = new NdfsFileSystem();
                    fs.Initialize(device);

                    var verify = fs.ReadSintranInitialCommands();
                    if (verify == null)
                    {
                        Console.WriteLine("⚠ Verify FAILED: buffer still not locatable after repair. Restore the backup!");
                        Console.WriteLine($"Backup at: {backupPath}");
                        return 1;
                    }

                    Console.WriteLine($"✓ Verified at SEGFIL0 byte 0x{verify.ByteOffset:X}, SINTRAN {verify.VersionLetter}. Buffer now reads:");
                    for (int i = 0; i < verify.Commands.Count; i++)
                        Console.WriteLine($"  {i + 1,2}  {verify.Commands[i]}");
                    Console.WriteLine($"Backup saved at: {backupPath}");
                    return 0;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        static int ListFiles(string imagePath)
        {
            if (!File.Exists(imagePath)) { Console.WriteLine($"Error: File not found: {imagePath}"); return 1; }
            try
            {
                using var stream = File.Open(imagePath, FileMode.Open, FileAccess.Read);
                var descriptor = NdfsBlockDeviceDescriptor.Create((int)stream.Length);
                using var device = new GenericBlockDevice(descriptor);
                device.Mount(stream, readOnly: true);
                using var fs = new NdfsFileSystem();
                fs.Initialize(device);

                var objs = fs.GetObjectEntries();
                Console.WriteLine($"{objs.Count} object file(s):");
                for (int i = 0; i < objs.Count; i++)
                {
                    var o = objs[i];
                    string type = (o.Type ?? "").TrimEnd('\x27', ' ');
                    string name = string.IsNullOrEmpty(type) ? o.ObjectName : $"{o.ObjectName}:{type}";
                    Console.WriteLine($"  ({o.UserName}){name}  [{o.BytesInFile} bytes]");
                }
                return 0;
            }
            catch (Exception ex) { Console.WriteLine($"Error: {ex.Message}"); return 1; }
        }

        static int ReadFileText(string imagePath, string path)
        {
            if (!File.Exists(imagePath)) { Console.WriteLine($"Error: File not found: {imagePath}"); return 1; }
            try
            {
                using var stream = File.Open(imagePath, FileMode.Open, FileAccess.Read);
                var descriptor = NdfsBlockDeviceDescriptor.Create((int)stream.Length);
                using var device = new GenericBlockDevice(descriptor);
                device.Mount(stream, readOnly: true);
                using var fs = new NdfsFileSystem();
                fs.Initialize(device);

                byte[] data = fs.ReadFile(path);
                Console.WriteLine($"=== {path} ({data.Length} bytes) — parity-stripped 7-bit text ===");
                // SINTRAN text files set the high (parity) bit; strip it. Show CR/'/NUL structure readably.
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < data.Length; i++)
                {
                    int c = data[i] & 0x7F;
                    if (c == 0x0D) sb.Append('\n');          // CR -> newline for display
                    else if (c == 0x00) { /* skip NUL pad */ }
                    else if (c >= 0x20 && c < 0x7F) sb.Append((char)c);
                    else sb.Append('.');
                }
                Console.WriteLine(sb.ToString());
                return 0;
            }
            catch (Exception ex) { Console.WriteLine($"Error: {ex.Message}"); return 1; }
        }

        static int DiagnoseInitialCommands(string imagePath)
        {
            if (!File.Exists(imagePath))
            {
                Console.WriteLine($"Error: File not found: {imagePath}");
                return 1;
            }

            try
            {
                using var stream = File.Open(imagePath, FileMode.Open, FileAccess.Read);
                var descriptor = NdfsBlockDeviceDescriptor.Create((int)stream.Length);
                using var device = new GenericBlockDevice(descriptor);
                device.Mount(stream, readOnly: true);

                using var fs = new NdfsFileSystem();
                fs.Initialize(device);

                var candidates = fs.DiagnoseSintranInitialCommands();
                Console.WriteLine($"INIBU-containing segment candidates in SEGFIL0: {candidates.Count}");
                Console.WriteLine("(The reader picks the FIRST one that is 'parses=YES'.)");
                Console.WriteLine();

                bool pickedShown = false;
                for (int i = 0; i < candidates.Count; i++)
                {
                    var c = candidates[i];
                    // The reader now requires a self-consistent buffer (re-encode == stored length),
                    // so it picks the first CONSISTENT candidate, not merely the first that parses.
                    bool isPick = !pickedShown && c.Consistent;
                    if (isPick) pickedShown = true;

                    Console.WriteLine($"[{i}] {(isPick ? "<== READER PICKS THIS" : "")}");
                    Console.WriteLine($"    Version : SINTRAN {c.VersionLetter}");
                    Console.WriteLine($"    Segment : {Convert.ToString(c.SegmentNumber, 8)}₈ (MADR {c.Madr})");
                    Console.WriteLine($"    Offset  : SEGFIL0 byte 0x{c.ByteOffset:X}");
                    Console.WriteLine($"    LenCell : {c.LengthCell} (plausible={c.LengthPlausible})");
                    if (c.Commands != null && c.Commands.Count > 0)
                    {
                        Console.WriteLine($"    Parses  : YES, {c.Commands.Count} command(s)  (consistent={c.Consistent}{(c.Consistent ? "" : " -> coincidental/garbage")})");
                        for (int j = 0; j < c.Commands.Count; j++)
                            Console.WriteLine($"                {j + 1,2}  {c.Commands[j]}");
                    }
                    else
                    {
                        Console.WriteLine($"    Parses  : NO");
                    }
                    Console.WriteLine("    Bytes   :");
                    DumpHex(c.RegionHead, "        ");
                    Console.WriteLine();
                }

                if (candidates.Count == 0)
                    Console.WriteLine("No candidates — not a SINTRAN system pack, or no segment table in SEGFIL0.");

                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        // Simple hex + ASCII dump (16 bytes/row) for the diagnostic.
        static void DumpHex(byte[] data, string indent)
        {
            for (int row = 0; row < data.Length; row += 16)
            {
                var hex = new System.Text.StringBuilder();
                var asc = new System.Text.StringBuilder();
                for (int col = 0; col < 16; col++)
                {
                    if (row + col < data.Length)
                    {
                        byte b = data[row + col];
                        hex.Append(b.ToString("X2")).Append(' ');
                        // Strip parity bit so 7-bit SINTRAN text shows through.
                        byte t = (byte)(b & 0x7F);
                        asc.Append(t >= 0x20 && t < 0x7F ? (char)t : '.');
                    }
                    else
                    {
                        hex.Append("   ");
                    }
                }
                Console.WriteLine($"{indent}{row:X4}  {hex}  {asc}");
            }
        }

        static int SetInitialCommands(string imagePath, System.Collections.Generic.List<string> commands)
        {
            if (!File.Exists(imagePath))
            {
                Console.WriteLine($"Error: File not found: {imagePath}");
                return 1;
            }

            try
            {
                // Always back up before touching a bootable system pack.
                string backupPath = imagePath + ".backup";
                Console.WriteLine($"Creating backup: {backupPath}");
                File.Copy(imagePath, backupPath, true);

                using var stream = File.Open(imagePath, FileMode.Open, FileAccess.ReadWrite);
                var descriptor = NdfsBlockDeviceDescriptor.Create((int)stream.Length);
                using var device = new GenericBlockDevice(descriptor);
                device.Mount(stream, readOnly: false);

                using var fs = new NdfsFileSystem();
                fs.Initialize(device);

                Console.WriteLine($"Writing {commands.Count} initial command(s):");
                for (int i = 0; i < commands.Count; i++)
                    Console.WriteLine($"  {i + 1,2}  {commands[i]}");
                Console.WriteLine();

                bool ok = fs.WriteSintranInitialCommands(commands);
                if (!ok)
                {
                    Console.WriteLine("No SINTRAN initial-command buffer found - nothing written.");
                    Console.WriteLine($"Original backup at: {backupPath}");
                    return 2;
                }

                // Read back to verify the write round-trips.
                var verify = fs.ReadSintranInitialCommands();
                Console.WriteLine("✓ Written. Buffer now reads:");
                if (verify != null)
                {
                    for (int i = 0; i < verify.Commands.Count; i++)
                        Console.WriteLine($"  {i + 1,2}  {verify.Commands[i]}");
                }
                Console.WriteLine($"Backup saved at: {backupPath}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        static int AnalyzeFilesystem(string imagePath)
        {
            if (!File.Exists(imagePath))
            {
                Console.WriteLine($"Error: File not found: {imagePath}");
                return 1;
            }

            try
            {
                Console.WriteLine($"Analyzing: {imagePath}");
                Console.WriteLine();

                using var stream = File.Open(imagePath, FileMode.Open, FileAccess.Read);
                var descriptor = NdfsBlockDeviceDescriptor.Create((int)stream.Length);
                using var device = new GenericBlockDevice(descriptor);
                device.Mount(stream, readOnly: true);

                using var fs = new NdfsFileSystem();
                fs.Initialize(device);

                Console.WriteLine("Running deep analysis...");
                var analyzer = new RetroIO.Storage.Ndfs.Diagnostics.NdfsAnalyzer(fs, device);
                var report = analyzer.Analyze();

                Console.WriteLine();
                Console.WriteLine(report.GenerateTextReport());

                if (report.TotalIssues > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"⚠ Found {report.TotalIssues} issue(s). Run 'repair' command to fix.");
                    return 2; // Issues found
                }

                return 0; // Success, no issues
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        static int RepairFilesystem(string imagePath, bool autoApprove)
        {
            if (!File.Exists(imagePath))
            {
                Console.WriteLine($"Error: File not found: {imagePath}");
                return 1;
            }

            try
            {
                Console.WriteLine($"Repairing: {imagePath}");
                Console.WriteLine();

                // Create backup
                string backupPath = imagePath + ".backup";
                Console.WriteLine($"Creating backup: {backupPath}");
                File.Copy(imagePath, backupPath, true);

                using var stream = File.Open(imagePath, FileMode.Open, FileAccess.ReadWrite);
                var descriptor = NdfsBlockDeviceDescriptor.Create((int)stream.Length);
                using var device = new GenericBlockDevice(descriptor);
                device.Mount(stream, readOnly: false);

                using var fs = new NdfsFileSystem();
                fs.Initialize(device);

                // Step 1: Analyze
                Console.WriteLine("Step 1: Analyzing filesystem...");
                var analyzer = new RetroIO.Storage.Ndfs.Diagnostics.NdfsAnalyzer(fs, device);
                var report = analyzer.Analyze();

                if (report.TotalIssues == 0)
                {
                    Console.WriteLine("✓ No issues found. Filesystem is healthy!");
                    return 0;
                }

                Console.WriteLine($"Found {report.TotalIssues} issue(s).");
                Console.WriteLine();

                // Step 2: Generate repair plan
                Console.WriteLine("Step 2: Generating repair plan...");
                var repairTool = new NdfsRepairTool(fs, device, report);
                var plan = repairTool.GenerateRepairPlan();

                Console.WriteLine(plan.GenerateTextSummary());

                if (plan.TotalActions == 0)
                {
                    Console.WriteLine("No repair actions available.");
                    return 0;
                }

                // Step 3: Approve actions
                if (!autoApprove)
                {
                    Console.WriteLine();
                    Console.WriteLine("Step 3: Reviewing repair actions...");

                    for (int i = 0; i < plan.Actions.Count; i++)
                    {
                        var action = plan.Actions[i];

                        if (action.AutoApprove)
                        {
                            action.IsApproved = true;
                            Console.WriteLine($"  ✓ Auto-approved: {action.Description}");
                        }
                        else
                        {
                            Console.WriteLine();
                            Console.WriteLine($"  [{action.Risk}] {action.Description}");
                            if (!string.IsNullOrEmpty(action.WarningMessage))
                                Console.WriteLine($"  ⚠ WARNING: {action.WarningMessage}");

                            Console.Write("  Approve this action? (y/n): ");
                            string? response = Console.ReadLine();
                            action.IsApproved = response?.ToLower() == "y";

                            if (action.IsApproved)
                                Console.WriteLine("  ✓ Approved");
                            else
                                Console.WriteLine("  ✗ Skipped");
                        }
                    }
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine("Step 3: Auto-approving ALL actions...");
                }

                // Step 4: Execute repairs
                Console.WriteLine();
                Console.WriteLine("Step 4: Executing repairs...");
                var result = repairTool.ExecuteRepairs(plan, autoApprove);

                Console.WriteLine();
                Console.WriteLine(result.GenerateTextSummary());

                // Step 5: Re-analyze
                Console.WriteLine();
                Console.WriteLine("Step 5: Verifying repairs...");
                var analyzer2 = new RetroIO.Storage.Ndfs.Diagnostics.NdfsAnalyzer(fs, device);
                var report2 = analyzer2.Analyze();

                Console.WriteLine($"Remaining issues: {report2.TotalIssues}");

                if (report2.TotalIssues == 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("✓ SUCCESS! Filesystem repaired successfully.");
                    Console.WriteLine($"Backup saved at: {backupPath}");
                    return 0;
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine($"⚠ WARNING: {report2.TotalIssues} issue(s) remain.");
                    Console.WriteLine("Run 'analyze' command for details.");
                    Console.WriteLine($"Original backup at: {backupPath}");
                    return 2;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return 1;
            }
        }
    }
}
