//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

using Google.Protobuf;

using Microsoft.Extensions.Logging;

using LogLevel = Microsoft.Extensions.Logging.LogLevel;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Api;
using RetroIO.Protocols.Xmsg.Api.Node;
using RetroIO.Protocols.Xmsg.Plugins.Contracts;

namespace RetroIO.Protocols.Xmsg.Plugins
{
    /// <summary>
    /// The node's side of the plug-in connection: turns each plug-in's requests into XMSG kernel and
    /// XROUT calls on the node, and hands back what arrives on the plug-in's ports.
    /// </summary>
    /// <remarks>
    /// <para><b>One kernel, one name table, for every plug-in</b></para>
    /// The plug-ins' ports are opened in the kernel of one <see cref="XmsgKernelServer"/>, the bridge
    /// the node already uses for task-style servers, and their names go in its
    /// <see cref="XroutDirectory"/>. So a SINTRAN machine reaches a plug-in exactly as it reaches an
    /// in-process task: a letter to the name is forwarded whole (<see cref="XroutLetter"/>), and
    /// data to a port is delivered to that port. Two plug-ins can never get the same port, because
    /// one kernel hands out all of them.
    /// <para><b>One thread touches the node</b></para>
    /// The node keeps its state on one pump thread with no locks. gRPC runs on its own threads, so
    /// <see cref="Submit"/> and <see cref="Detach"/> only put work in a queue, and <see cref="Pump"/>,
    /// called on the node's pump thread, does it - the same rule as the Ethernet links
    /// (<c>DeliverInboundOnPump</c>). Answers and arrivals go into each session's own outbound
    /// queue, so a slow plug-in delays only itself.
    /// <para><b>A plug-in that goes away</b></para>
    /// <see cref="Detach"/> closes every port the plug-in opened and clears every name it claimed.
    /// New letters to those names then find no name, as on a SINTRAN machine whose server program
    /// stopped. Design: docs/XMSG-PLUGIN-SERVERS.md in the RetroServer repository.
    /// </remarks>
    public sealed class PluginServerHost
    {
        private readonly XmsgKernelServer _bridge;
        private readonly XroutDirectory _directory;
        private readonly ConcurrentQueue<Work> _work;
        private readonly List<PluginSession> _sessions;
        private readonly byte[] _scratch;
        private int _nextSession;

        /// <summary>
        /// The largest message the host reads off a plug-in's port in one go. XMSG messages on an
        /// ND are far smaller; this only bounds the scratch buffer.
        /// </summary>
        public const int MaximumMessageSize = 65536;

        /// <summary>
        /// Initialises the host on a bridge and its name table.
        /// </summary>
        /// <param name="bridge">
        /// The bridge whose kernel the plug-ins' ports are opened in. Register it with the node.
        /// </param>
        /// <param name="directory">
        /// The name table plug-ins claim names in. It is set as the bridge's
        /// <see cref="XmsgKernelServer.Directory"/>, so letters from the wire reach those names.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when either argument is null.
        /// </exception>
        public PluginServerHost(XmsgKernelServer bridge, XroutDirectory directory)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _directory = directory ?? throw new ArgumentNullException(nameof(directory));
            _bridge.Directory = directory;
            _work = new ConcurrentQueue<Work>();
            _sessions = new List<PluginSession>();
            _scratch = new byte[MaximumMessageSize];
        }

        /// <summary>
        /// Gets or sets the secret a program must send in its Hello, or null or empty for none
        /// (insecure mode).
        /// </summary>
        /// <remarks>
        /// Decided by Ronny, 2026-11-10: when a token is set it is ALWAYS checked, also for a program
        /// on the same machine. A wrong or missing token is answered with XEPVR (privilege request
        /// refused) and the program stays unable to do anything on the stream.
        /// </remarks>
        public string? Token { get; set; }

        /// <summary>
        /// Gets or sets where the plug-ins' log lines go, or null to drop them. RetroServer passes its
        /// own logging, so the lines show in its log view with its own lines.
        /// </summary>
        public ILoggerFactory? Logs { get; set; }

        /// <summary>
        /// Gets or sets the lowest level kept from a plug-in's log lines. Default Information. Told to
        /// each plug-in in the answer to its Hello, so it need not send what would be dropped.
        /// </summary>
        public LogLevel LogMinimum { get; set; } = LogLevel.Information;

        /// <summary>
        /// Gets the node's system number, as told to a plug-in in the answer to its Hello.
        /// </summary>
        public ushort SystemNumber
        {
            get { return _bridge.Kernel.SystemNumber; }
        }

        /// <summary>
        /// Starts a session for a newly connected plug-in. Safe from any thread.
        /// </summary>
        /// <returns>
        /// The session. Its <see cref="PluginSession.Outbound"/> queue carries what to send back.
        /// </returns>
        public PluginSession Attach()
        {
            int id = System.Threading.Interlocked.Increment(ref _nextSession);
            PluginSession session = new PluginSession(id);
            _work.Enqueue(new Work(session, null, WorkKind.Attach));
            return session;
        }

        /// <summary>
        /// Queues one request from a plug-in. Safe from any thread; done by the next <see cref="Pump"/>.
        /// </summary>
        /// <param name="session">
        /// The plug-in's session.
        /// </param>
        /// <param name="request">
        /// The request.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when either argument is null.
        /// </exception>
        public void Submit(PluginSession session, PluginRequest request)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            _work.Enqueue(new Work(session, request, WorkKind.Request));
        }

        /// <summary>
        /// Ends a plug-in's session: its ports are closed and its names cleared by the next
        /// <see cref="Pump"/>. Safe from any thread.
        /// </summary>
        /// <param name="session">
        /// The session.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="session"/> is null.
        /// </exception>
        public void Detach(PluginSession session)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            _work.Enqueue(new Work(session, null, WorkKind.Detach));
        }

        /// <summary>
        /// Checks a Hello's token against <see cref="Token"/>.
        /// </summary>
        /// <param name="offered">
        /// The token the program sent.
        /// </param>
        /// <returns>
        /// True when no token is configured, or the two are the same.
        /// </returns>
        /// <remarks>
        /// Compared in a time that does not depend on where the two first differ, so the answer's
        /// timing does not give the secret away a byte at a time.
        /// </remarks>
        private bool TokenMatches(string offered)
        {
            if (string.IsNullOrEmpty(Token))
            {
                return true;
            }

            byte[] expected = System.Text.Encoding.UTF8.GetBytes(Token);
            byte[] given = System.Text.Encoding.UTF8.GetBytes(offered ?? string.Empty);
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, given);
        }

        /// <summary>
        /// Gets a copy of the sessions now attached, for status pages. Call on the pump thread.
        /// </summary>
        /// <returns>
        /// The sessions.
        /// </returns>
        public PluginSession[] CopySessions()
        {
            return _sessions.ToArray();
        }

        /// <summary>
        /// Does the queued work and collects what arrived on the plug-ins' ports. Call on the node's
        /// pump thread, every tick.
        /// </summary>
        /// <returns>
        /// How many requests and arrivals were handled.
        /// </returns>
        public int Pump()
        {
            int handled = 0;

            Work work;
            while (_work.TryDequeue(out work))
            {
                switch (work.Kind)
                {
                    case WorkKind.Attach:
                        _sessions.Add(work.Session);
                        break;
                    case WorkKind.Detach:
                        Release(work.Session);
                        _sessions.Remove(work.Session);
                        break;
                    default:
                        Apply(work.Session, work.Request!);
                        break;
                }

                handled++;
            }

            for (int i = 0; i < _sessions.Count; i++)
            {
                handled += CollectArrivals(_sessions[i]);
            }

            return handled;
        }

        private void Apply(PluginSession session, PluginRequest request)
        {
            if (session.Detached)
            {
                return;
            }

            XmsgKernel kernel = _bridge.Kernel;

            switch (request.BodyCase)
            {
                case PluginRequest.BodyOneofCase.Hello:
                    if (!TokenMatches(request.Hello.Token))
                    {
                        Answer(session, request.Id, XmsgStatus.Failure(XmsgError.XEPVR).Value, 0, "Wrong or missing token.");
                        return;
                    }

                    session.PluginName = request.Hello.PluginName;
                    session.Version = request.Hello.Version;
                    session.ProtocolRevision = request.Hello.ProtocolRevision;
                    session.SaidHello = true;
                    session.Post(new HostEvent
                    {
                        Result = new Result
                        {
                            AnswersId = request.Id,
                            Status = XmsgStatus.Completed.Value,
                            Value = SystemNumber,
                            Node = DescribeNode(),
                        },
                    });
                    return;

                case PluginRequest.BodyOneofCase.Log:
                    // Never answered, whatever happens: the program does not wait for it.
                    if (session.SaidHello)
                    {
                        KeepLog(session, request.Log);
                    }

                    return;
            }

            if (!session.SaidHello)
            {
                Answer(session, request.Id, XmsgStatus.Failure(XmsgError.XEILF).Value, 0, "The first request must be a Hello.");
                return;
            }

            switch (request.BodyCase)
            {
                case PluginRequest.BodyOneofCase.OpenPort:
                    OpenPort(session, request.Id, kernel);
                    break;

                case PluginRequest.BodyOneofCase.ClosePort:
                    ClosePort(session, request.Id, kernel, request.ClosePort.Magic);
                    break;

                case PluginRequest.BodyOneofCase.RegisterName:
                    RegisterName(session, request.Id, request.RegisterName);
                    break;

                case PluginRequest.BodyOneofCase.AdjustSeats:
                    AdjustSeats(session, request.Id, request.AdjustSeats);
                    break;

                case PluginRequest.BodyOneofCase.ClearName:
                    ClearName(session, request.Id, request.ClearName.Name);
                    break;

                case PluginRequest.BodyOneofCase.Send:
                    Send(session, request.Id, kernel, request.Send);
                    break;

                case PluginRequest.BodyOneofCase.SendLetter:
                    SendLetter(session, request.Id, request.SendLetter);
                    break;

                default:
                    Answer(session, request.Id, XmsgStatus.Failure(XmsgError.XEILF).Value, 0, "Unknown request.");
                    break;
            }
        }

        private void OpenPort(PluginSession session, uint id, XmsgKernel kernel)
        {
            XmsgPortNumber port;
            XmsgStatus opened = kernel.OpenPort(out port);
            if (opened.IsError)
            {
                Answer(session, id, opened.Value, 0, "The node could not open a port.");
                return;
            }

            XmsgMagicNumber magic;
            XmsgStatus converted = kernel.ConvertPortToMagic(port, out magic);
            if (converted.IsError)
            {
                kernel.ClosePort(port);
                Answer(session, id, converted.Value, 0, "The node could not name the new port.");
                return;
            }

            session.Ports.Add(magic.Value, port);
            _bridge.RegisterPort(magic.PortWord);
            Answer(session, id, XmsgStatus.Completed.Value, magic.Value, string.Empty);
        }

        private void ClosePort(PluginSession session, uint id, XmsgKernel kernel, uint magic)
        {
            XmsgPortNumber port;
            if (!session.Ports.TryGetValue(magic, out port))
            {
                Answer(session, id, XmsgStatus.Failure(XmsgError.XEIPN).Value, 0, "Not one of this plug-in's ports.");
                return;
            }

            XmsgStatus closed = kernel.ClosePort(port);
            session.Ports.Remove(magic);
            _bridge.UnregisterPort(new XmsgMagicNumber(magic).PortWord);
            Answer(session, id, closed.Value, 0, string.Empty);
        }

        private void RegisterName(PluginSession session, uint id, RegisterName request)
        {
            XmsgPortNumber port;
            if (!session.Ports.TryGetValue(request.Magic, out port))
            {
                Answer(session, id, (int)XroutError.XRUNM, 0, "Not one of this plug-in's ports.");
                return;
            }

            XmsgMagicNumber magic = new XmsgMagicNumber(request.Magic);
            XroutError result = request.ConnectionPort
                ? _directory.RegisterConnectionPort(request.Name, _bridge.Kernel, magic, request.Seats, request.Unique)
                : _directory.RegisterName(request.Name, _bridge.Kernel, magic);

            if (result == XroutError.XRSOK)
            {
                session.Names.Add(request.Name);
            }

            Answer(session, id, (int)result, 0, string.Empty);
        }

        private void AdjustSeats(PluginSession session, uint id, AdjustSeats request)
        {
            if (!session.Names.Contains(request.Name))
            {
                Answer(session, id, (int)XroutError.XRUNN, 0, "Not one of this plug-in's names.");
                return;
            }

            Answer(session, id, (int)_directory.AdjustFreeConnections(request.Name, request.Delta), 0, string.Empty);
        }

        private void ClearName(PluginSession session, uint id, string name)
        {
            if (!session.Names.Remove(name))
            {
                Answer(session, id, (int)XroutError.XRUNN, 0, "Not one of this plug-in's names.");
                return;
            }

            _directory.ClearName(name);
            Answer(session, id, (int)XroutError.XRSOK, 0, string.Empty);
        }

        private void Send(PluginSession session, uint id, XmsgKernel kernel, Send request)
        {
            XmsgPortNumber port;
            if (!session.Ports.TryGetValue(request.FromMagic, out port))
            {
                Answer(session, id, XmsgStatus.Failure(XmsgError.XEIPN).Value, 0, "Not one of this plug-in's ports.");
                return;
            }

            // The calls a SINTRAN task makes to send: reserve a buffer, write into it, send it
            // from the port (it is the task's current message, so XFSND takes it).
            byte[] data = request.Data.ToByteArray();
            XmsgMessageIdentifier buffer;
            XmsgStatus reserved = kernel.ReserveBuffer(data.Length, XmsgBufferOptions.None, out buffer);
            if (reserved.IsError)
            {
                Answer(session, id, reserved.Value, 0, "No message buffer.");
                return;
            }

            int written;
            XmsgStatus wrote = kernel.Write(buffer, data, 0, false, out written);
            if (wrote.IsError)
            {
                kernel.ReleaseBuffer(buffer);
                Answer(session, id, wrote.Value, 0, "Could not fill the message buffer.");
                return;
            }

            XmsgStatus sent = kernel.Send(new XmsgMagicNumber(request.ToMagic), port, (XmsgSendFlags)request.Flags);
            if (sent.IsError)
            {
                // A send that fails leaves the buffer with the task; give it back.
                kernel.ReleaseBuffer(buffer);
            }

            Answer(session, id, sent.Value, 0, string.Empty);
        }

        private void SendLetter(PluginSession session, uint id, SendLetter request)
        {
            if (!session.Ports.ContainsKey(request.FromMagic))
            {
                Answer(session, id, (int)XroutError.XRUNM, 0, "Not one of this plug-in's ports.");
                return;
            }

            XroutError result = _directory.SendLetter(
                request.Name, new XmsgMagicNumber(request.FromMagic), request.Data.Span);
            Answer(session, id, (int)result, 0, string.Empty);
        }

        private int CollectArrivals(PluginSession session)
        {
            XmsgKernel kernel = _bridge.Kernel;
            int handled = 0;

            foreach (KeyValuePair<uint, XmsgPortNumber> entry in session.Ports)
            {
                while (true)
                {
                    XmsgReceiveResult arrived = kernel.Receive(entry.Value, XmsgWaitOptions.None);
                    if (!arrived.Received)
                    {
                        break;
                    }

                    XmsgMessageStatus status = kernel.GetMessageStatus(arrived.Message);
                    int read;
                    kernel.Read(arrived.Message, _scratch, 0, out read);
                    kernel.ReleaseBuffer(arrived.Message);

                    Arrived message = new Arrived
                    {
                        ToMagic = entry.Key,
                        FromMagic = status.Sender.Value,
                        MessageType = (int)arrived.MessageType!.Value,
                        Data = ByteString.CopyFrom(_scratch, 0, read),
                        // A returned message carries why it came back in the receive's extra word.
                        ReturnReason = arrived.MessageType == XmsgMessageType.XMTRE ? arrived.Extra : 0,
                    };
                    session.Post(new HostEvent { Arrived = message });
                    session.Arrivals++;
                    handled++;
                }
            }

            return handled;
        }

        private void Release(PluginSession session)
        {
            session.Detached = true;
            XmsgKernel kernel = _bridge.Kernel;

            foreach (string name in session.Names)
            {
                _directory.ClearName(name);
            }

            session.Names.Clear();

            foreach (KeyValuePair<uint, XmsgPortNumber> entry in session.Ports)
            {
                kernel.ClosePort(entry.Value);
                _bridge.UnregisterPort(new XmsgMagicNumber(entry.Key).PortWord);
            }

            session.Ports.Clear();
        }

        /// <summary>
        /// What the node tells a plug-in about itself in the answer to its Hello.
        /// </summary>
        /// <returns>
        /// The node's revision, its features and the lowest log level it keeps.
        /// </returns>
        private NodeInfo DescribeNode()
        {
            NodeInfo node = new NodeInfo
            {
                ProtocolRevision = (uint)Contracts.ProtocolRevision.Current,
                LogMinimum = (Contracts.LogLevel)(int)LogMinimum,
            };
            node.Features.Add("token");
            node.Features.Add("returned-messages");
            node.Features.Add("log");
            return node;
        }

        /// <summary>
        /// Passes one log line from a plug-in to the node's logging.
        /// </summary>
        /// <param name="session">
        /// The plug-in.
        /// </param>
        /// <param name="line">
        /// The line.
        /// </param>
        /// <remarks>
        /// The category is <c>XmsgProgram.&lt;plugin name&gt;</c>, plus the program's own category when it
        /// gave one, so the log view can filter one program. The levels have the same numbers on the
        /// wire as here (the proto's LogLevel), so the level is passed on unchanged; a number outside
        /// them is kept as Information rather than dropped. The node's log store stamps its own time;
        /// when the program's time differs by more than a second (a slow link, a wrong clock) it is
        /// added to the text so the difference is visible.
        /// </remarks>
        private void KeepLog(PluginSession session, Contracts.Log line)
        {
            ILoggerFactory? logs = Logs;
            int number = (int)line.Level;
            LogLevel level = number >= (int)LogLevel.Trace && number <= (int)LogLevel.Critical
                ? (LogLevel)number
                : LogLevel.Information;
            if (logs == null || level < LogMinimum || LogMinimum == LogLevel.None)
            {
                return;
            }

            string category = "XmsgProgram." + (session.PluginName.Length > 0 ? session.PluginName : "unnamed");
            if (line.Category.Length > 0)
            {
                category += "." + line.Category;
            }

            string text = line.Text;
            if (line.UnixTimeMs != 0)
            {
                DateTimeOffset said = DateTimeOffset.FromUnixTimeMilliseconds(line.UnixTimeMs);
                if (Math.Abs((DateTimeOffset.UtcNow - said).TotalSeconds) > 1)
                {
                    text = "[program time " + said.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + " UTC] " + text;
                }
            }

            if (line.Exception.Length > 0)
            {
                text += Environment.NewLine + line.Exception;
            }

            logs.CreateLogger(category).Log(level, "{Text}", text);
            session.LogLines++;
        }

        private static void Answer(PluginSession session, uint id, int status, uint value, string text)
        {
            session.Post(new HostEvent
            {
                Result = new Result { AnswersId = id, Status = status, Value = value, Text = text },
            });
        }

        private enum WorkKind
        {
            Request,
            Attach,
            Detach,
        }

        private readonly struct Work
        {
            internal Work(PluginSession session, PluginRequest? request, WorkKind kind)
            {
                Session = session;
                Request = request;
                Kind = kind;
            }

            internal PluginSession Session { get; }

            internal PluginRequest? Request { get; }

            internal WorkKind Kind { get; }
        }
    }
}
