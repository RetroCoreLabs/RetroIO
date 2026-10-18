//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using RetroIO.Identity;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet;

/// <summary>
/// When the user store refuses a write (the user was removed after the command found it), the
/// commands that write to it must reply an error and not success. The codes and texts follow PiFS
/// (fs.c) for the same failure: PRIV "User not found" 0xBC, DISCMASK "Unknown user" 0xFF,
/// SETHOME and SETLIB "No such user" 0xFF, and the boot option "Bad user ID" 0xBD.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class UserStoreFailureTests
{
    /// <summary>
    /// Wraps the identity provider. While <see cref="Vanish"/> is set, looking a user up by id finds
    /// nothing, so every user store write fails, while a look-up by name still works.
    /// </summary>
    public class VanishingIdp : DispatchProxy
    {
        public IRetroIDP Target { get; set; } = null!;

        public bool Vanish { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (Vanish && targetMethod!.Name == "GetUserAsync")
                return new ValueTask<RetroUser?>((RetroUser?)null);

            return targetMethod!.Invoke(Target, args);
        }
    }

    private VanishingIdp _idp = null!;
    private EconetFsTestHarness _harness = null!;

    [SetUp]
    public void SetUp()
    {
        VanishingIdp? captured = null;
        _harness = new EconetFsTestHarness(real =>
        {
            IRetroIDP proxy = DispatchProxy.Create<IRetroIDP, VanishingIdp>();
            captured = (VanishingIdp)(object)proxy;
            captured.Target = real;
            return proxy;
        });
        _idp = captured!;
    }

    private static byte Status(EconetMessage reply) => reply.Payload[1];

    [Test]
    public void Priv_WhenStoreFails_RepliesUserNotFound()
    {
        _harness.LoginAsSyst();
        _idp.Vanish = true;

        Assert.That(Status(_harness.HandleOsCli("PRIV USER S\r")), Is.EqualTo((byte)0xBC));
    }

    [Test]
    public void DiscMask_WhenStoreFails_RepliesUnknownUser()
    {
        _harness.LoginAsSyst();
        _idp.Vanish = true;

        Assert.That(Status(_harness.HandleOsCli("DISCMASK USER NONE\r")), Is.EqualTo(FileServerErrors.Unsupported));
    }

    [Test]
    public void SetHome_Clear_WhenStoreFails_RepliesError()
    {
        _harness.LoginAsSyst();
        _idp.Vanish = true;

        Assert.That(Status(_harness.HandleOsCli("SETHOME %\r")), Is.Not.EqualTo(0));
    }

    [Test]
    public void SetHome_Directory_WhenStoreFails_RepliesError()
    {
        _harness.LoginAsSyst();
        _idp.Vanish = true;

        Assert.That(Status(_harness.HandleOsCli("SETHOME $\r")), Is.Not.EqualTo(0));
    }

    [Test]
    public void SetLib_Clear_WhenStoreFails_RepliesError()
    {
        _harness.LoginAsSyst();
        _idp.Vanish = true;

        Assert.That(Status(_harness.HandleOsCli("SETLIB %\r")), Is.Not.EqualTo(0));
    }

    [Test]
    public void SetLib_Directory_WhenStoreFails_RepliesError()
    {
        _harness.LoginAsSyst();
        _idp.Vanish = true;

        Assert.That(Status(_harness.HandleOsCli("SETLIB $\r")), Is.Not.EqualTo(0));
    }

    [Test]
    public void SetBootOpt_WhenStoreFails_RepliesBadUserId()
    {
        _harness.LoginAsUser();
        _idp.Vanish = true;

        EconetMessage reply = _harness.Send(FileServerOpcodes.SetBootOpt, [3]);

        Assert.That(Status(reply), Is.EqualTo(FileServerErrors.InsufficientAccess));
    }

    [Test]
    public void SetBootOpt_WhenStoreWorks_RepliesSuccess()
    {
        _harness.LoginAsUser();

        EconetMessage reply = _harness.Send(FileServerOpcodes.SetBootOpt, [3]);

        Assert.That(Status(reply), Is.EqualTo(0));
    }
}
