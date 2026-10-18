//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;
using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Sessions;

namespace RetroIO.Protocols.Econet.Protocol.FileServer;

/// <summary>
/// Verbose request/reply traces for *CAT* debugging and BeebEm/DNFS interop analysis.
/// </summary>
public static class EconetProtocolDiagnostics
{
    private const int MaxHexBytes = 128;

    /// <summary>
    /// Formats a request as a trace line: a dump of the request with login passwords removed, the handles of the session when there is one, and a description of the parsed request when there is one.
    /// </summary>
    /// <param name="request">
    /// The request.
    /// </param>
    /// <param name="session">
    /// The session of the sending station, or null when it is not logged in.
    /// </param>
    /// <returns>
    /// The trace text.
    /// </returns>
    public static string FormatRequestTrace(FileServerRequest request, FileServerSession? session)
    {
        StringBuilder builder = new StringBuilder(512);

        // Redact any login password at the byte level BEFORE anything is rendered, so it can never
        // reach the hex dump, the ASCII preview, or the parse line. Non-login requests redact to
        // null and are dumped as-is.
        byte[]? redacted = EconetLogRedaction.RedactRawRequest(request.Opcode, request.Raw);
        if (redacted != null && FileServerRequest.TryParse(redacted, out FileServerRequest safeRequest))
            builder.Append(FileServerPacketFormatter.FormatRequestDump(safeRequest, MaxHexBytes));
        else
            builder.Append(FileServerPacketFormatter.FormatRequestDump(request, MaxHexBytes));

        if (session != null)
        {
            builder.Append(" | session CWD=");
            builder.Append(session.CurrentDirectoryUnix);
            builder.Append(" wireURD=&");
            builder.Append(request.Urd.ToString("X2"));
            builder.Append(" wireCWD=&");
            builder.Append(request.Cwd.ToString("X2"));
            builder.Append(" wireLIB=&");
            builder.Append(request.Lib.ToString("X2"));
            builder.Append(" intURD=");
            builder.Append(session.UrdHandle);
            builder.Append(" intCWD=");
            builder.Append(session.CwdHandle);
            builder.Append(" intLIB=");
            builder.Append(session.LibHandle);
        }

        string parsed = DescribeParsedRequest(request);
        if (!string.IsNullOrEmpty(parsed))
        {
            builder.Append(" | parse ");
            builder.Append(parsed);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Formats a reply as a trace line with the result, the length and the bytes in hex, and a decoded description for some replies.
    /// </summary>
    /// <param name="request">
    /// The request the reply answers.
    /// </param>
    /// <param name="payload">
    /// The reply bytes, or null when there is no reply.
    /// </param>
    /// <param name="success">
    /// True when the reply was judged to be a success.
    /// </param>
    /// <returns>
    /// The trace text. A login reply gives Login OK, and a missing or empty reply gives a short fixed text.
    /// </returns>
    public static string FormatReplyTrace(FileServerRequest request, byte[]? payload, bool success)
    {
        if (payload == null || payload.Length == 0)
            return success ? "OK (empty reply)" : "FAIL (no reply)";

        byte[] reply = payload;
        string hex = FileServerPacketFormatter.FormatHex(reply, reply.Length);
        int hexPairs = CountHexPairs(hex);

        StringBuilder builder = new StringBuilder(256);
        if (reply[0] == FileServerOpcodes.LoginSuccessReturnCode)
        {
            builder.Append("Login OK");
            builder.Append($" | bytes={reply.Length} hexPairs={hexPairs}");
            builder.Append(" | hex ");
            builder.Append(hex);
            return builder.ToString();
        }

        builder.Append(success ? "OK " : "FAIL ");
        builder.Append(DescribeReplySummary(request, reply, success));
        builder.Append($" | bytes={reply.Length} hexPairs={hexPairs}");
        if (reply.Length != hexPairs)
            builder.Append(" MISMATCH");
        builder.Append(" | hex ");
        builder.Append(hex);

        string decoded = DescribeDecodedReply(request, reply);
        if (!string.IsNullOrEmpty(decoded))
        {
            builder.Append(" | ");
            builder.Append(decoded);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Splits the reply to a get object info request with command 6 into named fields.
    /// </summary>
    /// <param name="payload">
    /// The reply bytes.
    /// </param>
    /// <returns>
    /// A dictionary of the field values, or a dictionary with only an error entry when the payload is shorter than 17 bytes.
    /// </returns>
    public static IReadOnlyDictionary<string, object?> DescribeCmd6Fields(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 17)
        {
            return new Dictionary<string, object?>
            {
                ["error"] = $"need>=17 got={payload.Length}",
            };
        }

        string name = Encoding.ASCII.GetString(payload.Slice(5, 10));
        return new Dictionary<string, object?>
        {
            // Bytes 0 and 1 are the return code and the result code of any reply (0 and 0 on success).
            // They used to be reported as the low and high byte of a load address, which this form does not carry.
            ["returnCode"] = payload[0],
            ["resultCode"] = payload[1],
            ["fileType"] = payload[2],
            ["reserved"] = payload[3],
            ["nameLength"] = payload[4],
            ["name"] = name.TrimEnd(),
            ["nameRaw"] = name,
            ["ownerFlag"] = payload[15],
            ["entryCount"] = payload[16],
        };
    }

    private static int CountHexPairs(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex == "(none)")
            return 0;

        int count = 0;
        for (int i = 0; i < hex.Length; i++)
        {
            if (hex[i] == ' ')
                continue;

            if (i + 1 < hex.Length)
            {
                count++;
                i++;
            }
        }

        return count;
    }

    /// <summary>
    /// Judges whether a reply is a success.
    /// </summary>
    /// <param name="fsRequest">
    /// The request the reply answers.
    /// </param>
    /// <param name="payload">
    /// The reply bytes, or null when there is no reply.
    /// </param>
    /// <returns>
    /// False for the three zero byte reply, for a reply with a non-zero error byte at index 1, and for a get object info reply that is too short or reports an object type of 0. True in all other cases, including a missing reply, a reply shorter than 2 bytes and a login reply.
    /// </returns>
    public static bool EvaluateReplySuccess(FileServerRequest fsRequest, byte[]? payload)
    {
        if (payload == null || payload.Length < 2)
            return true;

        if (payload[0] == FileServerOpcodes.LoginSuccessReturnCode)
            return true;

        if (payload.Length == 3 && payload[0] == 0 && payload[1] == 0 && payload[2] == 0)
            return false;

        if (payload[1] != 0)
            return false;

        if (fsRequest.Opcode == FileServerOpcodes.GetObjectInfo &&
            GetObjectInfoRequestParser.TryParse(fsRequest, out byte command, out _))
        {
            int expected = GetObjectInfoRequestParser.GetExpectedMinReplyLength(command);
            if (payload.Length < expected)
                return false;

            if (payload[2] == 0)
                return false;
        }

        if (fsRequest.Opcode == FileServerOpcodes.Examine && payload.Length >= 2 && payload[1] != 0)
            return false;

        return true;
    }

    /// <summary>
    /// Judges whether a reply is a success. Calls EvaluateReplySuccess.
    /// </summary>
    /// <param name="fsRequest">
    /// The request the reply answers.
    /// </param>
    /// <param name="payload">
    /// The reply bytes, or null when there is no reply.
    /// </param>
    /// <returns>
    /// The result of EvaluateReplySuccess.
    /// </returns>
    public static bool EvaluateSuccess(FileServerRequest fsRequest, byte[]? payload) =>
        EvaluateReplySuccess(fsRequest, payload);

    /// <summary>
    /// A short, redacted, one-line description of a request for normal-operation logs.
    /// </summary>
    /// <remarks>
    /// Enough to see what happened — the operation and its key argument — with no hex dump and no
    /// private detail. The full byte-level trace belongs at debug level via
    /// <see cref="FormatRequestTrace"/>. For OSCLI this is the command text with any password masked;
    /// for other operations it is the parsed descriptor, or just the operation name when there is no
    /// concise parse.
    /// </remarks>
    /// <param name="request">
    /// The request to summarise.
    /// </param>
    /// <returns>
    /// A single line safe to log during normal operation.
    /// </returns>
    public static string FormatShortSummary(FileServerRequest request)
    {
        string parsed = DescribeParsedRequest(request);
        if (!string.IsNullOrEmpty(parsed))
            return parsed;

        return FileServerOpcodeNames.GetName(request.Opcode);
    }

    /// <summary>
    /// A short, one-line result descriptor for normal-operation logs.
    /// </summary>
    /// <remarks>
    /// "OK" plus the reply length on success, or the Acorn error code and its message on failure.
    /// Error text is not private, so it is shown in full to aid triage.
    /// </remarks>
    /// <param name="payload">
    /// The reply payload bytes, or null when there was no reply.
    /// </param>
    /// <param name="success">
    /// Whether the reply was evaluated as a success.
    /// </param>
    /// <returns>
    /// A single line safe to log during normal operation.
    /// </returns>
    public static string FormatShortResult(byte[]? payload, bool success)
    {
        if (payload == null || payload.Length == 0)
            return success ? "OK" : "FAIL";

        if (success)
            return $"OK ({payload.Length}B)";

        // Error replies are [0x00, errorCode, message..., 0x0D]. Show the code and text — neither is
        // private, and both speed up diagnosis.
        if (payload.Length >= 2)
        {
            string message = ExtractErrorMessage(payload);
            return $"FAIL &{payload[1]:X2} {message}".TrimEnd();
        }

        return "FAIL";
    }

    private static string ExtractErrorMessage(byte[] payload)
    {
        int end = payload.Length;
        if (end > 2 && payload[end - 1] == 0x0D)
            end--;

        if (end <= 2)
            return string.Empty;

        StringBuilder builder = new StringBuilder(end - 2);
        for (int i = 2; i < end; i++)
        {
            byte value = payload[i];
            builder.Append(value is >= 0x20 and <= 0x7E ? (char)value : '.');
        }

        return builder.ToString();
    }

    private static string DescribeParsedRequest(FileServerRequest request)
    {
        return request.Opcode switch
        {
            FileServerOpcodes.OsCli => $"OSCLI \"{EconetLogRedaction.RedactCommandText(AcornPath.ReadCrTerminatedString(request.Parameters)).Replace("\r", "\\r")}\"",
            FileServerOpcodes.Examine => DescribeExamineParse(request.Parameters),
            FileServerOpcodes.GetObjectInfo => DescribeGetObjectInfoParse(request),
            FileServerOpcodes.CatHeader => $"path=\"{FormatPathParameter(request.Parameters)}\"",
            _ => string.Empty,
        };
    }

    private static string DescribeExamineParse(ReadOnlySpan<byte> parameters)
    {
        if (!ExamineRequestParser.TryParse(parameters, out byte arg, out byte start, out byte count, out ReadOnlySpan<byte> pathBytes))
            return "examine parse failed";

        bool pathOnly = parameters.Length > 0 && parameters[0] > 3;
        string mode = pathOnly ? "path-only→arg0" : "structured";
        return $"examine {mode} arg={arg} start={start} n={count} path=\"{ExamineRequestParser.FormatPathPreview(pathBytes)}\" params={FileServerPacketFormatter.FormatHex(parameters, 32)}";
    }

    private static string DescribeGetObjectInfoParse(FileServerRequest request)
    {
        if (!GetObjectInfoRequestParser.TryParse(request, out byte command, out ReadOnlySpan<byte> pathBytes))
            return "get-object-info parse failed";

        byte relativeTo = AcornFileHandles.DivHandle(request.Cwd);
        bool implicitCmd = request.Parameters.Length > 0 && !GetObjectInfoRequestParser.IsKnownCommand(request.Parameters[0]);
        string mode = implicitCmd ? "implicit-cmd" : "explicit-cmd";
        if (request.Parameters.IsEmpty)
            mode = "empty-params→cmd6";

        int expected = GetObjectInfoRequestParser.GetExpectedMinReplyLength(command);
        return $"get-object-info {mode} cmd={command} expectReply={expected}B path=\"{GetObjectInfoRequestParser.FormatPathPreview(pathBytes)}\" rel=&{relativeTo:X2} params={FileServerPacketFormatter.FormatHex(request.Parameters, 32)}";
    }

    private static string FormatPathParameter(ReadOnlySpan<byte> parameters)
    {
        if (parameters.IsEmpty)
            return "(cwd)";

        return AcornPath.ReadCrTerminatedString(parameters).Trim();
    }

    private static string DescribeReplySummary(FileServerRequest request, byte[] payload, bool success)
    {
        if (!success && payload.Length >= 2 && payload[1] != 0)
        {
            string msg = payload.Length > 2
                ? Encoding.ASCII.GetString(payload, 2, Math.Min(payload.Length - 2, 40)).TrimEnd('\r')
                : string.Empty;
            return $"err=&{payload[1]:X2} {FileServerErrorNames.GetName(payload[1])} {msg}".Trim();
        }

        if (payload.Length == 3 && payload[0] == 0 && payload[1] == 0 && payload[2] == 0)
            return "soft-not-found [00 00 00]";

        if (request.Opcode == FileServerOpcodes.GetObjectInfo &&
            GetObjectInfoRequestParser.TryParse(request, out byte command, out _))
        {
            int expected = GetObjectInfoRequestParser.GetExpectedMinReplyLength(command);
            string lenNote = payload.Length < expected ? $"SHORT need>={expected}" : "len-ok";
            return $"cmd{command} type=&{payload.ElementAtOrDefault(2):X2} {lenNote}";
        }

        if (request.Opcode == FileServerOpcodes.Examine && payload.Length >= 4)
            return $"examined={payload[2]} dirsize={payload[3]} term={(payload[^1] == 0x80 ? "0x80" : "?")}";

        if (payload.Length >= 2 && payload[1] == 0)
            return "success";

        return "reply";
    }

    private static string DescribeDecodedReply(FileServerRequest request, byte[] payload)
    {
        if (request.Opcode == FileServerOpcodes.GetObjectInfo &&
            GetObjectInfoRequestParser.TryParse(request, out byte command, out _) &&
            command == 6 &&
            payload.Length >= 17)
        {
            IReadOnlyDictionary<string, object?> fields = DescribeCmd6Fields(payload);
            return $"cmd6 dir=\"{fields["name"]}\" entries={fields["entryCount"]} owner=&{payload[15]:X2} fields=[0]=rc &{payload[0]:X2} [1]=result &{payload[1]:X2} [2]=type &{payload[2]:X2} [3]=&{payload[3]:X2} [4]={payload[4]} [5-14]=name [15]=&{payload[15]:X2} [16]={payload[16]}";
        }

        if (request.Opcode == FileServerOpcodes.GetObjectInfo &&
            GetObjectInfoRequestParser.TryParse(request, out byte cmd, out _) &&
            cmd == 1 &&
            payload.Length >= 5)
        {
            return $"cmd1 ftype=&{payload[2]:X2} date={payload[3]:X2}/{payload[4]:X2}";
        }

        if (request.Opcode == FileServerOpcodes.Examine && payload.Length > 4)
        {
            int count = payload[2];
            if (count > 0 && payload.Length >= 4 + 27)
            {
                string name = Encoding.ASCII.GetString(payload, 4, 10).TrimEnd();
                return $"entry0=\"{name}\" len24-26={payload[28]:X2} {payload[29]:X2} {payload[30]:X2}";
            }
        }

        if (request.Opcode == FileServerOpcodes.OsCli && payload.Length >= 3 && payload[1] == 0)
        {
            return $"redirect fsop=&{payload[0]:X2} path=\"{AcornPath.ReadCrTerminatedString(payload.AsSpan(2)).Trim()}\"";
        }

        return string.Empty;
    }
}
