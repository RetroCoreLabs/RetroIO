using System;
using RetroIO.Protocols.X25.Switch.Model;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Switch
{
    /// <summary>
    /// The switching engine: it watches every attached port, routes incoming calls to an egress port,
    /// stitches the two circuits together, forwards data in both directions, and tears the pair down on
    /// clear — maintaining counters, accounts, and Call Detail Records as it goes.
    /// </summary>
    public sealed class SwitchCore
    {
        /// <summary>
        /// Clear cause: call cleared by the originating DTE / normal teardown.
        /// </summary>
        public const byte CauseDteOriginated = 0x00;

        /// <summary>
        /// Clear cause: destination address is not obtainable (no route).
        /// </summary>
        public const byte CauseNotObtainable = 0x0D;

        private readonly SwitchNode _node;

        /// <summary>
        /// Raised when a routed circuit is established.
        /// </summary>
        public event CallSetupHandler? CallSetup;

        /// <summary>
        /// Raised when a circuit is cleared.
        /// </summary>
        public event CallClearHandler? CallClear;

        /// <summary>
        /// Raised when a torn-down circuit produces a Call Detail Record.
        /// </summary>
        public event CdrHandler? CdrRecorded;

        /// <summary>
        /// The reserved management address. Incoming calls to this address are handed to
        /// <see cref="ManagementCall"/> instead of being routed. Null or empty disables it.
        /// </summary>
        public string? ManagementAddress { get; set; }

        /// <summary>
        /// Raised when a call arrives for <see cref="ManagementAddress"/>. The handler owns accepting the
        /// call and running the management session on it.
        /// </summary>
        public event PortIncomingCall? ManagementCall;

        /// <summary>
        /// Creates the engine over a switch node and wires all currently attached ports.
        /// </summary>
        /// <param name="node">
        /// The switch runtime state to operate on.
        /// </param>
        public SwitchCore(SwitchNode node)
        {
            _node = node ?? throw new ArgumentNullException(nameof(node));
            for (int i = 0; i < _node.Ports.Count; i++)
            {
                Wire(_node.Ports[i]);
            }
        }

        /// <summary>
        /// Subscribes the engine to a port's events. Call this for any port attached after construction.
        /// </summary>
        /// <param name="port">
        /// The port to wire.
        /// </param>
        public void Wire(ISwitchPort port)
        {
            port.IncomingCall += OnIncomingCall;
            port.CallConnected += OnCallConnected;
            port.DataReceived += OnDataReceived;
            port.CallCleared += OnCallCleared;
        }

        private static long NowMs()
        {
            return Environment.TickCount64;
        }

        private void OnIncomingCall(ISwitchPort inPort, ushort inLci, string calledAddress, string callingAddress, byte[] callUserData)
        {
            inPort.Counters.IncrementCallsIn();

            // Calls to the reserved management address are handed to the management surface, not routed.
            if (!string.IsNullOrEmpty(ManagementAddress) && calledAddress == ManagementAddress)
            {
                ManagementCall?.Invoke(inPort, inLci, calledAddress, callingAddress, callUserData);
                return;
            }

            // Resolve an egress port for the dialled address; never route back to the ingress port.
            if (!_node.Config.Routing.TryLookup(calledAddress, inPort.Id, out int outPortId))
            {
                inPort.ClearCall(inLci, CauseNotObtainable, 0);
                inPort.Counters.IncrementCallsCleared();
                return;
            }

            ISwitchPort? outPort = _node.GetPort(outPortId);
            if (outPort == null)
            {
                inPort.ClearCall(inLci, CauseNotObtainable, 0);
                inPort.Counters.IncrementCallsCleared();
                return;
            }

            // Place the onward call; a zero LCI means the egress port could not initiate it.
            ushort outLci = outPort.PlaceCall(calledAddress, callingAddress, callUserData);
            if (outLci == 0)
            {
                inPort.ClearCall(inLci, CauseNotObtainable, 0);
                inPort.Counters.IncrementCallsCleared();
                return;
            }

            outPort.Counters.IncrementCallsOut();

            VcStitch stitch = _node.CreateStitch(inPort, inLci, outPort, outLci);
            stitch.CallingAddress = callingAddress;
            stitch.CalledAddress = calledAddress;
            stitch.StartTimeMs = NowMs();
            stitch.HopCount = 0;

            // Accept the incoming call now that the onward leg is placed.
            inPort.AcceptCall(inLci);

            AccountForCallSetup(callingAddress, calledAddress);
            CallSetup?.Invoke(callingAddress, calledAddress, inPort.Id, outPort.Id);
        }

        private void OnCallConnected(ISwitchPort port, ushort lci)
        {
            // The onward leg was accepted by the far end. The stitch already exists (created when the call
            // was routed), so there is nothing further to establish; this is a notification hook.
        }

        private void OnDataReceived(ISwitchPort port, ushort lci, byte[] data, int length, bool qBit, bool mBit, bool dBit)
        {
            port.Counters.IncrementPacketsIn();

            // Forward ingress -> egress.
            VcStitch? ingress = _node.FindIngress(port, lci);
            if (ingress != null)
            {
                ingress.OutPort.SendData(ingress.OutLci, new ReadOnlySpan<byte>(data, 0, length), qBit, mBit, dBit);
                ingress.OutPort.Counters.IncrementPacketsOut();
                ingress.PacketsForward++;
                ingress.BytesForward += (ulong)length;
                return;
            }

            // Forward egress -> ingress.
            VcStitch? egress = _node.FindEgress(port, lci);
            if (egress != null)
            {
                egress.InPort.SendData(egress.InLci, new ReadOnlySpan<byte>(data, 0, length), qBit, mBit, dBit);
                egress.InPort.Counters.IncrementPacketsOut();
                egress.PacketsReverse++;
                egress.BytesReverse += (ulong)length;
            }
        }

        private void OnCallCleared(ISwitchPort port, ushort lci, byte cause, byte diagnostic)
        {
            port.Counters.IncrementCallsCleared();

            // Locate the stitch from either side and clear its peer.
            VcStitch? stitch = _node.FindIngress(port, lci);
            bool clearedFromIngress = stitch != null;
            if (stitch == null)
            {
                stitch = _node.FindEgress(port, lci);
            }

            if (stitch == null)
            {
                return; // no stitch (e.g. already torn down)
            }

            if (clearedFromIngress)
            {
                stitch.OutPort.ClearCall(stitch.OutLci, cause, diagnostic);
            }
            else
            {
                stitch.InPort.ClearCall(stitch.InLci, cause, diagnostic);
            }

            WriteCdr(stitch, cause, diagnostic);
            AccountForCallClear(stitch);
            _node.DestroyStitch(stitch);

            CallClear?.Invoke(port.Id, lci, cause, diagnostic);
        }

        private void WriteCdr(VcStitch stitch, byte cause, byte diagnostic)
        {
            if (CdrRecorded == null)
            {
                return;
            }

            Cdr cdr = new Cdr
            {
                StartTimeMs = stitch.StartTimeMs,
                EndTimeMs = NowMs(),
                Calling = stitch.CallingAddress,
                Called = stitch.CalledAddress,
                InPort = stitch.InPort.Id,
                OutPort = stitch.OutPort.Id,
                InLci = stitch.InLci,
                OutLci = stitch.OutLci,
                PacketsForward = stitch.PacketsForward,
                PacketsReverse = stitch.PacketsReverse,
                BytesForward = stitch.BytesForward,
                BytesReverse = stitch.BytesReverse,
                ClearCause = cause,
                ClearDiagnostic = diagnostic
            };

            CdrRecorded.Invoke(cdr);
        }

        private void AccountForCallSetup(string callingAddress, string calledAddress)
        {
            SwitchAccount? caller = FindAccount(callingAddress);
            if (caller != null)
            {
                caller.CallsMade++;
            }

            SwitchAccount? callee = FindAccount(calledAddress);
            if (callee != null)
            {
                callee.CallsReceived++;
            }
        }

        private void AccountForCallClear(VcStitch stitch)
        {
            ulong durationMs = (ulong)Math.Max(0, NowMs() - stitch.StartTimeMs);

            SwitchAccount? caller = FindAccount(stitch.CallingAddress);
            if (caller != null)
            {
                caller.PacketsSent += stitch.PacketsForward;
                caller.PacketsReceived += stitch.PacketsReverse;
                caller.BytesSent += stitch.BytesForward;
                caller.BytesReceived += stitch.BytesReverse;
                caller.TotalDurationMs += durationMs;
            }
        }

        private SwitchAccount? FindAccount(string address)
        {
            for (int i = 0; i < _node.Accounts.Count; i++)
            {
                if (_node.Accounts[i].Covers(address))
                {
                    return _node.Accounts[i];
                }
            }

            return null;
        }
    }
}
