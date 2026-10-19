//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using RetroIO.Protocols.X25.Protocols;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Tests for the X28 CharacterProcessed event, and for the way the password mode parameters
    /// (echo 0, forwarding 127, idle timer 1, editing 0) make the PAD forward every character.
    /// </summary>
    [TestFixture]
    public class X28CharacterEventTests
    {
        private static X28 NewDataTransferPad(out X3 x3)
        {
            x3 = new X3();
            x3.SetParameter(15, 0); // no editing, so DEL / CAN / DC2 are plain data
            var x28 = new X28(x3);
            x28.SwitchToDataTransferMode();
            return x28;
        }

        /// <summary>
        /// A character processed in command mode raises CharacterProcessed with that character.
        /// </summary>
        [Test]
        public void CharacterProcessed_IsRaised_InCommandMode()
        {
            var x28 = new X28(new X3());
            var seen = new List<char>();
            x28.CharacterProcessed += (s, e) => seen.Add(e.Character);

            Assert.That(x28.ProcessCharacter('C'), Is.True);
            Assert.That(x28.ProcessCharacter('L'), Is.True);

            Assert.That(seen.Count, Is.EqualTo(2));
            Assert.That(seen[0], Is.EqualTo('C'));
            Assert.That(seen[1], Is.EqualTo('L'));
        }

        /// <summary>
        /// A character processed in data transfer mode raises CharacterProcessed, once per character,
        /// and the event comes with no response text (the PAD has none for plain data).
        /// </summary>
        [Test]
        public void CharacterProcessed_IsRaised_InDataTransferMode_WithNoResponse()
        {
            var x28 = NewDataTransferPad(out _);
            var args = new List<X28CharacterEventArgs>();
            x28.CharacterProcessed += (s, e) => args.Add(e);

            x28.ProcessCharacter('x');
            x28.ProcessCharacter('y');

            Assert.That(args.Count, Is.EqualTo(2));
            Assert.That(args[0].Character, Is.EqualTo('x'));
            Assert.That(args[1].Character, Is.EqualTo('y'));
            Assert.That(args[0].Response, Is.Null);
        }

        /// <summary>
        /// A character the PAD did not process (ProcessCharacter returns false) raises nothing.
        /// A backspace on an empty command line is such a character.
        /// </summary>
        [Test]
        public void CharacterProcessed_IsNotRaised_WhenTheCharacterIsNotProcessed()
        {
            var x28 = new X28(new X3());
            int count = 0;
            x28.CharacterProcessed += (s, e) => count++;

            Assert.That(x28.ProcessCharacter('\b'), Is.False);

            Assert.That(count, Is.EqualTo(0));
        }

        /// <summary>
        /// The event comes after the work of the character is done: data forwarded on this character
        /// is already out when CharacterProcessed is raised.
        /// </summary>
        [Test]
        public void CharacterProcessed_ComesAfterForwarding()
        {
            var x28 = NewDataTransferPad(out _);
            var order = new List<string>();
            x28.DataForwarded += (s, e) => order.Add("forward");
            x28.CharacterProcessed += (s, e) => order.Add("processed");

            x28.ProcessCharacter('\r'); // CR is a data forwarding character in the default (126)

            Assert.That(order, Is.EqualTo(new[] { "forward", "processed" }));
        }

        /// <summary>
        /// Password mode parameters: alphanumerics forward at once (flag 1 of parameter 3).
        /// </summary>
        [Test]
        public void PasswordParameters_ForwardAlphanumeric_AtOnce()
        {
            var x28 = NewDataTransferPad(out var x3);
            x3.SetParameter(2, 0);
            x3.SetParameter(3, 127);
            x3.SetParameter(4, 1);
            var forwarded = new List<string>();
            x28.DataForwarded += (s, e) => forwarded.Add(e.Data);

            x28.ProcessCharacter('a');

            Assert.That(forwarded.Count, Is.EqualTo(1));
            Assert.That(forwarded[0], Is.EqualTo("a"));
        }

        /// <summary>
        /// Password mode parameters: a symbol is in no flag of parameter 3, so the idle timer of one
        /// twentieth of a second (parameter 4 = 1) forwards it.
        /// </summary>
        [Test]
        public void PasswordParameters_ForwardSymbol_OnIdleTimer()
        {
            var x28 = NewDataTransferPad(out var x3);
            x3.SetParameter(2, 0);
            x3.SetParameter(3, 127);
            x3.SetParameter(4, 1);
            var forwarded = new List<string>();
            x28.DataForwarded += (s, e) => { lock (forwarded) { forwarded.Add(e.Data); } };

            x28.ProcessCharacter('!');

            bool got = false;
            for (int i = 0; i < 40 && !got; i++)
            {
                Thread.Sleep(25);
                lock (forwarded) { got = forwarded.Count > 0; }
            }
            Assert.That(got, Is.True, "the symbol should be forwarded by the idle timer");
            Assert.That(forwarded[0], Is.EqualTo("!"));
            x28.Dispose();
        }
    }
}
