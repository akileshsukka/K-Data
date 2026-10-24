using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PresApp.Infrastructure.Auditing;
using PresApp.Infrastructure.Data;
using PresApp.Infrastructure.Seeding;

namespace PresApp.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPresAppData(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<AuditContext>();
        services.AddScoped<IAuditContext>(sp => sp.GetRequiredService<AuditContext>());
        services.AddScoped<AuditSaveChangesInterceptor>();
        services.AddDbContext<PresAppDbContext>((sp, options) =>
        {
            options.UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsHistoryTable("__EFMigrationsHistory");
            });
            options.AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>());
        });
        services.AddScoped<DatabaseSeeder>();
        return services;
    }
}
