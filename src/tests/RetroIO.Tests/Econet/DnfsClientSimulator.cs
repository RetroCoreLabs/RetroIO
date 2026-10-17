//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text;
using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;
using RetroIO.Identity;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Minimal DNFS client: tracks wire handles from login and drives the catalogue opcode sequence.
/// </summary>
internal sealed class DnfsClientSimulator
{
    private readonly EconetFsTestHarness _harness;
    private byte _urd;
    private byte _cwd;
    private byte _lib;

    public DnfsClientSimulator(EconetFsTestHarness harness)
    {
        _harness = harness;
    }

    public bool IsLoggedIn { get; private set; }

    public byte WireUrd => _urd;
    public byte WireCwd => _cwd;
    public byte WireLib => _lib;

    public DnfsReply OsCli(string command)
    {
        EconetMessage response = _harness.HandleOsCli(command);
        DnfsReply reply = new DnfsReply(response.Payload);
        if (reply.IsLoginSuccess)
            ApplyLoginHandles(reply.Payload);
        else if (IsLogoutSuccess(reply.Payload))
            IsLoggedIn = false;

        return reply;
    }

    public DnfsReply Opcode(byte opcode, byte[] parameters)
    {
        EconetMessage response = _harness.HandleLoggedIn(opcode, parameters, _urd, _cwd, _lib);
        return new DnfsReply(response.Payload);
    }

    /// <summary>
    /// DNFS <c>*CAT</c> flow: OSCLI redirect → CatHeader → GetObjectInfo(6) → Examine → GetObjectInfo(1) per file.
    /// </summary>
    public DnfsCatalogueResult Cat(string path = ".")
    {
        string osCli = path == "." ? "CAT\r" : $"CAT {path}\r";
        DnfsReply redirect = OsCli(osCli);
        if (!redirect.IsExamineRedirect)
            return DnfsCatalogueResult.FailedAt("CAT redirect", redirect);

        string redirectPath = AcornPath.ReadCrTerminatedString(redirect.Payload.AsSpan(2));
        if (!string.Equals(redirectPath, path, StringComparison.Ordinal))
            return DnfsCatalogueResult.FailedAt("CAT redirect path", redirect);

        DnfsReply header = Opcode(FileServerOpcodes.CatHeader, Encoding.ASCII.GetBytes(path + "\r"));
        if (!header.IsProtocolSuccess || header.Payload.Length != 35)
            return DnfsCatalogueResult.FailedAt("CatHeader", header);

        DnfsReply directoryInfo = Opcode(
            FileServerOpcodes.GetObjectInfo,
            BuildGetObjectInfoRequest(6, path));
        if (!directoryInfo.IsProtocolSuccess || directoryInfo.Payload.Length != 17)
            return DnfsCatalogueResult.FailedAt("GetObjectInfo cmd 6", directoryInfo);

        byte[] examineParams = BuildExamineRequest(path);
        DnfsReply examine = Opcode(FileServerOpcodes.Examine, examineParams);
        if (!examine.IsProtocolSuccess)
            return DnfsCatalogueResult.FailedAt("Examine", examine);

        int entryCount = examine.Payload[2];
        List<string> fileNames = new List<string>(entryCount);
        for (int i = 0; i < entryCount; i++)
        {
            int offset = 4 + (i * 27);
            string name = Encoding.ASCII.GetString(examine.Payload, offset, 10).TrimEnd();
            if (examine.Payload[offset + 24] != 0x02)
                fileNames.Add(name);
        }

        List<DnfsReply> fileInfos = new List<DnfsReply>(fileNames.Count);
        for (int i = 0; i < fileNames.Count; i++)
        {
            DnfsReply info = Opcode(
                FileServerOpcodes.GetObjectInfo,
                BuildGetObjectInfoRequest(1, fileNames[i]));
            if (!info.IsProtocolSuccess || info.Payload.Length != 5)
                return DnfsCatalogueResult.FailedAt($"GetObjectInfo cmd 1 ({fileNames[i]})", info);

            fileInfos.Add(info);
        }

        return DnfsCatalogueResult.Success(redirect, header, directoryInfo, examine, fileNames, fileInfos);
    }

    public void SetSystPassword(string password)
    {
        RetroUser? syst = _harness.IdentityProvider.GetUserByUsernameAsync("SYST").AsTask().GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("SYST user missing from test harness.");
        _harness.IdentityProvider.SetPasswordAsync(syst.UserId, password).AsTask().GetAwaiter().GetResult();
    }

    private void ApplyLoginHandles(byte[] payload)
    {
        if (payload.Length < 5)
            throw new InvalidOperationException("Login reply too short for wire handles.");

        _urd = payload[2];
        _cwd = payload[3];
        _lib = payload[4];
        IsLoggedIn = true;
    }

    private static bool IsLogoutSuccess(byte[] payload) =>
        payload.Length >= 2 && payload[0] == 0 && payload[1] == 0;

    private static byte[] BuildGetObjectInfoRequest(byte command, string path)
    {
        byte[] pathBytes = Encoding.ASCII.GetBytes(path + "\r");
        byte[] request = new byte[1 + pathBytes.Length];
        request[0] = command;
        pathBytes.CopyTo(request, 1);
        return request;
    }

    private static byte[] BuildExamineRequest(string path)
    {
        byte[] pathBytes = Encoding.ASCII.GetBytes(path);
        byte[] request = new byte[3 + pathBytes.Length];
        request[2] = 0xFF;
        pathBytes.CopyTo(request, 3);
        return request;
    }
}

internal readonly struct DnfsReply
{
    public DnfsReply(byte[] payload)
    {
        Payload = payload;
    }

    public byte[] Payload { get; }

    public bool IsLoginSuccess =>
        Payload.Length >= 2 && Payload[0] == FileServerOpcodes.LoginSuccessReturnCode && Payload[1] == 0;

    public bool IsExamineRedirect =>
        Payload.Length >= 3 && Payload[0] == FileServerOpcodes.Examine && Payload[1] == 0;

    public bool IsProtocolSuccess =>
        Payload.Length >= 2 && Payload[1] == 0 &&
        !(Payload.Length == 3 && Payload[0] == 0 && Payload[1] == 0 && Payload[2] == 0);

    public byte ErrorCode => Payload.Length >= 2 ? Payload[1] : (byte)0xFF;
}

internal sealed class DnfsCatalogueResult
{
    private DnfsCatalogueResult(bool succeeded, string? failedStage, DnfsReply? failedReply)
    {
        Succeeded = succeeded;
        FailedStage = failedStage;
        FailedReply = failedReply;
    }

    public bool Succeeded { get; }
    public string? FailedStage { get; }
    public DnfsReply? FailedReply { get; }
    public DnfsReply Redirect { get; private init; }
    public DnfsReply Header { get; private init; }
    public DnfsReply DirectoryInfo { get; private init; }
    public DnfsReply Examine { get; private init; }
    public IReadOnlyList<string> FileNames { get; private init; } = Array.Empty<string>();
    public IReadOnlyList<DnfsReply> FileInfos { get; private init; } = Array.Empty<DnfsReply>();

    public static DnfsCatalogueResult Success(
        DnfsReply redirect,
        DnfsReply header,
        DnfsReply directoryInfo,
        DnfsReply examine,
        IReadOnlyList<string> fileNames,
        IReadOnlyList<DnfsReply> fileInfos) =>
        new DnfsCatalogueResult(true, null, null)
        {
            Redirect = redirect,
            Header = header,
            DirectoryInfo = directoryInfo,
            Examine = examine,
            FileNames = fileNames,
            FileInfos = fileInfos,
        };

    public static DnfsCatalogueResult FailedAt(string stage, DnfsReply reply) =>
        new DnfsCatalogueResult(false, stage, reply);
}
