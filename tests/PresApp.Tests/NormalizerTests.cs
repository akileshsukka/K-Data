using PresApp.Domain.Assets;
using PresApp.Domain.Rules;

namespace PresApp.Tests;

public sealed class NormalizerTests
{
    private static readonly string[] GateOptions = ["Completed", "Ongoing", "Hold", "Not Applicable"];

    [Theory]
    [InlineData("N/A", "Not Applicable")]
    [InlineData(" n.a. ", "Not Applicable")]
    [InlineData("HOLD", "Hold")]
    [InlineData("On going", "Ongoing")]
    [InlineData("Completed - Documentation Pending", "Completed")]
    [InlineData("completed", "Completed")]
    public void Gate_values_map_to_the_canonical_option(string raw, string expected)
    {
        var (value, matched) = ValueNormalizer.Canonical(raw, GateOptions, isGateStatus: true);
        Assert.True(matched);
        Assert.Equal(expected, value);
    }

    [Fact]
    public void Common_spellings_match_in_any_list_that_has_the_value()
    {
        Assert.Equal(("Completed", true), ValueNormalizer.Canonical("Complete", ["Completed", "Ongoing", "Not Applicable"]));
        Assert.Equal(("Complete", false), ValueNormalizer.Canonical("Complete", ["Yes", "No"]));
    }

    [Fact]
    public void Unknown_values_are_kept_and_flagged()
    {
        var (value, matched) = ValueNormalizer.Canonical("  Purging ", ["Draining"]);
        Assert.False(matched);
        Assert.Equal("Purging", value);
    }

    [Fact]
    public void Text_is_trimmed_with_line_breaks_kept()
    {
        Assert.Equal("Motor removed\nFan  belt".Replace("  ", " "), ValueNormalizer.CleanText("  Motor   removed \r\n Fan  belt "));
        Assert.Null(ValueNormalizer.CleanText("   "));
    }

    [Fact]
    public void Preservation_date_needs_every_gate_closed_and_one_completed()
    {
        var p = new AssetPreservation();
        for (var g = 1; g <= 8; g++) p.SetGateStatus(g, "Not Applicable");
        Assert.False(PreservationRules.AllGatesClosed(p));
        p.G1Status = "Completed";
        Assert.True(PreservationRules.AllGatesClosed(p));
        p.G8Status = "Hold";
        Assert.False(PreservationRules.AllGatesClosed(p));
    }

    [Fact]
    public void Removed_from_site_is_never_proposed_over()
    {
        var p = new AssetPreservation { CurrentGate = CurrentGate.RemovedFromSite, G3Status = "Completed" };
        var outcome = PreservationRules.Apply(p, gateStatusChanged: true, fieldActionHappened: false, new DateOnly(2026, 10, 24));
        Assert.Null(outcome.ProposedCurrentGate);
    }
}
