//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Management.Contracts;

public class ServerStatusResponse
{
    public bool IsRunning { get; set; }
    public string Uptime { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public DateTime? StartedAt { get; set; }
}

public class ServerActionResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}
