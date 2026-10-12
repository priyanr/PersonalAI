using System.Text;
using FluentAssertions;
using PersonalAI.Core.Services.Sandbox;

namespace PersonalAI.Tests.Sandbox;

public class BoundedOutputAccumulatorTests
{
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void ShortOutput_IsReturnedVerbatimAndNotMarkedTruncated()
    {
        var acc = new BoundedOutputAccumulator(100);
        acc.Append(Utf8("hello world"));

        acc.ToString().Should().Be("hello world");
        acc.Truncated.Should().BeFalse();
    }

    [Fact]
    public void LongOutput_KeepsTheTail_BecauseShellErrorsLandAtTheEnd()
    {
        // The regression this type exists for: a long build log whose actual error is the LAST line.
        // Head-only truncation would return the banner and silently discard the diagnosis.
        var acc = new BoundedOutputAccumulator(300);
        acc.Append(Utf8("START-OF-BUILD\n" + new string('x', 5000) + "\nERROR: could not build wheel"));

        var rendered = acc.ToString();

        rendered.Should().EndWith("ERROR: could not build wheel");
        rendered.Should().StartWith("START-OF-BUILD");
        rendered.Should().Contain("characters elided");
        acc.Truncated.Should().BeTrue();
    }

    [Fact]
    public void Truncation_KeepsRenderedOutputRoughlyWithinBudget()
    {
        var acc = new BoundedOutputAccumulator(300);
        acc.Append(Utf8(new string('y', 100_000)));

        // Budget + the short elision marker; the point is it stays bounded regardless of input size.
        acc.ToString().Length.Should().BeLessThan(400);
    }

    [Fact]
    public void MultiByteCharacterSplitAcrossChunks_IsDecodedIntact()
    {
        // Chunks are cut at arbitrary BYTE offsets, so a multi-byte sequence routinely straddles two of
        // them. Decoding each chunk independently would yield U+FFFD replacement characters here.
        var bytes = Utf8("héllo wörld — ✓ 日本語");
        var acc = new BoundedOutputAccumulator(1000);

        for (var i = 0; i < bytes.Length; i++)
            acc.Append([bytes[i]]); // worst case: one byte at a time

        acc.ToString().Should().Be("héllo wörld — ✓ 日本語");
        acc.ToString().Should().NotContain("\uFFFD");
    }

    [Fact]
    public void ZeroBudget_ElidesEverythingWithoutThrowing()
    {
        var acc = new BoundedOutputAccumulator(0);
        acc.Append(Utf8("anything at all"));

        acc.Truncated.Should().BeTrue();
        acc.ToString().Should().Contain("elided");
    }

    [Fact]
    public void EmptyInput_RendersEmpty()
    {
        var acc = new BoundedOutputAccumulator(100);
        acc.Append([]);

        acc.ToString().Should().BeEmpty();
        acc.Truncated.Should().BeFalse();
    }
}
