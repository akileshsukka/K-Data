using PresApp.Domain.Assets;

namespace PresApp.Domain.Rules;

/// <summary>Result of applying the derived rules after a change.</summary>
public sealed record DerivedOutcome(CurrentGate? ProposedCurrentGate, DateOnly? ProposedLastFieldActionDate);

/// <summary>
/// Derived rules of the Power App (handoff sections 6–8), with documentation now tracked per gate:
/// <list type="bullet">
/// <item>Current Gate = highest Completed gate, proposed to the user, never overriding Removed from Site.</item>
/// <item>Preservation Date = first day all 8 gates are Completed or Not Applicable with at least one Completed.</item>
/// <item>N2 Blanketed Date = first day G4 is Completed.</item>
/// <item>Last Field Action Date: set when empty, otherwise proposed when it would move forward.</item>
/// </list>
/// </summary>
public static class PreservationRules
{
    public static CurrentGate SuggestedCurrentGate(AssetPreservation p) =>
        p.HighestCompletedGate() is { } g ? (CurrentGate)g : CurrentGate.New;

    /// <summary>Applies the automatic milestones and returns what needs the user's approval.</summary>
    public static DerivedOutcome Apply(AssetPreservation p, bool gateStatusChanged, bool fieldActionHappened, DateOnly today)
    {
        CurrentGate? proposedGate = null;
        if (gateStatusChanged)
        {
            if (p.PreservationDate is null && AllGatesClosed(p)) p.PreservationDate = today;
            if (p.N2BlanketedDate is null && AssetPreservation.IsCompleted(p.G4Status)) p.N2BlanketedDate = today;

            var suggested = SuggestedCurrentGate(p);
            if (p.CurrentGate != CurrentGate.RemovedFromSite && suggested != p.CurrentGate) proposedGate = suggested;
        }

        DateOnly? proposedDate = null;
        if (fieldActionHappened)
        {
            if (p.LastFieldActionDate is null) p.LastFieldActionDate = today;
            else if (today > p.LastFieldActionDate) proposedDate = today;
        }

        return new DerivedOutcome(proposedGate, proposedDate);
    }

    public static bool AllGatesClosed(AssetPreservation p)
    {
        var anyCompleted = false;
        for (var g = 1; g <= 8; g++)
        {
            var s = p.GetGateStatus(g);
            if (AssetPreservation.IsCompleted(s)) anyCompleted = true;
            else if (!string.Equals(s?.Trim(), ValueNormalizer.NotApplicable, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return anyCompleted;
    }

    /// <summary>Fields whose change counts as a field action (moves Last Field Action Date).</summary>
    public static bool IsFieldAction(string field) =>
        field.StartsWith('G') && field.EndsWith("Status", StringComparison.Ordinal)
        || field is nameof(AssetPreservation.G1BulkLiquidDrained) or nameof(AssetPreservation.N2BlanketStatus);
}
