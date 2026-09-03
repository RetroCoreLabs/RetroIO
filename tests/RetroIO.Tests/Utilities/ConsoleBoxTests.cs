//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using NUnit.Framework;
using RetroIO.Storage.Utilities;

namespace RetroIO.Tests.Utilities;

[TestFixture]
public class ConsoleBoxTests
{
    private static int ContentLength(string line)
    {
        return line.Length - 2;
    }

    [Test]
    public void FormatBorder_HasExpectedInnerWidth()
    {
        string border = ConsoleBox.FormatBorder('╔', '═', '╗');
        Assert.That(ContentLength(border), Is.EqualTo(ConsoleBox.InnerWidth));
    }

    [Test]
    public void FormatLine_PadsShortContentToInnerWidth()
    {
        string line = ConsoleBox.FormatLine("hello");
        Assert.That(ContentLength(line), Is.EqualTo(ConsoleBox.InnerWidth));
        Assert.That(line.EndsWith('║'), Is.True);
    }

    [Test]
    public void FormatLine_TruncatesLongContentToInnerWidth()
    {
        string longPath = "C:\\Users\\ronny\\OneDrive\\Documents\\RetroFS\\econet-root";
        string label = "  Root Directory:  ";
        string line = ConsoleBox.FormatLine(label + longPath);
        Assert.That(ContentLength(line), Is.EqualTo(ConsoleBox.InnerWidth));
        Assert.That(line.EndsWith('║'), Is.True);
    }

    [Test]
    public void FormatTitle_CentersTitleWithinInnerWidth()
    {
        string line = ConsoleBox.FormatTitle("RATE LIMIT CONFIGURATION");
        Assert.That(ContentLength(line), Is.EqualTo(ConsoleBox.InnerWidth));
        Assert.That(line.StartsWith('║'), Is.True);
        Assert.That(line.EndsWith('║'), Is.True);
    }

    [Test]
    public void LabelValue_TruncatesValueWhenLabelAndValueExceedInnerWidth()
    {
        string label = "  Root Directory:  ";
        string longPath = "C:\\Users\\ronny\\OneDrive\\Documents\\RetroFS\\econet-root-extra";
        string valueText = longPath;
        int maxValueLength = ConsoleBox.InnerWidth - label.Length;
        if (valueText.Length > maxValueLength)
            valueText = valueText.Substring(0, maxValueLength - 3) + "...";

        string line = ConsoleBox.FormatLine(label + valueText);
        Assert.That(ContentLength(line), Is.EqualTo(ConsoleBox.InnerWidth));
    }
}
