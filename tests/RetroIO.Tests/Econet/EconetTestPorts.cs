//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Tests.Econet;

/// <summary>
/// UDP ports reserved for Econet unit tests (not production 32768).
/// </summary>
internal static class EconetTestPorts
{
    public const int UnitTestServerUdp = 32666;
    public const int UnitTestClientUdp = 32667;
}
