//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

// The scripted host's API and its limits: what a script can reach, and what happens when a script misbehaves.

using NUnit.Framework;
using RetroIO.Protocols.X25.Host;
using RetroIO.Protocols.X25.Protocols;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>ScriptedHost: the machine and session API, the sandbox, and fault handling.</summary>
    [TestFixture]
    public class ScriptedHostTests
    {
        private static readonly DateTime T0 = new DateTime(1987, 5, 29, 12, 0, 0);

        private sealed class Output : IHostOutput
        {
            public AltosChatActionList List { get; } = new AltosChatActionList();

            public void Send(int session, ReadOnlySpan<byte> data) => List.Send(session, data);

            public void Clear(int session) => List.Clear(session);

            public void Log(string text) => List.Line(text);
        }

        private static (ScriptedHost Host, Output Out) Make(string script, Action<ScriptedHostSettings>? change = null)
        {
            var settings = new ScriptedHostSettings();
            change?.Invoke(settings);
            var output = new Output();
            var host = new ScriptedHost(script, settings, output);
            host.Start(T0);
            return (host, output);
        }

        private static byte[] B(string text) => System.Text.Encoding.ASCII.GetBytes(text);

        [Test]
        public void SendTurnsNewlineIntoTheLineEnd_AndCountsPending()
        {
            (ScriptedHost host, Output o) = Make("function onConnect(s){ s.send('hi\\n'); machine.log('p=' + s.pending()); }");
            host.Connect(1, "guest", "123");
            Assert.That(o.List.TextFor(1), Is.EqualTo("hi\r\n"));
            Assert.That(o.List.LogLines(), Is.EqualTo(new List<string> { "p=4" }));
            host.OutputDrained(1, 3);
            host.Input(1, B("x"));
        }

        [Test]
        public void MachineSend_ReachesTheUserByName_InAnyCase_AndSaysFalseForNobody()
        {
            (ScriptedHost host, Output o) = Make(
                "function onConnect(s){ if (s.id == 2) s.user = 'Bob'; }" +
                "function onInput(s, t){ machine.log('' + machine.send('bob', 'psst\\n', s) + ',' + machine.send('carol', 'x')); }");
            host.Connect(1, "ann", null);
            host.Connect(2, "", null);
            host.Input(1, B("go"));
            Assert.That(o.List.TextFor(2), Is.EqualTo("psst\r\n"));
            Assert.That(o.List.TextFor(1), Is.Empty);
            Assert.That(o.List.LogLines(), Is.EqualTo(new List<string> { "true,false" }));
        }

        [Test]
        public void SessionOnMessage_TakesSendAndBroadcast_WithTheSender()
        {
            (ScriptedHost host, Output o) = Make(
                "function onConnect(s){ if (s.id == 2) s.onMessage = function(text, from){ s.send('[' + (from ? from.user : 'all') + '] ' + text); }; }" +
                "function onInput(s, t){ if (t == 's') machine.send('bob', 'hi', s); else machine.broadcast('news', s.id); }");
            host.Connect(1, "ann", null);
            host.Connect(2, "bob", null);
            host.Input(1, B("s"));
            host.Input(1, B("b"));
            Assert.That(o.List.TextFor(2), Is.EqualTo("[ann] hi[all] news"));
            Assert.That(o.List.TextFor(1), Is.Empty, "broadcast leaves out the sender");
        }

        [Test]
        public void ARoom_SaysToItsMembersButTheSender_AndAGoneCallLeavesIt()
        {
            (ScriptedHost host, Output o) = Make(
                "var r = machine.room('lounge');" +
                "function onConnect(s){ if (s.id != 3) r.join(s); }" +
                "function onInput(s, t){ if (t == 'm') machine.log(r.name + ':' + r.members().length); else r.say(t, s); }");
            host.Connect(1, "ann", null);
            host.Connect(2, "bob", null);
            host.Connect(3, "eve", null);
            host.Input(1, B("hello"));
            Assert.That(o.List.TextFor(2), Is.EqualTo("hello"));
            Assert.That(o.List.TextFor(1), Is.Empty);
            Assert.That(o.List.TextFor(3), Is.Empty, "not a member");
            host.Disconnect(2);
            host.Input(1, B("m"));
            Assert.That(o.List.LogLines(), Is.EqualTo(new List<string> { "lounge:1" }));
        }

        [Test]
        public void ARoomOnMessage_IsCalledPerMember_InsteadOfTheDelivery()
        {
            (ScriptedHost host, Output o) = Make(
                "var r = machine.room('cb'); r.onMessage = function(m, text, from){ m.send(from.user + ' says: ' + text); };" +
                "function onConnect(s){ r.join(s); }" +
                "function onInput(s, t){ r.say(t, s); }");
            host.Connect(1, "ann", null);
            host.Connect(2, "bob", null);
            host.Input(2, B("hi"));
            Assert.That(o.List.TextFor(1), Is.EqualTo("bob says: hi"));
            Assert.That(o.List.TextFor(2), Is.Empty, "the one who said it does not get it");
        }

        [Test]
        public void ARoomJoin_RefusesSomethingThatIsNotASession()
        {
            (ScriptedHost host, Output o) = Make("function onConnect(s){ machine.room('x').join({ id: s.id }); }");
            host.Connect(1, "ann", null);
            Assert.That(o.List.LogLines()[0], Does.Contain("room.join: the argument must be a session"));
            Assert.That(o.List.ClearCount(1), Is.EqualTo(1), "the failing call is cleared alone");
        }

        [Test]
        public void TheStore_KeepsStrings_SharedBySessions_UpTo64K()
        {
            (ScriptedHost host, Output o) = Make(
                "function onInput(s, t){" +
                " if (t == 'w') machine.log('' + machine.store.set('top', 'ann 120'));" +
                " if (t == 'r') machine.log(machine.store.get('top') + ',' + machine.store.keys().length + ',' + typeof machine.store.get('none'));" +
                " if (t == 'big') machine.log('' + machine.store.set('big', new Array(65535).join('x')) + ',' + machine.store.remove('top') + ',' + machine.store.set('big', new Array(65530).join('x'))); }");
            host.Connect(1, "ann", null);
            host.Connect(2, "bob", null);
            host.Input(1, B("w"));
            host.Input(2, B("r"));
            host.Input(2, B("big"));
            Assert.That(o.List.LogLines(), Is.EqualTo(new List<string> { "true", "ann 120,1,undefined", "false,true,true" }));
        }

        [Test]
        public void TheScriptCannotReachDotNet()
        {
            (ScriptedHost host, Output o) = Make(
                "function onConnect(s){ machine.log([typeof importNamespace, typeof System, typeof require, typeof process," +
                " typeof fetch, typeof XMLHttpRequest, typeof host].join(',')); }");
            host.Connect(1, "guest", null);
            Assert.That(o.List.LogLines()[0], Is.EqualTo("undefined,undefined,undefined,undefined,undefined,undefined,undefined"));
        }

        [Test]
        public void TheMachineObjectGivesNoWayToItsDotNetType()
        {
            (ScriptedHost host, Output o) = Make(
                "function onConnect(s){ var r = []; try { r.push(String(s.send.constructor('return typeof importNamespace')())); }" +
                " catch (e) { r.push('threw'); } r.push(typeof machine.GetType); r.push(typeof s.GetType); machine.log(r.join(',')); }");
            host.Connect(1, "guest", null);
            Assert.That(o.List.LogLines()[0], Is.EqualTo("undefined,undefined,undefined"));
        }

        [Test]
        public void AScriptThatDoesNotLoad_Throws()
        {
            Assert.Throws<Jint.Runtime.JavaScriptException>(() => Make("throw new Error('broken host');"));
            Assert.That(() => Make("function ("), Throws.Exception);
        }

        [Test]
        public void AnEndlessLoop_ClearsOnlyThatCall()
        {
            (ScriptedHost host, Output o) = Make(
                "function onInput(s, t){ if (t === 'loop') { while (true) {} } s.send('ok ' + t); }",
                s => s.MaxStatements = 100_000);
            host.Connect(1, "guest", null);
            host.Connect(2, "guest", null);
            host.Input(1, B("loop"));
            host.Input(2, B("still here"));
            Assert.That(o.List.ClearCount(1), Is.EqualTo(1));
            Assert.That(o.List.ClearCount(2), Is.EqualTo(0));
            Assert.That(o.List.TextFor(2), Is.EqualTo("ok still here"));
            Assert.That(o.List.LogLines()[0], Does.StartWith("script error in onInput: "));
            Assert.That(host.SessionCount, Is.EqualTo(1));
        }

        [Test]
        public void TooMuchMemory_ClearsOnlyThatCall()
        {
            (ScriptedHost host, Output o) = Make(
                "var keep = []; function onInput(s, t){ for (var i = 0; i < 1e7; i++) { keep.push('xxxxxxxxxxxxxxxxxxxx' + i); } }",
                s =>
                {
                    s.MemoryLimitBytes = 4L * 1024 * 1024;
                    s.MaxStatements = 0;
                });
            host.Connect(1, "guest", null);
            host.Input(1, B("go"));
            Assert.That(o.List.ClearCount(1), Is.EqualTo(1));
            Assert.That(o.List.LogLines()[0], Does.StartWith("script error in onInput: "));
        }

        [Test]
        public void EndlessRecursion_ClearsOnlyThatCall()
        {
            (ScriptedHost host, Output o) = Make("function f(n){ return f(n + 1); } function onInput(s, t){ f(0); }");
            host.Connect(1, "guest", null);
            host.Input(1, B("go"));
            Assert.That(o.List.ClearCount(1), Is.EqualTo(1));
        }

        [Test]
        public void ARunawayTimer_IsLogged_AndTheHostGoesOn()
        {
            (ScriptedHost host, Output o) = Make(
                "function onStart(){ machine.after(10, function(){ while (true) {} }); }" +
                " function onConnect(s){ s.send('alive'); }",
                s => s.MaxStatements = 100_000);
            host.Tick(T0.AddSeconds(1));
            host.Connect(1, "guest", null);
            Assert.That(o.List.LogLines()[0], Does.StartWith("script error in timer: "));
            Assert.That(o.List.TextFor(1), Is.EqualTo("alive"));
        }

        [Test]
        public void Timers_RunInDueOrder_AtTheFirstTickAtOrAfterTheirTime_AndCanBeCancelled()
        {
            (ScriptedHost host, Output o) = Make(
                "function onStart(){ machine.after(300, function(){ machine.log('c'); });" +
                " machine.after(100, function(){ machine.log('a'); });" +
                " machine.after(100, function(){ machine.log('b'); });" +
                " var x = machine.after(200, function(){ machine.log('never'); }); machine.cancel(x);" +
                " machine.after(0, function(){ machine.log('zero'); machine.after(0, function(){ machine.log('next tick'); }); }); }");
            Assert.That(host.TimerCount, Is.EqualTo(4));
            host.Tick(T0.AddMilliseconds(99));
            Assert.That(o.List.LogLines(), Is.EqualTo(new List<string> { "zero" }));
            host.Tick(T0.AddMilliseconds(100));
            Assert.That(o.List.LogLines(), Is.EqualTo(new List<string> { "zero", "next tick", "a", "b" }));
            host.Tick(T0.AddMilliseconds(1000));
            Assert.That(o.List.LogLines(), Is.EqualTo(new List<string> { "zero", "next tick", "a", "b", "c" }));
            Assert.That(host.TimerCount, Is.EqualTo(0));
        }

        [Test]
        public void SessionsWho_AndBroadcast_SeeEveryCallOnTheMachine()
        {
            (ScriptedHost host, Output o) = Make(
                "function onConnect(s){ machine.broadcast('+' + s.login + '\\n', s.id); }" +
                " function onInput(s, t){ var all = machine.sessions(); var r = [];" +
                " for (var i = 0; i < all.length; i++) { r.push(all[i].login + '@' + all[i].calling); }" +
                " s.send(r.join(' ')); }");
            host.Connect(1, "ann", "111");
            host.Connect(2, "bob", "222");
            host.Connect(3, "cid", null);
            host.Disconnect(2);
            host.Input(1, B("who"));
            Assert.That(o.List.TextFor(1), Is.EqualTo("+bob\r\n+cid\r\nann@111 cid@null"));
            Assert.That(o.List.TextFor(2), Is.EqualTo("+cid\r\n"));
            Assert.That(o.List.TextFor(3), Is.EqualTo(""));
        }

        [Test]
        public void SendsToACallThatIsGone_AreDropped()
        {
            (ScriptedHost host, Output o) = Make(
                "var keep = null; function onConnect(s){ keep = s; } function onDisconnect(s){ s.send('too late');" +
                " machine.log(String(s.isOpen())); }");
            host.Connect(1, "guest", null);
            host.Disconnect(1);
            Assert.That(o.List.TextFor(1), Is.EqualTo(""));
            Assert.That(o.List.LogLines(), Is.EqualTo(new List<string> { "false" }));
            Assert.That(o.List.ClearCount(1), Is.EqualTo(0));
            Assert.That(host.SessionCount, Is.EqualTo(0));
        }

        [Test]
        public void ClearFromTheScript_ClearsTheCallOnce()
        {
            (ScriptedHost host, Output o) = Make("function onInput(s, t){ s.send('bye'); s.clear(); s.clear(); s.send('x'); }");
            host.Connect(1, "guest", null);
            host.Input(1, B(".x"));
            host.Input(1, B("ignored"));
            Assert.That(o.List.TextFor(1), Is.EqualTo("bye"));
            Assert.That(o.List.ClearCount(1), Is.EqualTo(1));
        }

        [Test]
        public void InputBytesReachTheScriptOneCharacterPerByte()
        {
            (ScriptedHost host, Output o) = Make(
                "function onInput(s, t){ var r = []; for (var i = 0; i < t.length; i++) { r.push(t.charCodeAt(i)); } machine.log(r.join(',')); }");
            host.Connect(1, "guest", null);
            host.Input(1, new byte[] { 0, 13, 127, 128, 255 });
            Assert.That(o.List.LogLines()[0], Is.EqualTo("0,13,127,128,255"));
        }

        [Test]
        public void SettingsAndClockReachTheScript()
        {
            (ScriptedHost host, Output o) = Make(
                "function onStart(){ var t = machine.now(); machine.log(machine.settings.Name + ' ' + t.year + '-' + t.month + '-' + t.day +" +
                " ' ' + t.weekday + ' ' + t.hour + ':' + t.minute); }",
                s => s.Values["Name"] = "TOMVAX");
            Assert.That(o.List.LogLines()[0], Is.EqualTo("TOMVAX 1987-5-29 5 12:0"));
        }

        [Test]
        public void ConnectingTheSameSessionTwice_Throws()
        {
            (ScriptedHost host, _) = Make("");
            host.Connect(1, "guest", null);
            Assert.Throws<InvalidOperationException>(() => host.Connect(1, "guest", null));
        }
    }
}
