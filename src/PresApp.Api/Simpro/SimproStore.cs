using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace PresApp.Api.Simpro;

public sealed class SimproOptions
{
    public const string Section = "Simpro";

    /// <summary>Username written in tbl_flangeHistory for the app's events (max 50).</summary>
    public string SyncUsername { get; set; } = "SYNC-KDATA";

    /// <summary>Earlier sync usernames whose events also count as "last sync snapshot".</summary>
    public string[] PreviousSyncUsernames { get; set; } = ["SYNC-SHAREPOINT"];

    /// <summary>Events before this UTC time also count as snapshot (last full reload). Null = none.</summary>
    public DateTime? BaselineUtc { get; set; }

    /// <summary>Text Simpro receives for "Not Applicable" (Power BI and the existing Simpro data use "N/A").</summary>
    public string NotApplicableText { get; set; } = "N/A";

    public Guid NewInstallationId { get; set; } = Guid.Parse("D61FCD67-6827-4DCF-B730-3346197F62B7");
    public Guid NewWorkpackId { get; set; } = Guid.Parse("68143739-2D33-4203-967A-918EEB21B7E2");
    public Guid WorkpackItemTypeId { get; set; } = Guid.Parse("4D258552-8D88-4DDC-920D-D87DE3564E56");
    public int NewGateCode { get; set; } = 1;
    public int NewPriorityId { get; set; } = 3;
    public int CommandTimeoutSeconds { get; set; } = 120;
}

/// <summary>Opens connections to the Simpro database. Tests replace it with SQLite.</summary>
public interface ISimproConnectionFactory
{
    bool IsConfigured { get; }
    bool IsSqlServer { get; }
    DbConnection Create();
}

public sealed class SqlServerSimproConnectionFactory(IConfiguration configuration) : ISimproConnectionFactory
{
    private readonly string? connectionString = configuration.GetConnectionString("Simpro");
    public bool IsConfigured => !string.IsNullOrWhiteSpace(connectionString);
    public bool IsSqlServer => true;
    public DbConnection Create() => new SqlConnection(connectionString ?? throw new InvalidOperationException("ConnectionStrings:Simpro is not set."));
}

// ---------------------------------------------------------------------- data read from Simpro

/// <summary>Header columns of one tbl_flange row (or tbl_flangeChanges snapshot), keyed by field key.</summary>
public sealed class SimproHeader
{
    public Guid FlangeId { get; init; }
    public Guid? WorkpackId { get; init; }
    public Dictionary<string, string?> Values { get; } = [];
}

public sealed record SimproHistoryEvent(Guid FlangeId, Guid ActivityRecordId, DateTime? Created, string? Username, int Sequence, Guid? WorkpackId);

public sealed record SimproCustomValue(Guid Id, Guid OwnerId, Guid FieldId, string? Value);

public sealed class SimproData
{
    public Dictionary<Guid, SimproHeader> Flanges { get; } = [];
    public Dictionary<Guid, List<SimproCustomValue>> CustomValues { get; } = [];
    public Dictionary<Guid, List<SimproHistoryEvent>> History { get; } = [];
    public Dictionary<Guid, SimproHeader> BaseHeaders { get; } = [];          // by flangeId
    public Dictionary<Guid, Dictionary<Guid, string?>> BaseCustom { get; } = []; // by flangeId → fieldId → value
    public Dictionary<Guid, SimproHistoryEvent> BaseEvents { get; } = [];
    public Dictionary<int, string> Criticalities { get; } = [];
    public Dictionary<Guid, string> Locations { get; } = [];
    public HashSet<Guid> ActiveCustomFields { get; } = [];
}

/// <summary>
/// Plain parameterised SQL against the Simpro tables (works on SQL Server and on SQLite for the tests).
/// Never creates tables, columns or triggers in Simpro; never deletes anything outside an undo.
/// </summary>
public sealed class SimproStore(ISimproConnectionFactory factory, IOptions<SimproOptions> options)
{
    private readonly SimproOptions o = options.Value;

    private const string HeaderColumns =
        "flangeId, tagNo, custID, flangeDescription, flangeTag, pbsLevel1Id, pbsLevel2Id, flangeCriticalityId, flangeStatusId, workOrder, pipeSpec, WorkInstructions, verified, comments, workpackId";

    public bool IsConfigured => factory.IsConfigured;

    public async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var c = factory.Create();
        await c.OpenAsync(ct);
        return c;
    }

    /// <summary>Locks every equipment row for the rest of the transaction (SQL Server), like the script.</summary>
    public async Task LockAsync(DbConnection c, DbTransaction tx, CancellationToken ct)
    {
        if (!factory.IsSqlServer) return;
        await ScalarAsync(c, tx, "SELECT COUNT(*) FROM dbo.tbl_flange WITH (UPDLOCK, HOLDLOCK)", ct);
        await ScalarAsync(c, tx, "SELECT COUNT(*) FROM dbo.tbl_flangeCustomFieldValue WITH (UPDLOCK, HOLDLOCK)", ct);
        await ScalarAsync(c, tx, "SELECT COUNT(*) FROM dbo.tbl_flangeHistory WITH (UPDLOCK, HOLDLOCK)", ct);
    }

    public async Task<SimproData> LoadAsync(DbConnection c, DbTransaction? tx, CancellationToken ct)
    {
        var data = new SimproData();
        await ReadAsync(c, tx, $"SELECT {HeaderColumns} FROM {T("tbl_flange")}", r => { var h = ReadHeader(r); data.Flanges[h.FlangeId] = h; }, ct);

        var fieldIds = SimproFields.Customs.Select(f => f.FieldId!.Value).ToList();
        await ReadAsync(c, tx, $"SELECT id, ownerId, fieldId, value FROM {T("tbl_flangeCustomFieldValue")}", r =>
        {
            var v = new SimproCustomValue(G(r[0])!.Value, G(r[1])!.Value, G(r[2])!.Value, S(r[3]));
            if (!fieldIds.Contains(v.FieldId)) return;
            if (!data.CustomValues.TryGetValue(v.OwnerId, out var list)) data.CustomValues[v.OwnerId] = list = [];
            list.Add(v);
        }, ct);

        var withSnapshot = new HashSet<Guid>();
        await ReadAsync(c, tx, $"SELECT activityRecordId FROM {T("tbl_flangeChanges")}", r => withSnapshot.Add(G(r[0])!.Value), ct);
        await ReadAsync(c, tx, $"SELECT flangeId, activityRecordId, created, username, activitySequence, workpackId FROM {T("tbl_flangeHistory")}", r =>
        {
            var e = new SimproHistoryEvent(G(r[0])!.Value, G(r[1])!.Value, D(r[2]), S(r[3]), r[4] is DBNull ? 0 : Convert.ToInt32(r[4], CultureInfo.InvariantCulture), G(r[5]));
            if (!data.History.TryGetValue(e.FlangeId, out var list)) data.History[e.FlangeId] = list = [];
            list.Add(e);
        }, ct);

        // Last sync snapshot: the latest event written by the sync (or before the baseline) that has a header snapshot.
        var syncUsers = new HashSet<string>(o.PreviousSyncUsernames.Append(o.SyncUsername), StringComparer.OrdinalIgnoreCase);
        foreach (var (flangeId, events) in data.History)
        {
            var baseEvent = events
                .Where(e => withSnapshot.Contains(e.ActivityRecordId)
                            && (e.Username is not null && syncUsers.Contains(e.Username.Trim()) || o.BaselineUtc is { } b && e.Created <= b))
                .OrderByDescending(e => e.Created).ThenByDescending(e => e.Sequence).FirstOrDefault();
            if (baseEvent is not null) data.BaseEvents[flangeId] = baseEvent;
        }
        var records = data.BaseEvents.Values.Select(e => e.ActivityRecordId).ToList();
        var flangeOfRecord = data.BaseEvents.Values.ToDictionary(e => e.ActivityRecordId, e => e.FlangeId);
        foreach (var chunk in records.Chunk(500))
        {
            await ReadAsync(c, tx, $"SELECT {HeaderColumns}, activityRecordId FROM {T("tbl_flangeChanges")} WHERE activityRecordId IN ({Params(chunk.Length)})", r =>
            {
                var h = ReadHeader(r);
                data.BaseHeaders[flangeOfRecord[G(r[15])!.Value]] = h;
            }, ct, chunk.Cast<object>().ToArray());
            await ReadAsync(c, tx, $"SELECT ownerId, fieldId, value FROM {T("tbl_flangeCustomFieldChange")} WHERE ownerId IN ({Params(chunk.Length)})", r =>
            {
                var flangeId = flangeOfRecord[G(r[0])!.Value];
                if (!data.BaseCustom.TryGetValue(flangeId, out var map)) data.BaseCustom[flangeId] = map = [];
                var fieldId = G(r[1])!.Value;
                var value = S(r[2]);
                // MAX(value) as in the script when a snapshot has two rows of the same field
                if (!map.TryGetValue(fieldId, out var existing) || string.CompareOrdinal(value, existing) > 0) map[fieldId] = value;
            }, ct, chunk.Cast<object>().ToArray());
        }

        await ReadAsync(c, tx, $"SELECT criticalityId, criticality FROM {T("tbl_flangeCriticality")}", r =>
            data.Criticalities[Convert.ToInt32(r[0], CultureInfo.InvariantCulture)] = (S(r[1]) ?? "").Trim(), ct);
        await ReadAsync(c, tx, $"SELECT id, description FROM {T("tbl_locationBreakdown")}", r => data.Locations[G(r[0])!.Value] = (S(r[1]) ?? "").Trim(), ct);
        await ReadAsync(c, tx, $"SELECT id FROM {T("tbl_flangeCustomField")} WHERE active = 1", r => data.ActiveCustomFields.Add(G(r[0])!.Value), ct);
        return data;
    }

    private static SimproHeader ReadHeader(DbDataReader r)
    {
        var h = new SimproHeader { FlangeId = G(r[0])!.Value, WorkpackId = G(r[14]) };
        h.Values["TAGNO"] = S(r[1]);
        h.Values["CUSTID"] = S(r[2]);
        h.Values["DESC"] = S(r[3]);
        h.Values["DISC"] = S(r[4]);
        h.Values["PLANT"] = G(r[5])?.ToString();
        h.Values["UNIT"] = G(r[6])?.ToString();
        h.Values["PRIO"] = r[7] is DBNull ? null : Convert.ToString(r[7], CultureInfo.InvariantCulture);
        h.Values["GATE"] = r[8] is DBNull ? null : Convert.ToString(r[8], CultureInfo.InvariantCulture);
        h.Values["AVAIL"] = S(r[9]);
        h.Values["LFAD"] = S(r[10]);
        h.Values["REMARK"] = S(r[11]);
        h.Values["WALK"] = r[12] is DBNull ? null : Convert.ToBoolean(r[12], CultureInfo.InvariantCulture) ? "1" : "0";
        h.Values["EMCOM"] = S(r[13]);
        return h;
    }

    // ------------------------------------------------------------------ writes

    public async Task CreateFlangeAsync(DbConnection c, DbTransaction tx, Guid flangeId, IReadOnlyDictionary<string, string?> header, DateTime nowUtc, CancellationToken ct)
    {
        await ExecAsync(c, tx, $@"INSERT INTO {T("tbl_flange")}
            (flangeId, installationId, workpackId, tagNo, custID, flangeTag, flangeType, flangeDescription, pbsLevel1Id, pbsLevel2Id,
             flangeCriticalityId, flangeStatusId, workOrder, pipeSpec, WorkInstructions, comments, verified, equipmentType, leakTestMethod,
             witnessJoint, leakTestDeviation, testPackNo, deleteActivityRecordId, created, lastUpdated)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, 'ANSI', @p6, @p7, @p8, @p9, @p10, @p11, @p12, @p13, @p14, @p15, 'Standard', 'TBC', 0, 0, @p16, NULL, @p17, @p17)", ct,
            flangeId, o.NewInstallationId, o.NewWorkpackId, header["TAGNO"], header.GetValueOrDefault("CUSTID"), header.GetValueOrDefault("DISC"),
            header.GetValueOrDefault("DESC"), Guid.Parse(header["PLANT"]!), Guid.Parse(header["UNIT"]!),
            int.Parse(header.GetValueOrDefault("PRIO") ?? o.NewPriorityId.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
            int.Parse(header.GetValueOrDefault("GATE") ?? o.NewGateCode.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
            header.GetValueOrDefault("AVAIL"), header.GetValueOrDefault("LFAD"), header.GetValueOrDefault("REMARK"), header.GetValueOrDefault("EMCOM"),
            header.GetValueOrDefault("WALK") == "1", Guid.Empty, nowUtc);
        await ExecAsync(c, tx, $"INSERT INTO {T("tbl_workpackItem")} (id, workpackId, itemId, itemTypeId) VALUES (@p0, @p1, @p2, @p3)", ct,
            Guid.NewGuid(), o.NewWorkpackId, flangeId, o.WorkpackItemTypeId);
    }

    /// <summary>Updates only the header columns given (by field key) and lastUpdated.</summary>
    public async Task UpdateHeaderAsync(DbConnection c, DbTransaction tx, Guid flangeId, IReadOnlyDictionary<string, string?> changes, DateTime nowUtc, CancellationToken ct)
    {
        var sets = new List<string>();
        var values = new List<object?>();
        foreach (var (key, value) in changes)
        {
            var field = SimproFields.ByKey[key];
            sets.Add($"{field.Column} = @p{values.Count}");
            values.Add(field.Type switch
            {
                SimproType.Plant or SimproType.Unit => value is null ? null : Guid.Parse(value),
                SimproType.Priority or SimproType.Gate => value is null ? null : int.Parse(value, CultureInfo.InvariantCulture),
                SimproType.Bit => value == "1",
                _ => value
            });
        }
        sets.Add($"lastUpdated = @p{values.Count}");
        values.Add(nowUtc);
        values.Add(flangeId);
        await ExecAsync(c, tx, $"UPDATE {T("tbl_flange")} SET {string.Join(", ", sets)} WHERE flangeId = @p{values.Count - 1}", ct, values.ToArray());
    }

    public Task UpdateCustomAsync(DbConnection c, DbTransaction tx, Guid id, string value, CancellationToken ct) =>
        ExecAsync(c, tx, $"UPDATE {T("tbl_flangeCustomFieldValue")} SET value = @p0 WHERE id = @p1", ct, value, id);

    public async Task<Guid> InsertCustomAsync(DbConnection c, DbTransaction tx, Guid flangeId, Guid fieldId, string value, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await ExecAsync(c, tx, $"INSERT INTO {T("tbl_flangeCustomFieldValue")} (id, fieldId, ownerId, value) VALUES (@p0, @p1, @p2, @p3)", ct, id, fieldId, flangeId, value);
        return id;
    }

    /// <summary>One history event like Simpro (1 = Flange Added, 2 = Flange Modified) plus its header and custom snapshots.</summary>
    public async Task<(Guid Pkid, Guid RecordId)> WriteEventAsync(DbConnection c, DbTransaction tx, Guid flangeId, bool created, int sequence, Guid? workpackId, DateTime nowUtc, CancellationToken ct)
    {
        var recordId = Guid.NewGuid();
        var pkid = Guid.NewGuid();
        await ExecAsync(c, tx, $@"INSERT INTO {T("tbl_flangeHistory")}
            (flangeId, workpackId, activityId, activityRecordId, eventDate, comments, pkid, username, activitySequence, created)
            VALUES (@p0, @p1, @p2, @p3, @p4, NULL, @p5, @p6, @p7, @p8)", ct,
            flangeId, workpackId, created ? 1 : 2, recordId, nowUtc.Date, pkid, o.SyncUsername, sequence, nowUtc);
        await ExecAsync(c, tx, $@"INSERT INTO {T("tbl_flangeChanges")}
            (activityRecordId, flangeId, installationId, tagNo, custID, flangeTag, flangeType, lbsLevel1Id, lbsLevel2Id, lbsLevel3Id,
             flangeCriticalityId, flangeStatusId, pipeSpec, flangeDescription, comments, WorkInstructions, pbsLevel1Id, pbsLevel2Id, pbsLevel3Id,
             equipmentType, witnessJoint, verified, leakTestMethod, testPackNo, workpackId, drawingId, lastActivityType, lastActivityRecord,
             lastTorqueRecord, lastTensionRecord, isoId, lastHistoryId, currentSpecId, RetestDate, otherDrawingId, lastUpdated, workOrder)
            SELECT @p0, flangeId, installationId, tagNo, custID, flangeTag, flangeType, lbsLevel1Id, lbsLevel2Id, lbsLevel3Id,
             flangeCriticalityId, flangeStatusId, pipeSpec, flangeDescription, comments, WorkInstructions, pbsLevel1Id, pbsLevel2Id, pbsLevel3Id,
             equipmentType, witnessJoint, verified, leakTestMethod, testPackNo, workpackId, drawingId, lastActivityType, lastActivityRecord,
             lastTorqueRecord, lastTensionRecord, isoId, lastHistoryId, currentSpecId, RetestDate, otherDrawingId, lastUpdated, workOrder
            FROM {T("tbl_flange")} WHERE flangeId = @p1", ct, recordId, flangeId);

        var rows = new List<(Guid FieldId, string? Value)>();
        await ReadAsync(c, tx, $"SELECT fieldId, value FROM {T("tbl_flangeCustomFieldValue")} WHERE ownerId = @p0", r => rows.Add((G(r[0])!.Value, S(r[1]))), ct, flangeId);
        foreach (var (fieldId, value) in rows)
        {
            await ExecAsync(c, tx, $"INSERT INTO {T("tbl_flangeCustomFieldChange")} (id, fieldId, ownerId, value) VALUES (@p0, @p1, @p2, @p3)", ct,
                Guid.NewGuid(), fieldId, recordId, value);
        }
        return (pkid, recordId);
    }

    public async Task<Dictionary<string, object?>> ReadRowAsync(DbConnection c, DbTransaction tx, string table, string keyColumn, Guid key, CancellationToken ct)
    {
        var row = new Dictionary<string, object?>();
        await ReadAsync(c, tx, $"SELECT * FROM {T(table)} WHERE {keyColumn} = @p0", r =>
        {
            for (var i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r[i] is DBNull ? null : r[i];
        }, ct, key);
        return row;
    }

    public Task<int> DeleteAsync(DbConnection c, DbTransaction tx, string table, string keyColumn, Guid key, CancellationToken ct) =>
        ExecAsync(c, tx, $"DELETE FROM {T(table)} WHERE {keyColumn} = @p0", ct, key);

    public Task<object?> CountAsync(DbConnection c, DbTransaction tx, string table, CancellationToken ct) =>
        ScalarAsync(c, tx, $"SELECT COUNT(*) FROM {T(table)}", ct);

    // ------------------------------------------------------------------ plumbing

    private string T(string table) => factory.IsSqlServer ? $"dbo.{table}" : table;

    private static string Params(int n) => string.Join(", ", Enumerable.Range(0, n).Select(i => $"@p{i}"));

    private DbCommand Command(DbConnection c, DbTransaction? tx, string sql, object?[] args)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.CommandTimeout = o.CommandTimeoutSeconds;
        for (var i = 0; i < args.Length; i++)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = $"@p{i}";
            p.Value = args[i] ?? DBNull.Value;
            if (args[i] is string s) { p.DbType = DbType.String; p.Size = Math.Max(s.Length, 1); }
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    private async Task ReadAsync(DbConnection c, DbTransaction? tx, string sql, Action<DbDataReader> row, CancellationToken ct, params object?[] args)
    {
        await using var cmd = Command(c, tx, sql, args);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) row(r);
    }

    private async Task<int> ExecAsync(DbConnection c, DbTransaction tx, string sql, CancellationToken ct, params object?[] args)
    {
        await using var cmd = Command(c, tx, sql, args);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<object?> ScalarAsync(DbConnection c, DbTransaction? tx, string sql, CancellationToken ct, params object?[] args)
    {
        await using var cmd = Command(c, tx, sql, args);
        return await cmd.ExecuteScalarAsync(ct);
    }

    internal static Guid? G(object value) => value switch
    {
        Guid g => g,
        string s when Guid.TryParse(s, out var g) => g,
        byte[] { Length: 16 } b => new Guid(b),
        _ => null
    };

    internal static string? S(object value) => value is DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);

    internal static DateTime? D(object value) => value switch
    {
        DateTime d => d,
        string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) => d,
        _ => null
    };
}
