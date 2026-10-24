namespace PresApp.Domain.Assets;

/// <summary>Current Gate of a preserved asset. Stored as text (New, G1..G8, RemovedFromSite).</summary>
public enum CurrentGate
{
    New = 0,
    G1 = 1,
    G2 = 2,
    G3 = 3,
    G4 = 4,
    G5 = 5,
    G6 = 6,
    G7 = 7,
    G8 = 8,
    RemovedFromSite = 99
}
