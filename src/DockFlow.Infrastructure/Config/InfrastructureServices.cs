using DockFlow.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace DockFlow.Infrastructure;

internal sealed class SchemaContext(DbContextOptions<SchemaContext> options) : DbContext(options);

public static class InfrastructureServices
{
    private sealed record DemoProvisioning(bool Enabled);

    public static IServiceCollection AddDockInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DockFlow");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("ConnectionStrings:DockFlow is required.");
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        // Bound the default so concurrent requests queue rather than exhaust PostgreSQL's client slots.
        if (!settings.ShouldSerialize("Maximum Pool Size")) settings.MaxPoolSize = 32;
        var pooledConnectionString = settings.ConnectionString;
        services.AddSingleton(_ => NpgsqlDataSource.Create(pooledConnectionString));
        services.AddDbContext<SchemaContext>((sp, options) => options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton(new DemoProvisioning(bool.TryParse(configuration["DockFlow:SeedDemo"], out var seed) && seed));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IDockOperations, PostgresDockOperations>();
        services.AddSingleton<ICompletedUnloadingsReader, PostgresCompletedUnloadingsReader>();
        return services;
    }

    public static async Task MigrateAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SchemaContext>().Database.MigrateAsync(cancellationToken);
        if (!scope.ServiceProvider.GetRequiredService<DemoProvisioning>().Enabled) return;

        await using var connection = await scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO public.tenants(id) VALUES ('11111111-1111-1111-1111-111111111111'),
                ('22222222-2222-2222-2222-222222222222') ON CONFLICT DO NOTHING;
            INSERT INTO public.carriers(tenant_id,id) VALUES
                ('11111111-1111-1111-1111-111111111111','aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'),
                ('11111111-1111-1111-1111-111111111111','bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'),
                ('22222222-2222-2222-2222-222222222222','cccccccc-cccc-cccc-cccc-cccccccccccc') ON CONFLICT DO NOTHING;
            INSERT INTO public.docks(id,tenant_id,code,cargo,max_weight_kg) VALUES
                ('11111111-1111-1111-1111-111111111101','11111111-1111-1111-1111-111111111111','G-01','General',12000),
                ('11111111-1111-1111-1111-111111111102','11111111-1111-1111-1111-111111111111','G-02','General',30000),
                ('11111111-1111-1111-1111-111111111103','11111111-1111-1111-1111-111111111111','R-01','Refrigerated',20000),
                ('22222222-2222-2222-2222-222222222201','22222222-2222-2222-2222-222222222222','G-01','General',20000)
                ON CONFLICT DO NOTHING;
            """, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
