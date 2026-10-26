using Freight.Shared;
using Freight.Shared.Contracts;
using Freight.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Freight.Capacity;

public static class CapacityModule
{
    public static IServiceCollection AddCapacityModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Freight") ?? throw new InvalidOperationException("Connection string 'Freight' is not configured.");
        services.AddDbContext<CapacityDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", CapacityDbContext.SchemaName)));
        services.AddScoped<CapacityHandler>();
        services.AddScoped<ISailingCatalog, SailingCatalog>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<CapacityDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<CapacityDbContext>>("capacity-db", tags: ["ready"]);

        messaging.AddOutbox<CapacityDbContext>()
            .AddConsumer<CapacityHandler>(Topology.CapacityQueue, [Topology.ReserveKey, Topology.ReleaseKey], prefetch: 1);
        return services;
    }
}
