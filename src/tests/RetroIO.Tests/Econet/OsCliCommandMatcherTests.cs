//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using NUnit.Framework;
using RetroIO.Protocols.Econet.FileServer.Dispatch;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Specifies <see cref="OsCliCommandMatcher"/> — the single expression of Acorn's OSCLI dot
/// abbreviation rule.
/// </summary>
/// <remarks>
/// Every registered command's abbreviation behaviour reduces to this matcher, so these tests are
/// the contract for all of them. The scattered <c>normalized.StartsWith(...)</c> arms this replaces
/// got the question backwards — they asked "does the typed text start with the command name?"
/// instead of "is the typed word a prefix OF the command name?" — which both rejected the
/// abbreviations users actually type (<c>*CA.</c>, <c>*I.</c>) and accepted things it shouldn't
/// (<c>*EXEC</c> matching an <c>EX</c> arm).
/// </remarks>
[TestFixture]
[Category("Unit")]
public sealed class OsCliCommandMatcherTests
{
    // ---- Full, unabbreviated matches ----

    [Test]
    public void FullName_Matches_WithNoArgument()
    {
        Assert.That(OsCliCommandMatcher.TryMatch("CAT", "CAT", 2, out string arg), Is.True);
        Assert.That(arg, Is.Empty);
    }

    [Test]
    public void FullName_IsCaseInsensitive()
    {
        Assert.That(OsCliCommandMatcher.TryMatch("cat", "CAT", 2, out _), Is.True);
        Assert.That(OsCliCommandMatcher.TryMatch("CaT", "CAT", 2, out _), Is.True);
    }

    [Test]
    public void FullName_WithArgument_SplitsArgument()
    {
        Assert.That(OsCliCommandMatcher.TryMatch("DIR GAMES", "DIR", 3, out string arg), Is.True);
        Assert.That(arg, Is.EqualTo("GAMES"));
    }

    [Test]
    public void Argument_HasLeadingSpacesStripped()
    {
        Assert.That(OsCliCommandMatcher.TryMatch("DIR     GAMES", "DIR", 3, out string arg), Is.True);
        Assert.That(arg, Is.EqualTo("GAMES"));
    }

    [Test]
    public void Argument_KeepsInternalSpaces()
    {
        Assert.That(OsCliCommandMatcher.TryMatch("SAVE PROG 3000 +100", "SAVE", 2, out string arg), Is.True);
        Assert.That(arg, Is.EqualTo("PROG 3000 +100"));
    }

    // ---- Dot abbreviations: the everyday case that used to fail ----

    [Test]
    public void DotAbbreviation_Matches_WhenLongEnough()
    {
        // "*CA." is what a BBC user actually types for *CAT.
        Assert.That(OsCliCommandMatcher.TryMatch("CA.", "CAT", 2, out string arg), Is.True);
        Assert.That(arg, Is.Empty);
    }

    [Test]
    public void DotAbbreviation_CarriesArgument()
    {
        Assert.That(OsCliCommandMatcher.TryMatch("LO.README", "LOAD", 2, out string arg), Is.True);
        Assert.That(arg, Is.EqualTo("README"));
    }

    [Test]
    public void DotAbbreviation_CarriesArgumentAfterSpaces()
    {
        Assert.That(OsCliCommandMatcher.TryMatch("LO. README 3000", "LOAD", 2, out string arg), Is.True);
        Assert.That(arg, Is.EqualTo("README 3000"));
    }

    [Test]
    public void DotAbbreviation_TooShort_DoesNotMatch()
    {
        // "C." is 1 character but CAT demands 2.
        Assert.That(OsCliCommandMatcher.TryMatch("C.", "CAT", 2, out _), Is.False);
    }

    [Test]
    public void DotAbbreviation_AtExactMinimum_Matches()
    {
        Assert.That(OsCliCommandMatcher.TryMatch("CA.", "CAT", 2, out _), Is.True);
    }

    [Test]
    public void FullNameFollowedByDot_Matches()
    {
        Assert.That(OsCliCommandMatcher.TryMatch("CAT.", "CAT", 2, out _), Is.True);
    }

    // ---- A bare prefix is NOT an abbreviation ----

    [Test]
    public void BarePrefix_WithoutDot_DoesNotMatch()
    {
        // Acorn abbreviations require the dot: "*CA" is not "*CAT".
        Assert.That(OsCliCommandMatcher.TryMatch("CA", "CAT", 2, out _), Is.False);
    }

    [Test]
    public void BarePrefix_WithArgument_DoesNotMatch()
    {
        Assert.That(OsCliCommandMatcher.TryMatch("DI GAMES", "DIR", 3, out _), Is.False);
    }

    // ---- Non-matches ----

    [Test]
    public void DifferentCommand_DoesNotMatch()
    {
        Assert.That(OsCliCommandMatcher.TryMatch("SAVE", "LOAD", 2, out _), Is.False);
    }

    [Test]
    public void LongerThanCommandName_DoesNotMatch()
    {
        // "CATALOGUE" cannot be a prefix of "CAT".
        Assert.That(OsCliCommandMatcher.TryMatch("CATALOGUE", "CAT", 2, out _), Is.False);
    }

    [Test]
    public void EmptyInput_DoesNotMatch()
    {
        Assert.That(OsCliCommandMatcher.TryMatch("", "CAT", 2, out _), Is.False);
    }

    // ---- Command names containing a space ("I AM") ----

    [Test]
    public void CommandNameWithSpace_MatchesInFull()
    {
        // The boundary scan starts at the minimum (2), so it skips the space INSIDE "I AM"
        // and stops at the space that ends it.
        Assert.That(OsCliCommandMatcher.TryMatch("I AM SYST", "I AM", 2, out string arg), Is.True);
        Assert.That(arg, Is.EqualTo("SYST"));
    }

    [Test]
    public void CommandNameWithSpace_CarriesUsernameAndPassword()
    {
        Assert.That(OsCliCommandMatcher.TryMatch("I AM SYST MYPASSWORD", "I AM", 2, out string arg), Is.True);
        Assert.That(arg, Is.EqualTo("SYST MYPASSWORD"));
    }

    // ---- The I. / I AM collision: the reason the minimum-offset scan exists ----

    [Test]
    public void DotI_SelectsInfo_NotLogin()
    {
        // INFO has a minimum of 1, so "I." abbreviates to INFO.
        Assert.That(OsCliCommandMatcher.TryMatch("I.", "INFO", 1, out string arg), Is.True);
        Assert.That(arg, Is.Empty);

        // Login must NOT be selected by "I.": scanning from index 2 of "I." finds no separator, so
        // the word stays "I.", and comparing that against "I AM" pits '.' against ' '.
        Assert.That(OsCliCommandMatcher.TryMatch("I.", "I AM", 2, out _), Is.False);
    }

    [Test]
    public void DotI_WithArgument_SelectsInfo()
    {
        Assert.That(OsCliCommandMatcher.TryMatch("I.README", "INFO", 1, out string arg), Is.True);
        Assert.That(arg, Is.EqualTo("README"));
    }

    [Test]
    public void PlainI_WithoutDot_SelectsNeither()
    {
        // A bare "I" is a prefix of both but has no dot, so neither is selected.
        Assert.That(OsCliCommandMatcher.TryMatch("I", "INFO", 1, out _), Is.False);
        Assert.That(OsCliCommandMatcher.TryMatch("I", "I AM", 2, out _), Is.False);
    }

    // ---- The false-positive class: *EXEC must not be captured by EX ----

    [Test]
    public void Exec_DoesNotMatchExamine()
    {
        // "EXEC" is not a prefix of "EX", so it cannot select it. The old StartsWith test asked the
        // question backwards and wrongly answered *EXEC with an Examine return code.
        Assert.That(OsCliCommandMatcher.TryMatch("EXEC", "EX", 2, out _), Is.False);
    }

    [Test]
    public void Catalogue_DoesNotMatchCat()
    {
        Assert.That(OsCliCommandMatcher.TryMatch("CATALOGUE", "CAT", 2, out _), Is.False);
    }

    [Test]
    public void Renumber_DoesNotMatchRename()
    {
        Assert.That(OsCliCommandMatcher.TryMatch("RENUMBER", "RENAME", 3, out _), Is.False);
    }

    // ---- Minimum larger than the name means "never abbreviate" ----

    [Test]
    public void MinimumEqualToNameLength_StillMatchesFullName()
    {
        Assert.That(OsCliCommandMatcher.TryMatch("BYE", "BYE", 3, out _), Is.True);
    }

    [Test]
    public void MinimumEqualToNameLength_RejectsAbbreviation()
    {
        Assert.That(OsCliCommandMatcher.TryMatch("BY.", "BYE", 3, out _), Is.False);
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void MinimumBelowOne_IsTreatedAsOne(int minimum)
    {
        // Guards against a zero minimum making the empty word match everything.
        Assert.That(OsCliCommandMatcher.TryMatch("I.", "INFO", minimum, out _), Is.True);
        Assert.That(OsCliCommandMatcher.TryMatch(".", "INFO", minimum, out _), Is.False);
    }
}
