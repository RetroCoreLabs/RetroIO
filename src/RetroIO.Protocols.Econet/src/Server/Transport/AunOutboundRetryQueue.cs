//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Protocols.Econet.Server.Transport;

/// <summary>
/// Retries outbound AUN packets when the transport send fails transiently.
/// </summary>
public sealed class AunOutboundRetryQueue
{
    private readonly object _sync = new();
    private readonly List<PendingSend> _pending = new();
    private const int MaxAttempts = 3;

    public void Enqueue(EconetMessage message)
    {
        lock (_sync)
            _pending.Add(new PendingSend(message, 0));
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        List<PendingSend> snapshot;
        lock (_sync)
        {
            snapshot = new List<PendingSend>(_pending);
            _pending.Clear();
        }

        for (int i = 0; i < snapshot.Count; i++)
        {
            PendingSend item = snapshot[i];
            if (item.Message.Transport == null)
                continue;

            try
            {
                await item.Message.Transport.SendAsync(item.Message);
            }
            catch
            {
                if (item.Attempts + 1 < MaxAttempts)
                {
                    lock (_sync)
                        _pending.Add(new PendingSend(item.Message, item.Attempts + 1));
                }
            }
        }
    }

    private readonly struct PendingSend(EconetMessage message, int attempts)
    {
        public EconetMessage Message { get; } = message;
        public int Attempts { get; } = attempts;
    }
}
