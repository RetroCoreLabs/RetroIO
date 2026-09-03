//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

// SPDX-License-Identifier: MIT

// Copyright (c) 1985-2026 Ronny Hansen

// HackerCorp Labs — https://github.com/HackerCorpLabs

// RetroFS -- Accessing yesterday's data with today's standards"

//



using System;

using System.IO;

using RetroIO.Protocols.Econet.FileServer.Users;

using RetroIO.Identity;



namespace RetroIO.Tests.Econet;



internal static class EconetTestUsers

{

    public static EconetUserDirectory CreateDirectory(out IRetroIDP idp)

    {

        string directory = Path.Combine(Path.GetTempPath(), "retrofs-econet-tests", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);



        idp = new JsonRetroIDP(

            Path.Combine(directory, "users.json"),

            Path.Combine(directory, "groups.json"));



        RetroUser syst = new RetroUser

        {

            UserId = Guid.NewGuid(),

            Username = "SYST",

            DisplayName = "System Manager",

            IsActive = true,

            IsAdmin = true,

            AllowEconet = true,

            AllowTnfs = false,

        };

        syst.Metadata[EconetMetadataKeys.AllowEmptyPassword] = "true";

        syst.Metadata[EconetMetadataKeys.DiscMask] = "65535";



        idp.CreateUserAsync(syst).AsTask().GetAwaiter().GetResult();

        RetroUser user = new RetroUser
        {
            UserId = Guid.NewGuid(),
            Username = "USER",
            DisplayName = "Normal User",
            IsActive = true,
            IsAdmin = false,
            AllowEconet = true,
            AllowTnfs = false,
        };
        user.Metadata[EconetMetadataKeys.AllowEmptyPassword] = "true";
        user.Metadata[EconetMetadataKeys.DiscMask] = "65535";
        idp.CreateUserAsync(user).AsTask().GetAwaiter().GetResult();

        idp.SaveAsync().AsTask().GetAwaiter().GetResult();



        return new EconetUserDirectory(idp);

    }

}


