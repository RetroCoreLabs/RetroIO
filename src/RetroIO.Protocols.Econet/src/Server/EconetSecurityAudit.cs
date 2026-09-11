//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Security.Implementation.Auditing;

namespace RetroIO.Protocols.Econet.Server;

internal static class EconetSecurityAudit
{
    public static async ValueTask EmitAuthAsync(
        SecurityEventEmitter? emitter,
        Guid userId,
        string username,
        bool success,
        string clientAddress)
    {
        if (emitter == null)
            return;

        await emitter.EmitAuthEventAsync(userId, username, success, "Econet *I AM", clientAddress);
    }

    public static async ValueTask EmitFileAccessAsync(
        SecurityEventEmitter? emitter,
        FileServerSession? session,
        SecurityEventType eventType,
        string path,
        string clientAddress,
        bool granted,
        string? denialReason = null)
    {
        if (emitter == null)
            return;

        SecurityAuditEntry entry = new SecurityAuditEntry
        {
            EventType = eventType,
            Severity = granted ? AuditSeverity.Info : AuditSeverity.Warning,
            PrincipalId = session?.RetroUserId ?? Guid.Empty,
            PrincipalName = session?.Username ?? "UNKNOWN",
            IpAddress = clientAddress,
            Path = path,
            Operation = eventType.ToString(),
            AccessGranted = granted,
            DenialReason = denialReason,
            Message = granted
                ? $"{eventType} {path}"
                : $"{eventType} denied for {path}: {denialReason}"
        };

        await emitter.EmitAsync(entry);
    }
}
