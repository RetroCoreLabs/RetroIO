//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Management.Contracts;

public class TnfsSettingsDto
{
    public int UdpPort { get; set; }
    public int TcpPort { get; set; }
    public bool UdpEnabled { get; set; }
    public bool TcpEnabled { get; set; }
    public int MaxConnections { get; set; }
    public int SessionTimeoutSeconds { get; set; }
    public string RootPath { get; set; } = string.Empty;
}

public class UpdateTnfsSettingsRequest
{
    public int? UdpPort { get; set; }
    public int? TcpPort { get; set; }
    public bool? UdpEnabled { get; set; }
    public bool? TcpEnabled { get; set; }
    public int? MaxConnections { get; set; }
    public int? SessionTimeoutSeconds { get; set; }
    public string? RootPath { get; set; }
}

public class ServerSettingsDto
{
    public bool AutoStart { get; set; }
    public string LogLevel { get; set; } = "Information";
    public int MaxLogEntries { get; set; }
}

public class UpdateServerSettingsRequest
{
    public bool? AutoStart { get; set; }
    public string? LogLevel { get; set; }
    public int? MaxLogEntries { get; set; }
}
