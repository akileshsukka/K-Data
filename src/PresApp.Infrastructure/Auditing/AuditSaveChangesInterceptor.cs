using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PresApp.Domain.Common;

namespace PresApp.Infrastructure.Auditing;

/// <summary>
/// On every save: stamps Created/Updated columns and writes one <see cref="ChangeLog"/> row per
/// changed field of <see cref="IFieldAudited"/> entities, in the same transaction as the change.
/// </summary>
public sealed class AuditSaveChangesInterceptor(IAuditContext audit, TimeProvider clock) : SaveChangesInterceptor
{
    private static readonly HashSet<string> Ignored =
    [
        nameof(AuditableEntity.CreatedAtUtc), nameof(AuditableEntity.CreatedBy),
        nameof(AuditableEntity.UpdatedAtUtc), nameof(AuditableEntity.UpdatedBy),
        nameof(AuditableEntity.RowVersion)
    ];

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is not null) Apply(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null) Apply(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Apply(DbContext context)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var isSqlServer = context.Database.IsSqlServer();
        var logs = new List<ChangeLog>();

        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is AuditableEntity auditable)
            {
                if (entry.State == EntityState.Added)
                {
                    auditable.CreatedAtUtc = now;
                    auditable.CreatedBy = audit.UserName;
                }
                if (entry.State is EntityState.Added or EntityState.Modified)
                {
                    auditable.UpdatedAtUtc = now;
                    auditable.UpdatedBy = audit.UserName;
                    // SQL Server generates rowversion itself; other providers (tests) get a fresh token.
                    if (!isSqlServer) auditable.RowVersion = Guid.NewGuid().ToByteArray();
                }
            }

            if (entry.Entity is IFieldAudited audited && !audit.SuppressFieldAudit)
            {
                logs.AddRange(Describe(entry, audited, now));
            }
        }

        if (logs.Count > 0) context.Set<ChangeLog>().AddRange(logs);
    }

    private IEnumerable<ChangeLog> Describe(EntityEntry entry, IFieldAudited audited, DateTime now)
    {
        var entityType = entry.Metadata.ClrType.Name;

        if (entry.State == EntityState.Added)
        {
            if (entry.Entity is Domain.Assets.Asset)
            {
                // The asset itself: one "created" row. Its satellites (preservation, documents…) log their values.
                yield return NewLog(entry, audited, entityType, "(created)", null, Format(entry.Property("Code").CurrentValue), now);
                yield break;
            }

            foreach (var property in entry.Properties)
            {
                var meta = property.Metadata;
                if (Ignored.Contains(meta.Name) || meta.IsShadowProperty() || meta.IsPrimaryKey() || meta.IsForeignKey()) continue;
                var value = Format(property.CurrentValue);
                if (string.IsNullOrEmpty(value) || IsDefault(property.CurrentValue)) continue;
                yield return NewLog(entry, audited, entityType, FieldName(entry, meta.Name), null, value, now);
            }
            yield break;
        }

        if (entry.State != EntityState.Modified) yield break;

        foreach (var property in entry.Properties)
        {
            var name = property.Metadata.Name;
            if (!property.IsModified || Ignored.Contains(name) || property.Metadata.IsShadowProperty()) continue;

            var oldValue = Format(property.OriginalValue);
            var newValue = Format(property.CurrentValue);
            if (oldValue == newValue) continue;

            yield return NewLog(entry, audited, entityType, FieldName(entry, name), oldValue, newValue, now);
        }
    }

    /// <summary>A custom field value is logged as "cf:{field id}" whatever column holds it (text, number or date).</summary>
    private static string FieldName(EntityEntry entry, string property) =>
        entry.Entity is Domain.Assets.AssetFieldValue v && property.StartsWith("Value", StringComparison.Ordinal) ? $"cf:{v.FieldDefinitionId}" : property;

    private ChangeLog NewLog(EntityEntry entry, IFieldAudited audited, string entityType, string field,
        string? oldValue, string? newValue, DateTime now) => new()
    {
        EntityType = entityType,
        EntityKey = KeyOf(entry),
        AssetId = audited.AuditAssetId,
        Field = field,
        OldValue = oldValue,
        NewValue = newValue,
        ChangedAtUtc = now,
        ChangedBy = audit.UserName,
        Source = audit.Source,
        BatchId = audit.BatchId
    };

    private static string KeyOf(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key is null) return "";
        return string.Join("|", key.Properties.Select(p => Format(entry.Property(p.Name).CurrentValue)));
    }

    private static bool IsDefault(object? value) => value is Enum e && Convert.ToInt32(e, CultureInfo.InvariantCulture) == 0;

    internal static string? Format(object? value) => value switch
    {
        null => null,
        string s => s,
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };
}
