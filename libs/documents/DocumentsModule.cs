using Freight.Shared;
using Freight.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Freight.Documents;

public static class DocumentsModule
{
    public static IServiceCollection AddDocumentsModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Freight") ?? throw new InvalidOperationException("Connection string 'Freight' is not configured.");
        services.AddDbContext<DocumentsDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", DocumentsDbContext.SchemaName)));
        services.AddScoped<DocumentsHandler>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<DocumentsDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<DocumentsDbContext>>("documents-db", tags: ["ready"]);

        messaging.AddOutbox<DocumentsDbContext>()
            .AddConsumer<DocumentsHandler>(Topology.DocumentsQueue, [Topology.HeldKey], prefetch: 4);
        return services;
    }
}
