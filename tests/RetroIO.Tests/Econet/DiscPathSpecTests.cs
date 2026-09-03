//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using NUnit.Framework;
using RetroIO.Protocols.Econet.Common;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Locks the ':'-prefixed (disc-qualified) Acorn path rules against PiFS ground truth
/// (E:\Dev\Emulators\6502\PiEconetBridge\utilities\fs.c, fs_normalize_path_wildcard:2377-2406).
/// </summary>
/// <remarks>
/// REGRESSION GUARD. Found live in BeebEm on 2026-07-17: "*DIR :" was accepted and set the CSD to
/// the raw string ":". That resolved to the disc CONTAINER directory — above the logged-in user's
/// root — which (a) leaked the host folder name "0RetroFS" to the BBC as an Acorn directory name,
/// (b) made every relative lookup in the user's home 404, and (c) TRAPPED the session, because the
/// "$"-clamp in OsCliCommandHandler.ParentAcorn only fires on paths containing '$' and ":" has none,
/// so "*DIR ^" could not climb back out.
///
/// The reference rejects a bare ":" because a disc name must be 1-16 characters long (fs.c:3409) —
/// a zero-length name cannot match, so path normalisation fails with FS_PATH_ERR_NODISC (fs.c:2399).
/// The *DIR handler then errors and leaves the CSD untouched (fs.c:10365, 10419).
/// Do not "fix" this back to accepting ":".
/// </remarks>
[TestFixture]
public sealed class DiscPathSpecTests
{
    // ---- Rejections: PiFS FS_PATH_ERR_NODISC / FS_PATH_ERR_FORMAT ----

    [Test]
    public void BareColon_IsRejected_BecauseDiscNameRegexRequiresAtLeastOneChar()
    {
        // fs.c:2379 - a zero-length disc name cannot match -> fs.c:2399 FS_PATH_ERR_NODISC.
        // This is THE bug: ":" used to resolve to the disc container and strand the session.
        Assert.That(AcornPath.TryParseDiscPath(":", out _, out _), Is.False);
    }

    [Test]
    public void ColonDot_IsRejected_ZeroLengthDiscName()
    {
        Assert.That(AcornPath.TryParseDiscPath(":.", out _, out _), Is.False);
    }

    [Test]
    public void ColonDotSomething_IsRejected_ZeroLengthDiscName()
    {
        Assert.That(AcornPath.TryParseDiscPath(":.USER", out _, out _), Is.False);
    }

    [Test]
    public void DiscNameLongerThanSixteen_IsRejected()
    {
        // PiFS regex is {1,16}; 17 characters cannot be a disc name.
        string tooLong = ":" + new string('A', 17);
        Assert.That(AcornPath.TryParseDiscPath(tooLong, out _, out _), Is.False);
    }

    [Test]
    public void DiscNameOfExactlySixteen_IsAccepted_BoundaryOfFsRegex()
    {
        string atLimit = ":" + new string('A', 16);
        Assert.That(AcornPath.TryParseDiscPath(atLimit, out string discName, out string rooted), Is.True);
        Assert.That(discName, Is.EqualTo(new string('A', 16)));
        Assert.That(rooted, Is.EqualTo("$"));
    }

    [Test]
    public void TrailingDotWithNoRemainder_IsRejected()
    {
        Assert.That(AcornPath.TryParseDiscPath(":RetroFS.", out _, out _), Is.False);
    }

    [Test]
    public void PathNotStartingWithColon_IsRejected()
    {
        Assert.That(AcornPath.TryParseDiscPath("$.USER", out _, out _), Is.False);
    }

    // ---- Acceptances: PiFS normalisation of a valid disc spec ----

    [Test]
    public void DiscNameAlone_BecomesDiscRoot()
    {
        // fs.c:2387 - a ':' plus a disc name and nothing else resolves to the disc root "$".
        Assert.That(AcornPath.TryParseDiscPath(":RetroFS", out string discName, out string rooted), Is.True);
        Assert.That(discName, Is.EqualTo("RetroFS"));
        Assert.That(rooted, Is.EqualTo("$"));
    }

    [Test]
    public void DiscNameWithTail_GetsDollarInserted()
    {
        // fs.c:2392 - no '$' after the disc name, so PiFS prepends "$.".
        Assert.That(AcornPath.TryParseDiscPath(":RetroFS.USER", out string discName, out string rooted), Is.True);
        Assert.That(discName, Is.EqualTo("RetroFS"));
        Assert.That(rooted, Is.EqualTo("$.USER"));
    }

    [Test]
    public void DiscNameWithExplicitDollar_IsPreserved()
    {
        // fs.c:2384-2386 - an explicit '$' after the disc name is kept verbatim.
        Assert.That(AcornPath.TryParseDiscPath(":RetroFS.$.USER", out string discName, out string rooted), Is.True);
        Assert.That(discName, Is.EqualTo("RetroFS"));
        Assert.That(rooted, Is.EqualTo("$.USER"));
    }

    [Test]
    public void DiscNameWithExplicitDollarRoot_IsPreserved()
    {
        Assert.That(AcornPath.TryParseDiscPath(":RetroFS.$", out string discName, out string rooted), Is.True);
        Assert.That(discName, Is.EqualTo("RetroFS"));
        Assert.That(rooted, Is.EqualTo("$"));
    }

    [Test]
    public void DeepTail_IsRootedOnce()
    {
        Assert.That(AcornPath.TryParseDiscPath(":RetroFS.USER.SUB.DEEP", out _, out string rooted), Is.True);
        Assert.That(rooted, Is.EqualTo("$.USER.SUB.DEEP"));
    }

    // ---- The trap: every accepted form must carry '$' so ParentAcorn can clamp ----

    [TestCase(":RetroFS")]
    [TestCase(":RetroFS.USER")]
    [TestCase(":RetroFS.$.USER")]
    public void EveryAcceptedDiscPath_YieldsADollarRootedRemainder(string input)
    {
        // The trap was a CSD with no '$' in it: ParentAcorn returns the path unchanged when the
        // parent contains no '$', so "*DIR ^" became a no-op and the session could not recover.
        Assert.That(AcornPath.TryParseDiscPath(input, out _, out string rooted), Is.True);
        Assert.That(rooted, Does.Contain("$"));
    }
}
