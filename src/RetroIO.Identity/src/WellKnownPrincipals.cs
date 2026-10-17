//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Identity
{
    /// <summary>
    /// Well-known system principals
    /// </summary>
    public static class WellKnownPrincipals
    {
        /// <summary>
        /// UNKNOWN - Unauthenticated user (Guid.Empty)
        /// Used for: Anonymous access, guest browsing
        /// </summary>
        public static readonly Guid UnknownUserId = Guid.Empty;
        public const string UnknownUsername = "UNKNOWN";

        /// <summary>
        /// SYSTEM - Administrative user
        /// Used for: Internal operations, system tasks
        /// </summary>
        public static readonly Guid SystemUserId =
            new Guid("5757300D-0000-0000-0000-000000000001"); // SYSTEM00
        public const string SystemUsername = "SYSTEM";

        /// <summary>
        /// ADMIN - Default administrator account
        /// Used for: Initial server administration
        /// </summary>
        public static readonly Guid AdminUserId = new Guid("C001D00D-0000-0000-0000-000000000000"); // COOL DUDE


        public const string AdminUsername = "admin";

        /// <summary>
        /// SYST - Acorn Econet file-server manager account
        /// </summary>
        public static readonly Guid SystUserId =
            new Guid("53595354-0000-0000-0000-000000000001"); // SYST0001
        public const string SystUsername = "SYST";

        /// <summary>
        /// Create UNKNOWN principal (Guid.Empty)
        /// </summary>
        public static RetroUser CreateUnknownPrincipal()
        {
            return new RetroUser
            {
                UserId = UnknownUserId,
                Username = UnknownUsername,
                DisplayName = "Anonymous User",
                IsActive = true,
                IsAdmin = false,
                CreatedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Create SYSTEM principal
        /// </summary>
        public static RetroUser CreateSystemPrincipal()
        {
            return new RetroUser
            {
                UserId = SystemUserId,
                Username = SystemUsername,
                DisplayName = "System Account",
                IsActive = true,
                IsAdmin = true,
                CreatedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Check if user is UNKNOWN
        /// </summary>
        public static bool IsUnknownUser(RetroUser? user)
        {
            return user == null || user.UserId == UnknownUserId;
        }

        /// <summary>
        /// Check if user is SYSTEM
        /// </summary>
        public static bool IsSystemUser(RetroUser? user)
        {
            return user != null && user.UserId == SystemUserId;
        }

        /// <summary>
        /// Create ADMIN principal
        /// </summary>
        public static RetroUser CreateAdminPrincipal()
        {
            return new RetroUser
            {
                UserId = AdminUserId,
                Username = AdminUsername,
                DisplayName = "Administrator",
                Email = "admin@retrofs.local",
                IsActive = true,
                IsAdmin = true,
                AllowTnfs = true,
                AllowEconet = true,
                CreatedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Create SYST principal for Acorn Econet file-server logins.
        /// </summary>
        public static RetroUser CreateSystPrincipal()
        {
            return new RetroUser
            {
                UserId = SystUserId,
                Username = SystUsername,
                DisplayName = "System Manager",
                IsActive = true,
                IsAdmin = true,
                AllowTnfs = false,
                AllowEconet = true,
                CreatedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Check if user is ADMIN
        /// </summary>
        public static bool IsAdminUser(RetroUser? user)
        {
            return user != null && user.UserId == AdminUserId;
        }
    }
}
