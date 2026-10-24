using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PresApp.Domain.Assets;
using PresApp.Domain.Catalog;
using PresApp.Infrastructure.Auditing;
using PresApp.Infrastructure.Data;
using PresApp.Infrastructure.Seeding;
using PresApp.Tests.Support;

namespace PresApp.Tests;

public sealed class DataTests : IClassFixture<TestApp>
{
    private readonly TestApp app;
    public DataTests(TestApp app) => this.app = app;

    [Fact]
    public async Task Seed_has_no_NA_and_no_documentation_pending_option()
    {
        var values = await app.WithDbAsync(db => db.OptionValues.Select(v => v.Value).ToListAsync());

        Assert.DoesNotContain(values, v => v == "N/A");
        Assert.DoesNotContain(values, v => v.Contains("Documentation Pending", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Not Applicable", values);
    }

    [Fact]
    public async Task Seeding_twice_adds_nothing()
    {
        int Count(PresAppDbContext db) => db.OptionValues.Count() + db.Roles.Count() + db.Units.Count() + db.MeasurementTypes.Count();
        var before = await app.WithDbAsync(db => Task.FromResult(Count(db)));

        await using (var scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync(TestApp.AdminEmail);
        }

        Assert.Equal(before, await app.WithDbAsync(db => Task.FromResult(Count(db))));
    }

    [Fact]
    public async Task Units_carry_Simpro_location_ids()
    {
        var unit = await app.WithDbAsync(db => db.Units.Include(u => u.Plant).FirstAsync(u => u.Name == "Unit 541"));
        Assert.Equal("Habshan - 5", unit.Plant.Name);
        Assert.Equal(Guid.Parse("8AAE2772-9EA8-4C38-B3F4-194067D96008"), unit.SimproLocationId);
    }

    [Fact]
    public async Task Field_changes_are_written_to_the_change_log()
    {
        var assetId = await CreateAssetAsync("ST-EMP-039-901");

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var audit = scope.ServiceProvider.GetRequiredService<AuditContext>();
            audit.UserName = "engineer@enermech.com";
            var db = scope.ServiceProvider.GetRequiredService<PresAppDbContext>();
            var p = await db.Preservations.FirstAsync(x => x.AssetId == assetId);
            p.G1Status = "Completed";
            p.RemarksAdnoc = "Checked on site";
            await db.SaveChangesAsync();
        }

        var logs = await app.WithDbAsync(db => db.ChangeLogs.Where(l => l.AssetId == assetId && l.EntityType == "AssetPreservation"
            && l.OldValue == null && (l.Field == "G1Status" || l.Field == "RemarksAdnoc")).ToListAsync());
        Assert.Equal(2, logs.Count);
        Assert.All(logs, l => Assert.Equal("engineer@enermech.com", l.ChangedBy));
        Assert.Contains(logs, l => l.Field == "G1Status" && l.NewValue == "Completed");
    }

    [Fact]
    public async Task Stale_save_is_rejected_instead_of_overwriting()
    {
        var assetId = await CreateAssetAsync("ST-EMP-039-902");

        await using var scopeA = app.Services.CreateAsyncScope();
        await using var scopeB = app.Services.CreateAsyncScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<PresAppDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<PresAppDbContext>();
        var a = await dbA.Preservations.FirstAsync(x => x.AssetId == assetId);
        var b = await dbB.Preservations.FirstAsync(x => x.AssetId == assetId);

        a.G2Status = "Ongoing";
        await dbA.SaveChangesAsync();
        b.G2Status = "Hold";

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());
    }

    [Theory]
    [InlineData("ST", 39, 1, "ST-EMP-039-001")]
    [InlineData("el", 541, 12, "EL-EMP-541-012")]
    [InlineData("EI", 41, 1234, "EI-EMP-041-1234")]
    public void Equipment_ids_follow_the_agreed_pattern(string prefix, int unit, int number, string expected) =>
        Assert.Equal(expected, EquipmentIdFormat.Build(prefix, unit, number));

    [Fact]
    public void Highest_completed_gate_ignores_gaps_and_other_statuses()
    {
        var p = new AssetPreservation { G1Status = "Completed", G2Status = "Ongoing", G5Status = " completed " , G6Status = "Not Applicable" };
        Assert.Equal(5, p.HighestCompletedGate());
        Assert.Null(new AssetPreservation { G1Status = "Hold" }.HighestCompletedGate());
    }

    private Task<Guid> CreateAssetAsync(string code) => app.WithDbAsync(async db =>
    {
        var unit = await db.Units.FirstAsync(u => u.Name == "Unit 39");
        var discipline = await db.Disciplines.FirstAsync(d => d.Name == "Static");
        var asset = new Asset
        {
            Id = Guid.NewGuid(), Type = AssetType.Equipment, Code = code, Tag = "39-V-" + code[^3..],
            UnitId = unit.Id, DisciplineId = discipline.Id, Preservation = new AssetPreservation()
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        return asset.Id;
    });
}
