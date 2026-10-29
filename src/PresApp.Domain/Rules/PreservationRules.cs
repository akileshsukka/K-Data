using PresApp.Domain.Assets;

namespace PresApp.Domain.Rules;

/// <summary>Result of applying the derived rules after a change.</summary>
public sealed record DerivedOutcome(CurrentGate? ProposedCurrentGate, DateOnly? ProposedLastFieldActionDate, GateMove? AvailabilityMove = null);

/// <summary>Current Gate moved automatically by the Asset Availability rule (already applied, no approval needed).</summary>
public sealed record GateMove(CurrentGate From, CurrentGate To, string Reason);

/// <summary>
/// Derived rules of the Power App (handoff sections 6–8), with documentation now tracked per gate:
/// <list type="bullet">
/// <item>Current Gate = highest Completed gate, proposed to the user, never overriding Removed from Site.</item>
/// <item>Asset Availability = Removed from Site → Current Gate = Removed from Site (applied, even with completed gates).
/// Asset Availability changed away from Removed from Site → Current Gate = highest Completed gate, or New when none is.</item>
/// <item>Preservation Date = first day all 8 gates are Completed or Not Applicable with at least one Completed.</item>
/// <item>N2 Blanketed Date = first day G4 is Completed.</item>
/// <item>Last Field Action Date: set when empty, otherwise proposed when it would move forward.</item>
/// </list>
/// </summary>
public static class PreservationRules
{
    public const string RemovedFromSite = "Removed from Site";

    /// <summary>True when the Asset Availability value means the asset left the site (case and spaces ignored).</summary>
    public static bool IsRemoved(string? availability) =>
        availability is not null && ValueNormalizer.ListKey(availability) == "removedfromsite";

    /// <summary>The Current Gate the rules expect: Removed from Site when the asset is removed, else the highest Completed gate (or New).</summary>
    public static CurrentGate SuggestedCurrentGate(AssetPreservation p) =>
        IsRemoved(p.AssetAvailability) ? CurrentGate.RemovedFromSite : GateFromGates(p);

    /// <summary>Highest Completed gate, or New when none is (ignores Asset Availability).</summary>
    public static CurrentGate GateFromGates(AssetPreservation p) =>
        p.HighestCompletedGate() is { } g ? (CurrentGate)g : CurrentGate.New;

    /// <summary>
    /// Where the Asset Availability rule would move Current Gate when availability goes from <paramref name="before"/>
    /// to <paramref name="after"/>, or null when it does not move. Does not change anything.
    /// </summary>
    public static GateMove? AvailabilityMove(AssetPreservation p, string? before, string? after)
    {
        if (IsRemoved(after))
        {
            return p.CurrentGate == CurrentGate.RemovedFromSite ? null
                : new GateMove(p.CurrentGate, CurrentGate.RemovedFromSite, "Asset Availability is Removed from Site.");
        }
        if (IsRemoved(before) && p.CurrentGate == CurrentGate.RemovedFromSite)
        {
            var target = GateFromGates(p);
            return new GateMove(p.CurrentGate, target, target == CurrentGate.New
                ? "Asset Availability is no longer Removed from Site and no gate is completed."
                : "Asset Availability is no longer Removed from Site: highest completed gate.");
        }
        return null;
    }

    /// <summary>
    /// Applies the automatic milestones and the Asset Availability rule, and returns what needs the user's approval.
    /// <paramref name="availabilityBefore"/> is the Asset Availability before this change (only read when it changed).
    /// </summary>
    public static DerivedOutcome Apply(AssetPreservation p, bool gateStatusChanged, bool fieldActionHappened, DateOnly today,
        bool availabilityChanged = false, string? availabilityBefore = null)
    {
        // Asset Availability first: a removed asset goes to Removed from Site; one that comes back follows its gates.
        // A gate change on a removed asset also aligns it (it can never leave Removed from Site that way).
        GateMove? move = null;
        if (availabilityChanged || gateStatusChanged)
        {
            move = AvailabilityMove(p, availabilityChanged ? availabilityBefore : p.AssetAvailability, p.AssetAvailability);
            if (move is not null) p.CurrentGate = move.To;
        }

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

        return new DerivedOutcome(proposedGate, proposedDate, move);
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
