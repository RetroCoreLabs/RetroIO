//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Storage.Ndfs.Creation
{
    /// <summary>
    /// Information for creating a user in a new NDFS image.
    /// </summary>
    public class NdfsUserCreationInfo
    {
        /// <summary>
        /// User name (max 16 characters).
        /// </summary>
        public string UserName { get; set; } = string.Empty;

        /// <summary>
        /// User password (optional, max 8 characters).
        /// If null or empty, no password is set.
        /// </summary>
        public string? Password { get; set; }

        /// <summary>
        /// User's page quota (0 = no quota/unlimited).
        /// </summary>
        public uint PageQuota { get; set; }

        /// <summary>
        /// User ID (1-255, 0 reserved for SYSTEM).
        /// </summary>
        public byte UserId { get; set; }
    }
}
