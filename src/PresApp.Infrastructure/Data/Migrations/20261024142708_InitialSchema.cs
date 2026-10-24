using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PresApp.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppSetting",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSetting", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "ChangeLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EntityType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EntityKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Field = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OldValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ChangedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ChangedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangeLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Discipline",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    AssetType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Prefix = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: true),
                    SyncToSimpro = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Discipline", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EquipmentIdSequence",
                columns: table => new
                {
                    Prefix = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    UnitNumber = table.Column<int>(type: "int", nullable: false),
                    LastNumber = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquipmentIdSequence", x => new { x.Prefix, x.UnitNumber });
                });

            migrationBuilder.CreateTable(
                name: "GroupType",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupType", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoginAudit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    AtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Success = table.Column<bool>(type: "bit", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoginAudit", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MeasurementType",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    UnitOfMeasure = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    EvidenceForGate = table.Column<int>(type: "int", nullable: true),
                    Gate = table.Column<int>(type: "int", nullable: true),
                    MinValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    MaxValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    AppliesTo = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeasurementType", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OptionSet",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OptionSet", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Plant",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SimproLocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Plant", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Role",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Role", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SimproBeforeImage",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RunId = table.Column<long>(type: "bigint", nullable: false),
                    TableName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    RowKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RowJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SimproBeforeImage", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SimproSyncRun",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Mode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    TriggeredBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedCount = table.Column<int>(type: "int", nullable: false),
                    UpdatedCount = table.Column<int>(type: "int", nullable: false),
                    FieldCount = table.Column<int>(type: "int", nullable: false),
                    HeldCount = table.Column<int>(type: "int", nullable: false),
                    RejectedCount = table.Column<int>(type: "int", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    UndoOfRunId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SimproSyncRun", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FieldDefinition",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AssetType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DataType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    OptionSetId = table.Column<int>(type: "int", nullable: true),
                    MaxLength = table.Column<int>(type: "int", nullable: true),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false),
                    Section = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FieldDefinition", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FieldDefinition_OptionSet_OptionSetId",
                        column: x => x.OptionSetId,
                        principalTable: "OptionSet",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OptionValue",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OptionSetId = table.Column<int>(type: "int", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OptionValue", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OptionValue_OptionSet_OptionSetId",
                        column: x => x.OptionSetId,
                        principalTable: "OptionSet",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Unit",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PlantId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    SimproLocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Unit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Unit_Plant_PlantId",
                        column: x => x.PlantId,
                        principalTable: "Plant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AppUser",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RoleId = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    LastLoginAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EmergencyPasswordHash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUser", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppUser_Role_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Role",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RolePermission",
                columns: table => new
                {
                    RoleId = table.Column<int>(type: "int", nullable: false),
                    Permission = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolePermission", x => new { x.RoleId, x.Permission });
                    table.ForeignKey(
                        name: "FK_RolePermission_Role_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Role",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SimproSyncItem",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RunId = table.Column<long>(type: "bigint", nullable: false),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssetCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Field = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SimproValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SimproSyncItem", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SimproSyncItem_SimproSyncRun_RunId",
                        column: x => x.RunId,
                        principalTable: "SimproSyncRun",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Asset",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Tag = table.Column<string>(type: "nvarchar(55)", maxLength: 55, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    DisciplineId = table.Column<int>(type: "int", nullable: false),
                    UnitId = table.Column<int>(type: "int", nullable: false),
                    Priority = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    SimproFlangeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LegacySharePointId = table.Column<int>(type: "int", nullable: true),
                    MigrationNotes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Asset", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Asset_Discipline_DisciplineId",
                        column: x => x.DisciplineId,
                        principalTable: "Discipline",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Asset_Unit_UnitId",
                        column: x => x.UnitId,
                        principalTable: "Unit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssetGroup",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GroupTypeId = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    UnitId = table.Column<int>(type: "int", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetGroup", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssetGroup_GroupType_GroupTypeId",
                        column: x => x.GroupTypeId,
                        principalTable: "GroupType",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetGroup_Unit_UnitId",
                        column: x => x.UnitId,
                        principalTable: "Unit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DashboardCategory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UnitId = table.Column<int>(type: "int", nullable: false),
                    Section = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    ActionBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    EmailAction = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DashboardCategory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DashboardCategory_Unit_UnitId",
                        column: x => x.UnitId,
                        principalTable: "Unit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExcelSession",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UnitId = table.Column<int>(type: "int", nullable: false),
                    AssetType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ExportedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExportedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FormatVersion = table.Column<int>(type: "int", nullable: false),
                    StructureSignature = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExcelSession", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExcelSession_Unit_UnitId",
                        column: x => x.UnitId,
                        principalTable: "Unit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ImportRun",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    UnitId = table.Column<int>(type: "int", nullable: true),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UploadedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    UploadedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AppliedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    AppliedCount = table.Column<int>(type: "int", nullable: false),
                    IssueCount = table.Column<int>(type: "int", nullable: false),
                    WarningCount = table.Column<int>(type: "int", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportRun", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportRun_Unit_UnitId",
                        column: x => x.UnitId,
                        principalTable: "Unit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UnitReportConfig",
                columns: table => new
                {
                    UnitId = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    UnitLabel = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ExtraColumnsJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnitReportConfig", x => x.UnitId);
                    table.ForeignKey(
                        name: "FK_UnitReportConfig_Unit_UnitId",
                        column: x => x.UnitId,
                        principalTable: "Unit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssetFieldValue",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FieldDefinitionId = table.Column<int>(type: "int", nullable: false),
                    ValueText = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ValueNumber = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    ValueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetFieldValue", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssetFieldValue_Asset_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Asset",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssetFieldValue_FieldDefinition_FieldDefinitionId",
                        column: x => x.FieldDefinitionId,
                        principalTable: "FieldDefinition",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssetPreservation",
                columns: table => new
                {
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CurrentGate = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AssetAvailability = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ThirdPartyInspectionStatus = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    PreservationStatusAdnoc = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    PreservedByEu = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    PreservationRemarks = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    RemarksAdnoc = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    EmComments = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    EmWalkdownStatus = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ActionBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Response = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CurrentStatusAdnoc = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    OemName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    CurrentActivity = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    N2BlanketStatus = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    G1BulkLiquidDrained = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    G5PositiveMechIsolation = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    G6CleaningMethod = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    G1Status = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    G2Status = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    G3Status = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    G4Status = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    G5Status = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    G6Status = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    G7Status = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    G8Status = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    G1Comments = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    G2Comments = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    G3Comments = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    G4Comments = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    G5Comments = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    G6Comments = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    G7Comments = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    G8Comments = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    PressureBarg = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    G2DewPointC = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    G3O2Pct = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    G4N2BlanketBarg = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    G6H2S = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    G6LelPct = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    LastFieldActionDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PreservationDate = table.Column<DateOnly>(type: "date", nullable: true),
                    N2BlanketedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    LastReadingAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PreservedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PreservedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetPreservation", x => x.AssetId);
                    table.ForeignKey(
                        name: "FK_AssetPreservation_Asset_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Asset",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GateDocumentation",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Gate = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ReceivedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Reference = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GateDocumentation", x => x.Id);
                    table.CheckConstraint("CK_GateDocumentation_Gate", "[Gate] BETWEEN 1 AND 8");
                    table.ForeignKey(
                        name: "FK_GateDocumentation_Asset_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Asset",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PipelineDetail",
                columns: table => new
                {
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LengthM = table.Column<decimal>(type: "decimal(12,3)", precision: 12, scale: 3, nullable: true),
                    SizeIn = table.Column<decimal>(type: "decimal(8,3)", precision: 8, scale: 3, nullable: true),
                    PipeSpec = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineDetail", x => x.AssetId);
                    table.ForeignKey(
                        name: "FK_PipelineDetail_Asset_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Asset",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Reading",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MeasurementTypeId = table.Column<int>(type: "int", nullable: false),
                    Value = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    MeasuredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RecordedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Phase = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    LegacySharePointId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reading", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Reading_Asset_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Asset",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Reading_MeasurementType_MeasurementTypeId",
                        column: x => x.MeasurementTypeId,
                        principalTable: "MeasurementType",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssetGroupMember",
                columns: table => new
                {
                    GroupId = table.Column<int>(type: "int", nullable: false),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AddedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AddedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetGroupMember", x => new { x.GroupId, x.AssetId });
                    table.ForeignKey(
                        name: "FK_AssetGroupMember_AssetGroup_GroupId",
                        column: x => x.GroupId,
                        principalTable: "AssetGroup",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssetGroupMember_Asset_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Asset",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CategoryKeyword",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CategoryId = table.Column<int>(type: "int", nullable: false),
                    Keyword = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CategoryKeyword", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CategoryKeyword_DashboardCategory_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "DashboardCategory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ImportChange",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ImportRunId = table.Column<long>(type: "bigint", nullable: false),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AssetCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Field = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    BaseValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExcelValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CurrentValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Resolution = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ResolvedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ResolvedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolutionNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportChange", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportChange_ImportRun_ImportRunId",
                        column: x => x.ImportRunId,
                        principalTable: "ImportRun",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppUser_Email",
                table: "AppUser",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppUser_RoleId",
                table: "AppUser",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_Asset_Code",
                table: "Asset",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Asset_DisciplineId",
                table: "Asset",
                column: "DisciplineId");

            migrationBuilder.CreateIndex(
                name: "IX_Asset_Tag",
                table: "Asset",
                column: "Tag");

            migrationBuilder.CreateIndex(
                name: "IX_Asset_UnitId_Type_IsActive",
                table: "Asset",
                columns: new[] { "UnitId", "Type", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_AssetFieldValue_AssetId_FieldDefinitionId",
                table: "AssetFieldValue",
                columns: new[] { "AssetId", "FieldDefinitionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssetFieldValue_FieldDefinitionId_ValueText",
                table: "AssetFieldValue",
                columns: new[] { "FieldDefinitionId", "ValueText" });

            migrationBuilder.CreateIndex(
                name: "IX_AssetGroup_Code",
                table: "AssetGroup",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssetGroup_GroupTypeId",
                table: "AssetGroup",
                column: "GroupTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetGroup_UnitId",
                table: "AssetGroup",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetGroupMember_AssetId",
                table: "AssetGroupMember",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetPreservation_CurrentGate",
                table: "AssetPreservation",
                column: "CurrentGate");

            migrationBuilder.CreateIndex(
                name: "IX_AssetPreservation_G1Status",
                table: "AssetPreservation",
                column: "G1Status");

            migrationBuilder.CreateIndex(
                name: "IX_AssetPreservation_G2Status",
                table: "AssetPreservation",
                column: "G2Status");

            migrationBuilder.CreateIndex(
                name: "IX_AssetPreservation_G3Status",
                table: "AssetPreservation",
                column: "G3Status");

            migrationBuilder.CreateIndex(
                name: "IX_AssetPreservation_G4Status",
                table: "AssetPreservation",
                column: "G4Status");

            migrationBuilder.CreateIndex(
                name: "IX_AssetPreservation_G5Status",
                table: "AssetPreservation",
                column: "G5Status");

            migrationBuilder.CreateIndex(
                name: "IX_AssetPreservation_G6Status",
                table: "AssetPreservation",
                column: "G6Status");

            migrationBuilder.CreateIndex(
                name: "IX_AssetPreservation_G7Status",
                table: "AssetPreservation",
                column: "G7Status");

            migrationBuilder.CreateIndex(
                name: "IX_AssetPreservation_G8Status",
                table: "AssetPreservation",
                column: "G8Status");

            migrationBuilder.CreateIndex(
                name: "IX_CategoryKeyword_CategoryId",
                table: "CategoryKeyword",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_ChangeLog_AssetId_ChangedAtUtc",
                table: "ChangeLog",
                columns: new[] { "AssetId", "ChangedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ChangeLog_BatchId",
                table: "ChangeLog",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_ChangeLog_ChangedAtUtc",
                table: "ChangeLog",
                column: "ChangedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DashboardCategory_UnitId_Label",
                table: "DashboardCategory",
                columns: new[] { "UnitId", "Label" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Discipline_Name",
                table: "Discipline",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExcelSession_UnitId",
                table: "ExcelSession",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_FieldDefinition_AssetType_Key",
                table: "FieldDefinition",
                columns: new[] { "AssetType", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FieldDefinition_OptionSetId",
                table: "FieldDefinition",
                column: "OptionSetId");

            migrationBuilder.CreateIndex(
                name: "IX_GateDocumentation_AssetId_Gate",
                table: "GateDocumentation",
                columns: new[] { "AssetId", "Gate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupType_Name",
                table: "GroupType",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImportChange_AssetId",
                table: "ImportChange",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportChange_ImportRunId",
                table: "ImportChange",
                column: "ImportRunId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportChange_Resolution_ImportRunId",
                table: "ImportChange",
                columns: new[] { "Resolution", "ImportRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_ImportRun_UnitId",
                table: "ImportRun",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportRun_UploadedAtUtc",
                table: "ImportRun",
                column: "UploadedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_LoginAudit_AtUtc",
                table: "LoginAudit",
                column: "AtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MeasurementType_Key",
                table: "MeasurementType",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OptionSet_Key",
                table: "OptionSet",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OptionValue_OptionSetId_Value",
                table: "OptionValue",
                columns: new[] { "OptionSetId", "Value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PipelineDetail_PipeSpec",
                table: "PipelineDetail",
                column: "PipeSpec");

            migrationBuilder.CreateIndex(
                name: "IX_Plant_Name",
                table: "Plant",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reading_AssetId_MeasuredAtUtc",
                table: "Reading",
                columns: new[] { "AssetId", "MeasuredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Reading_MeasurementTypeId_MeasuredAtUtc",
                table: "Reading",
                columns: new[] { "MeasurementTypeId", "MeasuredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Role_Name",
                table: "Role",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SimproBeforeImage_RunId",
                table: "SimproBeforeImage",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_SimproSyncItem_AssetId",
                table: "SimproSyncItem",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_SimproSyncItem_RunId",
                table: "SimproSyncItem",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_SimproSyncRun_StartedAtUtc",
                table: "SimproSyncRun",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Unit_Name",
                table: "Unit",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Unit_PlantId",
                table: "Unit",
                column: "PlantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppSetting");

            migrationBuilder.DropTable(
                name: "AppUser");

            migrationBuilder.DropTable(
                name: "AssetFieldValue");

            migrationBuilder.DropTable(
                name: "AssetGroupMember");

            migrationBuilder.DropTable(
                name: "AssetPreservation");

            migrationBuilder.DropTable(
                name: "CategoryKeyword");

            migrationBuilder.DropTable(
                name: "ChangeLog");

            migrationBuilder.DropTable(
                name: "EquipmentIdSequence");

            migrationBuilder.DropTable(
                name: "ExcelSession");

            migrationBuilder.DropTable(
                name: "GateDocumentation");

            migrationBuilder.DropTable(
                name: "ImportChange");

            migrationBuilder.DropTable(
                name: "LoginAudit");

            migrationBuilder.DropTable(
                name: "OptionValue");

            migrationBuilder.DropTable(
                name: "PipelineDetail");

            migrationBuilder.DropTable(
                name: "Reading");

            migrationBuilder.DropTable(
                name: "RolePermission");

            migrationBuilder.DropTable(
                name: "SimproBeforeImage");

            migrationBuilder.DropTable(
                name: "SimproSyncItem");

            migrationBuilder.DropTable(
                name: "UnitReportConfig");

            migrationBuilder.DropTable(
                name: "FieldDefinition");

            migrationBuilder.DropTable(
                name: "AssetGroup");

            migrationBuilder.DropTable(
                name: "DashboardCategory");

            migrationBuilder.DropTable(
                name: "ImportRun");

            migrationBuilder.DropTable(
                name: "Asset");

            migrationBuilder.DropTable(
                name: "MeasurementType");

            migrationBuilder.DropTable(
                name: "Role");

            migrationBuilder.DropTable(
                name: "SimproSyncRun");

            migrationBuilder.DropTable(
                name: "OptionSet");

            migrationBuilder.DropTable(
                name: "GroupType");

            migrationBuilder.DropTable(
                name: "Discipline");

            migrationBuilder.DropTable(
                name: "Unit");

            migrationBuilder.DropTable(
                name: "Plant");
        }
    }
}
