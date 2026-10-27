namespace PresApp.Domain.Simpro;

public enum SyncMode
{
    Preview = 0,
    Apply = 1,
    Scheduled = 2,
    Undo = 3
}

public enum SyncStatus
{
    Running = 0,
    Previewed = 1,
    Applied = 2,
    HeldForConflicts = 3,
    Failed = 4,
    Undone = 5
}

/// <summary>One push of PresApp data into Simpro, with its preview and results.</summary>
public class SimproSyncRun
{
    public long Id { get; set; }
    public SyncMode Mode { get; set; }
    public SyncStatus Status { get; set; }
    public string TriggeredBy { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public int CreatedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int FieldCount { get; set; }
    public int HeldCount { get; set; }
    public int RejectedCount { get; set; }
    public int NotInAppCount { get; set; }
    public string? Message { get; set; }
    public long? UndoOfRunId { get; set; }
    public List<SimproSyncItem> Items { get; set; } = [];
}

/// <summary>One field written (or held) for one asset in a sync run.</summary>
public class SimproSyncItem
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public SimproSyncRun Run { get; set; } = null!;
    public Guid AssetId { get; set; }
    public string AssetCode { get; set; } = "";
    public string? AssetTag { get; set; }
    public int? UnitId { get; set; }
    public string Field { get; set; } = "";
    public string? SimproValue { get; set; }
    public string? LastSyncedValue { get; set; }
    public string? NewValue { get; set; }
    public string Outcome { get; set; } = "";
    public string? Reason { get; set; }
}

/// <summary>Copy of a Simpro row before a sync changed it; lets a run be undone without a restore.</summary>
public class SimproBeforeImage
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public string TableName { get; set; } = "";
    public string RowKey { get; set; } = "";
    public string RowJson { get; set; } = "";
}

/// <summary>An equipment an administrator decided K Data wins for (conflict or Simpro edit); used by the next sync.</summary>
public class SimproForcedAsset
{
    public Guid AssetId { get; set; }
    public string RequestedBy { get; set; } = "";
    public DateTime RequestedAtUtc { get; set; }
}
