using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PresApp.Api.Auth;
using PresApp.Api.Hosting;
using PresApp.Domain.Assets;
using PresApp.Domain.Security;
using PresApp.Infrastructure.Data;

namespace PresApp.Api.Assets;

/// <summary>
/// E7 electrical test of an asset, entered in the app (usually by the engineer in the office from the field sheet).
/// Partial tests can be saved: the app only reports which required values are still missing.
/// </summary>
public sealed class ElectricalTestService(PresAppDbContext db, AssetQueries queries)
{
    public async Task<AssetProfile> SaveAsync(Guid id, SaveElectricalTestRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var asset = await queries.LoadAsync(id, tracking: true, ct);
        if (!asset.IsActive && !user.HasPermission(Permissions.AssetsManage))
            throw ApiProblem.Forbidden("The asset is inactive. Only an administrator can edit it.");
        if (asset.Type == AssetType.Pipeline) throw ApiProblem.Invalid("Lines have no electrical test.");

        var test = asset.ElectricalTest;
        if (test is not null && request.Expected is { } expected && Math.Abs((test.UpdatedAtUtc - expected).TotalMilliseconds) >= 1)
            throw ApiProblem.Conflict($"The electrical test was saved by {test.UpdatedBy} while you were editing. Nothing was saved; check the values and try again.");
        if (test is not null && request.Expected is null)
            throw ApiProblem.Conflict("Someone else started the electrical test of this asset while you were editing. Check the values and try again.");

        var errors = new Dictionary<string, string[]>();
        foreach (var key in request.Values?.Keys ?? [])
        {
            if (ElectricalTestSpec.Find(key) is null) errors[key] = ["Unknown value of the electrical test."];
            else if (request.Values![key] is < 0) errors[key] = [$"{ElectricalTestSpec.Find(key)!.Label} cannot be negative."];
        }
        if (errors.Count > 0) throw ApiProblem.Invalid(errors.Values.First()[0], errors);

        if (test is null)
        {
            test = new ElectricalTest { AssetId = asset.Id };
            asset.ElectricalTest = test;
            db.ElectricalTests.Add(test);
        }
        test.Required = request.Required;
        test.TestedOn = request.TestedOn;
        foreach (var (key, value) in request.Values ?? new Dictionary<string, decimal?>())
            ElectricalTestSpec.Set(test, ElectricalTestSpec.Find(key)!.Key, value);

        await db.SaveChangesAsync(ct);
        return await queries.ProfileAsync(id, ct);
    }
}
