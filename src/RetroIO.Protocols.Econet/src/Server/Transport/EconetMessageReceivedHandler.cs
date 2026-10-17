//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Econet.Server.Transport;

/// <summary>
/// The method type that is called when a transport receives a message.
/// </summary>
/// <param name="message">
/// The received message.
/// </param>
/// <returns>
/// A task that completes when the message has been handled.
/// </returns>
public delegate ValueTask EconetMessageReceivedHandler(EconetMessage message);
