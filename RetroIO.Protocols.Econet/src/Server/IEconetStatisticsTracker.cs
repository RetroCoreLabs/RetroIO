//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Protocols.Econet.Server;

public interface IEconetStatisticsTracker
{
    void RecordRequest(int bytesTransferred, bool isFileOperation);
}
