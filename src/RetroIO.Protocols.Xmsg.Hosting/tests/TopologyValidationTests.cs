//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Hosting.Tests
{
    /// <summary>
    /// <see cref="TopologyConfig.Parse"/> and the checks added to <see cref="TopologyConfig.Validate"/>
    /// when the topology moved out of the runner.
    /// </summary>
    /// <remarks>
    /// Before 2026-11-09 a duplicate id, a via chain naming an unknown node, or a self that was not
    /// listed was accepted and routed wrongly. Each of these tests is one of those mistakes.
    /// </remarks>
    public sealed class TopologyValidationTests
    {
        [Fact]
        public void AWellFormedTopologyParses_WithCommentsAndTrailingCommas()
        {
            const string Json = @"{
                // our node
                ""self"": 19999,
                ""nodes"": [
                    { ""id"": 19999, ""alias"": ""d19999"", ""reach"": ""local"" },
                    { ""id"": 100, ""alias"": ""d100"", ""reach"": ""neighbour"", ""link"": 1 },
                    { ""id"": 103, ""alias"": ""d103"", ""reach"": ""via"", ""via"": [ 100 ] },
                ],
            }";

            TopologyConfig config = TopologyConfig.Parse(Json, "test");

            Assert.Equal(19999, config.Self);
            Assert.Equal(3, config.Nodes.Count);
            Assert.NotNull(config.Find("d103"));
        }

        [Fact]
        public void ASelfOfZeroNeedNotBeListed()
        {
            const string Json = @"{ ""nodes"": [ { ""id"": 100, ""reach"": ""neighbour"" } ] }";

            TopologyConfig config = TopologyConfig.Parse(Json, "test");

            Assert.Equal(0, config.Self);
        }

        [Fact]
        public void ATwiceListedIdIsRefused()
        {
            const string Json = @"{ ""nodes"": [
                { ""id"": 100, ""reach"": ""neighbour"" },
                { ""id"": 100, ""reach"": ""neighbour"" } ] }";

            FormatException error = Assert.Throws<FormatException>(() => TopologyConfig.Parse(Json, "test"));
            Assert.Contains("listed twice", error.Message);
        }

        [Fact]
        public void ASharedAliasIsRefused_WhateverItsCase()
        {
            const string Json = @"{ ""nodes"": [
                { ""id"": 100, ""alias"": ""d100"", ""reach"": ""neighbour"" },
                { ""id"": 102, ""alias"": ""D100"", ""reach"": ""neighbour"" } ] }";

            FormatException error = Assert.Throws<FormatException>(() => TopologyConfig.Parse(Json, "test"));
            Assert.Contains("d100", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void AViaChainNamingAnUnknownNodeIsRefused()
        {
            const string Json = @"{ ""nodes"": [
                { ""id"": 100, ""reach"": ""neighbour"" },
                { ""id"": 103, ""reach"": ""via"", ""via"": [ 101 ] } ] }";

            FormatException error = Assert.Throws<FormatException>(() => TopologyConfig.Parse(Json, "test"));
            Assert.Contains("101", error.Message);
        }

        [Fact]
        public void AnUnknownTransportIsRefusedAtLoad()
        {
            const string Json = @"{ ""nodes"": [ { ""id"": 100, ""reach"": ""neighbour"", ""transport"": ""carrier-pigeon"" } ] }";

            Assert.Throws<FormatException>(() => TopologyConfig.Parse(Json, "test"));
        }

        [Fact]
        public void ASelfThatIsNotListedIsRefused()
        {
            const string Json = @"{ ""self"": 19999, ""nodes"": [ { ""id"": 100, ""reach"": ""neighbour"" } ] }";

            FormatException error = Assert.Throws<FormatException>(() => TopologyConfig.Parse(Json, "test"));
            Assert.Contains("19999", error.Message);
        }

        [Fact]
        public void NoNodesIsRefused()
        {
            Assert.Throws<FormatException>(() => TopologyConfig.Parse(@"{ ""nodes"": [] }", "test"));
        }
    }
}
