//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

// SPDX-License-Identifier: MIT

// Copyright (c) 1985-2026 Ronny Hansen

// RetroCore Labs — https://github.com/RetroCoreLabs

// RetroFS -- Accessing yesterday's data with today's standards"

//



using System;
using NUnit.Framework;
using RetroIO.Identity;
using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Users;



namespace RetroIO.Tests.Econet;



[TestFixture]

[Category("Unit")]

public sealed class EconetUserDirectoryTests

{

    [Test]

    public void TryAuthenticate_WithEmptyPasswordUser_Succeeds()

    {

        EconetUserDirectory directory = EconetTestUsers.CreateDirectory(out _);

        ProtocolAuthResult result = directory.TryAuthenticate(

            "SYST", AcornPath.PadPassword(string.Empty), out RetroUser? user, out EconetUserProfile? profile);



        Assert.That(result, Is.EqualTo(ProtocolAuthResult.Success));

        Assert.That(user, Is.Not.Null);

        Assert.That(profile, Is.Not.Null);

        Assert.That(profile!.Username.TrimEnd(), Is.EqualTo("SYST"));

        Assert.That(user!.LastLoginEconetAt, Is.Not.Null);

    }



    [Test]

    public void TryAuthenticate_Username_IsCaseInsensitiveForRetroIdpUsername()

    {

        EconetUserDirectory directory = EconetTestUsers.CreateDirectory(out _);

        ProtocolAuthResult result = directory.TryAuthenticate("syst", AcornPath.PadPassword(string.Empty), out _, out _);

        Assert.That(result, Is.EqualTo(ProtocolAuthResult.Success));

    }



    [Test]

    public void TryAuthenticate_Password_IsCaseSensitive()

    {

        EconetUserDirectory directory = EconetTestUsers.CreateDirectory(out IRetroIDP idp);



        RetroUser regular = new RetroUser

        {

            UserId = Guid.NewGuid(),

            Username = "alice",

            DisplayName = "Alice",

            IsActive = true,

            AllowEconet = true,

        };

        idp.CreateUserAsync(regular).AsTask().GetAwaiter().GetResult();

        idp.SetPasswordAsync(regular.UserId, "secret").AsTask().GetAwaiter().GetResult();



        Assert.That(directory.TryAuthenticate("alice", AcornPath.PadPassword("secret"), out _, out _), Is.EqualTo(ProtocolAuthResult.Success));

        Assert.That(directory.TryAuthenticate("alice", AcornPath.PadPassword("SECRET"), out _, out _), Is.EqualTo(ProtocolAuthResult.WrongPassword));

        Assert.That(directory.TryAuthenticate("ALICE", AcornPath.PadPassword("secret"), out _, out _), Is.EqualTo(ProtocolAuthResult.Success));

    }



    [Test]

    public void TryAuthenticate_UsernameWithMixedCase_MatchesRetroIdpUsername()

    {

        EconetUserDirectory directory = EconetTestUsers.CreateDirectory(out IRetroIDP idp);



        RetroUser user = new RetroUser

        {

            UserId = Guid.NewGuid(),

            Username = "BACKUP",

            DisplayName = "Backup operator",

            IsActive = true,

            AllowEconet = true,

        };

        user.Metadata[EconetMetadataKeys.AllowEmptyPassword] = "true";

        idp.CreateUserAsync(user).AsTask().GetAwaiter().GetResult();



        Assert.That(directory.TryAuthenticate("BACKUP", AcornPath.PadPassword(string.Empty), out _, out _), Is.EqualTo(ProtocolAuthResult.Success));

        Assert.That(directory.TryAuthenticate("backup", AcornPath.PadPassword(string.Empty), out _, out _), Is.EqualTo(ProtocolAuthResult.Success));

    }



    [Test]

    public void TryAuthenticate_WithWrongPassword_Fails()

    {

        EconetUserDirectory directory = EconetTestUsers.CreateDirectory(out IRetroIDP idp);



        RetroUser regular = new RetroUser

        {

            UserId = Guid.NewGuid(),

            Username = "alice",

            DisplayName = "Alice",

            IsActive = true,

            AllowEconet = true,

        };

        idp.CreateUserAsync(regular).AsTask().GetAwaiter().GetResult();

        idp.SetPasswordAsync(regular.UserId, "secret").AsTask().GetAwaiter().GetResult();



        ProtocolAuthResult result = directory.TryAuthenticate("alice", AcornPath.PadPassword("wrong"), out RetroUser? user, out _);

        Assert.That(result, Is.EqualTo(ProtocolAuthResult.WrongPassword));

        Assert.That(user!.LastFailedLoginAt, Is.Not.Null);

    }



    [Test]

    public void TryAuthenticate_SystWithWrongPasswordWhenEmptyAllowed_Fails()

    {

        EconetUserDirectory directory = EconetTestUsers.CreateDirectory(out _);



        ProtocolAuthResult result = directory.TryAuthenticate(

            "SYST",

            AcornPath.PadPassword("WRONGPASSWORD"),

            out _,

            out _);



        Assert.That(result, Is.EqualTo(ProtocolAuthResult.WrongPassword));

    }



    [Test]

    public void TryAuthenticate_SystWithPasswordHash_RejectsWrongAcceptsMyPassword()

    {

        EconetUserDirectory directory = EconetTestUsers.CreateDirectory(out IRetroIDP idp);

        RetroUser? syst = idp.GetUserByUsernameAsync("SYST").AsTask().GetAwaiter().GetResult();

        Assert.That(syst, Is.Not.Null);

        idp.SetPasswordAsync(syst!.UserId, "MYPASSWORD").AsTask().GetAwaiter().GetResult();



        Assert.That(

            directory.TryAuthenticate("SYST", AcornPath.PadPassword("WRONGPASSWORD"), out _, out _),

            Is.EqualTo(ProtocolAuthResult.WrongPassword));

        Assert.That(

            directory.TryAuthenticate("SYST", AcornPath.PadPassword("MYPASSWORD"), out _, out _),

            Is.EqualTo(ProtocolAuthResult.Success));

    }



    [Test]

    public void TryAuthenticate_WhenEconetDisabled_ReturnsProtocolNotAllowed()

    {

        EconetUserDirectory directory = EconetTestUsers.CreateDirectory(out IRetroIDP idp);



        RetroUser regular = new RetroUser

        {

            UserId = Guid.NewGuid(),

            Username = "bob",

            DisplayName = "Bob",

            IsActive = true,

            AllowEconet = false,

        };

        regular.Metadata[EconetMetadataKeys.AllowEmptyPassword] = "true";

        idp.CreateUserAsync(regular).AsTask().GetAwaiter().GetResult();



        ProtocolAuthResult result = directory.TryAuthenticate("bob", AcornPath.PadPassword(string.Empty), out RetroUser? user, out _);

        Assert.That(result, Is.EqualTo(ProtocolAuthResult.ProtocolNotAllowed));

        Assert.That(user!.LastFailedLoginAt, Is.Not.Null);

    }

}


