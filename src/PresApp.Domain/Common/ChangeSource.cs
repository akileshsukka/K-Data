namespace PresApp.Domain.Common;

/// <summary>Where a change or reading came from. Stored as text.</summary>
public enum ChangeSource
{
    App,
    Bulk,
    Excel,
    Onboarding,
    Migration,
    HistoryImport,
    Simpro,
    System
}
