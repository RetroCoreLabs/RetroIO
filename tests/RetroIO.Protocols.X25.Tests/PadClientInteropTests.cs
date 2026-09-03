using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Client;
using RetroIO.Protocols.X25.Modules;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// End-to-end tests for the console PAD path: a <see cref="PadClient"/> driven over the clean
    /// <see cref="X25Client"/> (exactly as the x25pad program wires it) against a loopback echo peer. The
    /// PAD is exercised through <see cref="PadClient.OnKey"/> just as keystrokes would drive it, so the full
    /// X.28 command handling, call setup, data forwarding, and echo delivery are covered without a terminal.
    /// </summary>
    [TestFixture]
    public class PadClientInteropTests
    {
        /// <summary>
        /// Typing <c>CALL 200</c> connects the PAD to the peer, and typed data is echoed back and delivered
        /// to the PAD's data-received event.
        /// </summary>
        [Test]
        public void Pad_CallCommand_Connects_And_EchoesTypedData()
        {
            using LoopbackX25Peer peer = new LoopbackX25Peer("200");
            using X25Client client = new X25Client { ServiceName = "pad-test" };

            X25ClientPadTransport transport = new X25ClientPadTransport(client);
            PadClient pad = new PadClient(NullLogger.Instance, transport)
            {
                LocalAddress = "10000"
            };

            // Inbound wiring, identical to the x25pad program.
            client.SetOnConnected((c, vc) => pad.NotifyCallAccepted(vc.Lcn));
            client.SetOnData((c, vc, data, length, qBit, mBit, dBit) => pad.DeliverData(vc.Lcn, data, qBit));
            client.SetOnClear((c, vc, cause, diagnostic) => pad.DeliverClear(vc.Lcn));

            ManualResetEventSlim connected = new ManualResetEventSlim(false);
            ManualResetEventSlim gotEcho = new ManualResetEventSlim(false);
            StringBuilder received = new StringBuilder();

            pad.ConnectionStateChanged += (sender, e) =>
            {
                if (e.State == PadConnectionState.Connected)
                {
                    connected.Set();
                }
            };
            pad.DataReceived += (sender, e) =>
            {
                received.Append(e.Data);
                if (received.ToString().Contains("HELLO"))
                {
                    gotEcho.Set();
                }
            };

            client.Open(new X25ClientConfig
            {
                Host = "127.0.0.1",
                Port = (ushort)peer.Port,
                LocalAddress = "10000",
                Framing = X25ClientFraming.Xot
            }, NullLogger.Instance);

            pad.Initialize();

            // Place the call by typing the X.28 command, then Enter (CR+LF).
            TypeLine(pad, "CALL 200");
            Assert.That(connected.Wait(3000), Is.True, "the PAD should connect after CALL 200");

            // In data-transfer mode: typed data is forwarded, echoed by the peer, and delivered back.
            TypeLine(pad, "HELLO");
            Assert.That(gotEcho.Wait(3000), Is.True, "the peer's echo of typed data should reach the PAD");

            pad.Dispose();
        }

        /// <summary>
        /// Typing <c>CLR</c> after connecting tears the circuit down.
        /// </summary>
        [Test]
        public void Pad_ClearCommand_Disconnects()
        {
            using LoopbackX25Peer peer = new LoopbackX25Peer("200");
            using X25Client client = new X25Client { ServiceName = "pad-test" };

            X25ClientPadTransport transport = new X25ClientPadTransport(client);
            PadClient pad = new PadClient(NullLogger.Instance, transport)
            {
                LocalAddress = "10000"
            };

            client.SetOnConnected((c, vc) => pad.NotifyCallAccepted(vc.Lcn));
            client.SetOnData((c, vc, data, length, qBit, mBit, dBit) => pad.DeliverData(vc.Lcn, data, qBit));
            client.SetOnClear((c, vc, cause, diagnostic) => pad.DeliverClear(vc.Lcn));

            ManualResetEventSlim connected = new ManualResetEventSlim(false);
            ManualResetEventSlim disconnected = new ManualResetEventSlim(false);
            pad.ConnectionStateChanged += (sender, e) =>
            {
                if (e.State == PadConnectionState.Connected)
                {
                    connected.Set();
                }
                else if (e.State == PadConnectionState.Disconnected)
                {
                    disconnected.Set();
                }
            };

            client.Open(new X25ClientConfig
            {
                Host = "127.0.0.1",
                Port = (ushort)peer.Port,
                LocalAddress = "10000",
                Framing = X25ClientFraming.Xot
            }, NullLogger.Instance);

            pad.Initialize();

            TypeLine(pad, "CALL 200");
            Assert.That(connected.Wait(3000), Is.True, "the PAD should connect after CALL 200");

            // In data-transfer mode a PAD command is only recognized after escaping to command mode with
            // DLE (Ctrl+P, 0x10), per ITU-T X.28; then CLR clears the call.
            pad.OnKey('\u0010');
            TypeLine(pad, "CLR");
            Assert.That(disconnected.Wait(3000), Is.True, "the PAD should disconnect after CLR");

            pad.Dispose();
        }

        /// <summary>
        /// Feeds a line of characters to the PAD followed by Enter (CR+LF), matching how the console front-end
        /// drives it.
        /// </summary>
        /// <param name="pad">
        /// The PAD to drive.
        /// </param>
        /// <param name="text">
        /// The characters to type before Enter.
        /// </param>
        private static void TypeLine(PadClient pad, string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                pad.OnKey(text[i]);
            }

            // Enter sends CR+LF (X.28 line handling), as the console front-end does.
            pad.OnKey('\r');
            pad.OnKey('\n');
        }
    }
}
