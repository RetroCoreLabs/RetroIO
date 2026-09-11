//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Collections.Generic;
using System.Text;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Read-only view of the embedded RetroFS activity monitor (commands + file transfers).
/// Use <see cref="Mark"/> before a client action, then inspect <see cref="CommandsSinceMark"/> /
/// <see cref="TransfersSinceMark"/> or <see cref="FormatTraceSinceMark"/> on failure.
/// </summary>
internal sealed class EconetTestMonitor
{
    private readonly EconetActivityLog _log;
    private int _commandMark;
    private int _transferMark;

    public EconetTestMonitor(EconetActivityLog log)
    {
        _log = log;
    }

    public EconetActivityLog Log => _log;

    public IReadOnlyList<EconetCommandEntry> RecentCommands => _log.GetRecentCommands(10_000);

    public IReadOnlyList<EconetFileTransferEntry> RecentTransfers => _log.GetRecentTransfers(10_000);

    public int CommandCount => RecentCommands.Count;

    public int TransferCount => RecentTransfers.Count;

    public void Mark()
    {
        _commandMark = CommandCount;
        _transferMark = TransferCount;
    }

    public IReadOnlyList<EconetCommandEntry> CommandsSinceMark()
    {
        IReadOnlyList<EconetCommandEntry> all = RecentCommands;
        int added = all.Count - _commandMark;
        if (added <= 0)
            return [];

        EconetCommandEntry[] slice = new EconetCommandEntry[added];
        for (int i = 0; i < added; i++)
            slice[i] = all[i];

        return slice;
    }

    public IReadOnlyList<EconetFileTransferEntry> TransfersSinceMark()
    {
        IReadOnlyList<EconetFileTransferEntry> all = RecentTransfers;
        int added = all.Count - _transferMark;
        if (added <= 0)
            return [];

        EconetFileTransferEntry[] slice = new EconetFileTransferEntry[added];
        for (int i = 0; i < added; i++)
            slice[i] = all[i];

        return slice;
    }

    public string FormatTrace() => FormatEntries(RecentCommands, RecentTransfers);

    public string FormatTraceSinceMark() => FormatEntries(CommandsSinceMark(), TransfersSinceMark());

    private static string FormatEntries(
        IReadOnlyList<EconetCommandEntry> commands,
        IReadOnlyList<EconetFileTransferEntry> transfers)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("--- RetroFS activity monitor ---");

        sb.AppendLine(commands.Count == 0 ? "(no commands)" : "Commands (newest first):");
        for (int i = 0; i < commands.Count; i++)
        {
            EconetCommandEntry entry = commands[i];
            sb.Append("  [");
            sb.Append(entry.Success ? "OK" : "FAIL");
            sb.Append("] ");
            sb.Append(entry.Station);
            sb.Append(' ');
            sb.Append(entry.Username.Length > 0 ? entry.Username : "-");
            sb.Append(" | ");
            sb.Append(entry.Command);
            sb.Append(" → ");
            sb.Append(entry.Result);
            if (!string.IsNullOrEmpty(entry.Detail))
            {
                sb.Append(" | ");
                sb.Append(entry.Detail);
            }

            sb.AppendLine();
        }

        sb.AppendLine(transfers.Count == 0 ? "(no file transfers)" : "File transfers (newest first):");
        for (int i = 0; i < transfers.Count; i++)
        {
            EconetFileTransferEntry entry = transfers[i];
            sb.Append("  [");
            sb.Append(entry.Success ? "OK" : "FAIL");
            sb.Append("] ");
            sb.Append(entry.Station);
            sb.Append(' ');
            sb.Append(entry.Username.Length > 0 ? entry.Username : "-");
            sb.Append(" | ");
            sb.Append(entry.DirectionLabel);
            sb.Append(' ');
            sb.Append(entry.FilePath);
            sb.Append(" (");
            sb.Append(entry.Bytes);
            sb.Append(" bytes) → ");
            sb.Append(entry.Status);
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
