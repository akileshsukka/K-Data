using System.Data.Common;
using Microsoft.Data.Sqlite;
using PresApp.Api.Simpro;

namespace PresApp.Tests.Support;

/// <summary>
/// The Simpro tables the sync uses, on a shared in-memory SQLite database, with the columns of the real schema
/// (export of 24-Oct-2026). Guids are stored the way the app binds them.
/// </summary>
public sealed class FakeSimpro : ISimproConnectionFactory, IDisposable
{
    private readonly string connectionString = $"Data Source=simpro-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly SqliteConnection keepAlive;
    private bool created;

    public FakeSimpro()
    {
        keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open();
    }

    public bool IsConfigured => created;
    public bool IsSqlServer => false;
    public DbConnection Create() => new SqliteConnection(connectionString);

    public static readonly Guid InstallationId = Guid.Parse("D61FCD67-6827-4DCF-B730-3346197F62B7");
    public static readonly Guid WorkpackId = Guid.Parse("68143739-2D33-4203-967A-918EEB21B7E2");

    public void EnsureCreated(IEnumerable<(Guid Id, string Name)> locations)
    {
        if (created) return;
        Exec(Schema);
        foreach (var (id, name) in locations) Exec("INSERT INTO tbl_locationBreakdown (id, description, parentId, deleted, level) VALUES (@p0, @p1, @p2, 0, 'x')", id, name, Guid.Empty);
        for (var i = 1; i <= 3; i++) Exec("INSERT INTO tbl_flangeCriticality (id, criticalityId, criticality, criticalityEnabled) VALUES (@p0, @p1, @p2, 1)", Guid.NewGuid(), i, $"Priority {i}");
        foreach (var f in SimproFields.Customs) Exec("INSERT INTO tbl_flangeCustomField (id, typeId, fieldName, description, required, active, displayOnTablet, fieldSequence) VALUES (@p0, 2, @p1, @p2, 0, 1, 0, 0)", f.FieldId!.Value, f.Key, f.Label);
        created = true;
    }

    /// <summary>Replaces the tables with a SQLite copy of a real Simpro database (local checks with production data).</summary>
    public void LoadFrom(string sqliteFile)
    {
        using var source = new SqliteConnection($"Data Source={sqliteFile};Mode=ReadOnly");
        source.Open();
        source.BackupDatabase(keepAlive);
        created = true;
    }

    /// <summary>An equipment as Simpro has it, with one SYNC-SHAREPOINT event whose snapshot equals the current values.</summary>
    public void AddFlange(Guid id, string tagNo, string custId, string discipline, Guid plant, Guid unit, int gateCode, Dictionary<string, string> custom, bool withSnapshot = true)
    {
        var now = DateTime.UtcNow.AddDays(-2);
        Exec(@"INSERT INTO tbl_flange (flangeId, installationId, tagNo, custID, flangeTag, flangeType, flangeDescription, pbsLevel1Id, pbsLevel2Id,
               flangeCriticalityId, flangeStatusId, workpackId, equipmentType, witnessJoint, verified, created, lastUpdated, leakTestMethod, leakTestDeviation)
               VALUES (@p0, @p1, @p2, @p3, @p4, 'ANSI', 'Item', @p5, @p6, 3, @p7, @p8, 'Standard', 0, 0, @p9, @p9, 'TBC', 0)",
            id, InstallationId, tagNo, custId, discipline, plant, unit, gateCode, WorkpackId, now);
        foreach (var (key, value) in custom)
            Exec("INSERT INTO tbl_flangeCustomFieldValue (id, fieldId, ownerId, value) VALUES (@p0, @p1, @p2, @p3)", Guid.NewGuid(), SimproFields.ByKey[key].FieldId!.Value, id, value);
        if (!withSnapshot) return;
        var record = Guid.NewGuid();
        Exec("INSERT INTO tbl_flangeHistory (flangeId, workpackId, activityId, activityRecordId, eventDate, pkid, username, activitySequence, created) VALUES (@p0, @p1, 2, @p2, @p3, @p4, 'SYNC-SHAREPOINT', 1, @p3)",
            id, WorkpackId, record, now, Guid.NewGuid());
        Exec(@"INSERT INTO tbl_flangeChanges (activityRecordId, flangeId, installationId, tagNo, custID, flangeTag, flangeType, flangeCriticalityId, flangeStatusId, pipeSpec,
               flangeDescription, comments, WorkInstructions, pbsLevel1Id, pbsLevel2Id, equipmentType, witnessJoint, verified, leakTestMethod, workpackId, lastUpdated, workOrder)
               SELECT @p0, flangeId, installationId, tagNo, custID, flangeTag, flangeType, flangeCriticalityId, flangeStatusId, pipeSpec, flangeDescription, comments,
               WorkInstructions, pbsLevel1Id, pbsLevel2Id, equipmentType, witnessJoint, verified, leakTestMethod, workpackId, lastUpdated, workOrder FROM tbl_flange WHERE flangeId = @p1", record, id);
        Exec("INSERT INTO tbl_flangeCustomFieldChange (id, fieldId, ownerId, value) SELECT lower(hex(randomblob(16))), fieldId, @p0, value FROM tbl_flangeCustomFieldValue WHERE ownerId = @p1", record, id);
    }

    public string? Custom(Guid flangeId, string key) =>
        Scalar("SELECT value FROM tbl_flangeCustomFieldValue WHERE ownerId = @p0 AND fieldId = @p1", flangeId, SimproFields.ByKey[key].FieldId!.Value) as string;

    public object? Header(Guid flangeId, string column) => Scalar($"SELECT {column} FROM tbl_flange WHERE flangeId = @p0", flangeId);

    public long Count(string table, string where = "1 = 1", params object[] args) => (long)Scalar($"SELECT COUNT(*) FROM {table} WHERE {where}", args)!;

    public void Exec(string sql, params object?[] args)
    {
        using var c = new SqliteConnection(connectionString);
        c.Open();
        using var cmd = Bind(c, sql, args);
        cmd.ExecuteNonQuery();
    }

    public object? Scalar(string sql, params object?[] args)
    {
        using var c = new SqliteConnection(connectionString);
        c.Open();
        using var cmd = Bind(c, sql, args);
        return cmd.ExecuteScalar();
    }

    private static SqliteCommand Bind(SqliteConnection c, string sql, object?[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue($"@p{i}", args[i] ?? DBNull.Value);
        return cmd;
    }

    public void Dispose() => keepAlive.Dispose();

    private const string Schema = @"
CREATE TABLE tbl_flange (flangeId BLOB PRIMARY KEY, installationId BLOB, tagNo TEXT NOT NULL, custID TEXT, flangeTag TEXT, flangeType TEXT,
  lbsLevel1Id BLOB, lbsLevel2Id BLOB, lbsLevel3Id BLOB, flangeCriticalityId INTEGER, flangeStatusId INTEGER, pipeSpec TEXT, flangeDescription TEXT,
  comments TEXT, WorkInstructions TEXT, workpackId BLOB NOT NULL, drawingId BLOB, lastActivityType INTEGER, lastActivityRecord BLOB, lastTorqueRecord BLOB,
  lastTensionRecord BLOB, isoId BLOB, lastHistoryId BLOB, currentSpecId BLOB, pbsLevel1Id BLOB, pbsLevel2Id BLOB, pbsLevel3Id BLOB, RetestDate TEXT,
  equipmentType TEXT NOT NULL, witnessJoint INTEGER NOT NULL, otherDrawingId BLOB, verified INTEGER NOT NULL, created TEXT NOT NULL, lastUpdated TEXT NOT NULL,
  leakTestMethod TEXT NOT NULL, testPackNo BLOB, workOrder TEXT, deleteActivityRecordId BLOB, leakTestDeviation INTEGER NOT NULL);
CREATE TABLE tbl_flangeChanges (activityRecordId BLOB PRIMARY KEY, flangeId BLOB NOT NULL, installationId BLOB, tagNo TEXT NOT NULL, custID TEXT, flangeTag TEXT,
  flangeType TEXT, lbsLevel1Id BLOB, lbsLevel2Id BLOB, lbsLevel3Id BLOB, flangeCriticalityId INTEGER, flangeStatusId INTEGER, pipeSpec TEXT, flangeDescription TEXT,
  comments TEXT, WorkInstructions TEXT, pbsLevel1Id BLOB, pbsLevel2Id BLOB, pbsLevel3Id BLOB, equipmentType TEXT NOT NULL, witnessJoint INTEGER NOT NULL,
  verified INTEGER NOT NULL, leakTestMethod TEXT NOT NULL, testPackNo BLOB, workpackId BLOB NOT NULL, drawingId BLOB, lastActivityType INTEGER,
  lastActivityRecord BLOB, lastTorqueRecord BLOB, lastTensionRecord BLOB, isoId BLOB, lastHistoryId BLOB, currentSpecId BLOB, RetestDate TEXT,
  otherDrawingId BLOB, lastUpdated TEXT NOT NULL, workOrder TEXT);
CREATE TABLE tbl_flangeCriticality (id BLOB PRIMARY KEY, criticalityId INTEGER NOT NULL, criticality TEXT, criticalityEnabled INTEGER NOT NULL, projectId BLOB);
CREATE TABLE tbl_flangeCustomField (id BLOB PRIMARY KEY, typeId INTEGER NOT NULL, restrictionId BLOB, fieldName TEXT NOT NULL, description TEXT,
  required INTEGER NOT NULL, active INTEGER NOT NULL, unitsId BLOB, displayOnTablet INTEGER NOT NULL, fieldSequence INTEGER NOT NULL, isReadOnly INTEGER);
CREATE TABLE tbl_flangeCustomFieldChange (id BLOB PRIMARY KEY, fieldId BLOB NOT NULL, ownerId BLOB NOT NULL, value TEXT);
CREATE TABLE tbl_flangeCustomFieldValue (id BLOB PRIMARY KEY, fieldId BLOB NOT NULL, ownerId BLOB NOT NULL REFERENCES tbl_flange(flangeId), value TEXT NOT NULL);
CREATE TABLE tbl_flangeHistory (flangeId BLOB NOT NULL, workpackId BLOB, activityId INTEGER NOT NULL, activityRecordId BLOB NOT NULL, anomalyId BLOB,
  eventDate TEXT, attachedFileName TEXT, gist BLOB, technician1 BLOB, technician2 BLOB, inspectedBy BLOB, comments TEXT, pkid BLOB PRIMARY KEY,
  username TEXT, activitySequence INTEGER NOT NULL, documentId BLOB, technician1SignatureId BLOB, technician2SignatureId BLOB, inspectedBySignatureId BLOB,
  created TEXT, witnessedBy BLOB, currentTag TEXT, newTag TEXT, deviation TEXT);
CREATE TABLE tbl_locationBreakdown (id BLOB PRIMARY KEY, description TEXT, parentId BLOB NOT NULL, deleted INTEGER NOT NULL, level TEXT NOT NULL,
  linkedTypeId BLOB, linkedItemId BLOB, longDescription TEXT);
CREATE TABLE tbl_workpackItem (id BLOB PRIMARY KEY, workpackId BLOB NOT NULL, itemId BLOB NOT NULL, itemTypeId BLOB NOT NULL);";
}
