using DockFlow.Application;
using DockFlow.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace DockFlow.CoreTests;

public sealed class SchedulingTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private ServiceProvider services = null!;
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly Actor A = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), "driver-a");
    private static readonly Actor B = A with { CarrierId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), Subject = "driver-b" };
    private static readonly Actor Operator = A with { CarrierId = null, Subject = "operator" };
    private static readonly DateTimeOffset At = new(2035, 1, 1, 10, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        await pg.StartAsync();
        await using (var conn = new NpgsqlConnection(pg.GetConnectionString()))
        {
            await conn.OpenAsync();
            await using var command = new NpgsqlCommand("""
                CREATE ROLE dockflow;
                ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO dockflow;
                """, conn);
            await command.ExecuteNonQueryAsync();
        }
        await using (var unseeded = BuildProvider(false))
        {
            await InfrastructureServices.MigrateAsync(unseeded, Ct);
            Assert.Equal(0L, await Count("SELECT count(*) FROM public.tenants"));
        }
        services = BuildProvider(true);
        await InfrastructureServices.MigrateAsync(services, Ct);
    }

    private ServiceProvider BuildProvider(bool seed) => new ServiceCollection().AddDockInfrastructure(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DockFlow"] = pg.GetConnectionString(), ["DockFlow:SeedDemo"] = seed.ToString()
        }).Build()).BuildServiceProvider();

    public async Task DisposeAsync()
    {
        if (services is not null) await services.DisposeAsync();
        await pg.DisposeAsync();
    }

    private IDockOperations Ops => services.GetRequiredService<IDockOperations>();
    private async Task<Guid> Dock() => (await Ops.BoardAsync(Operator, Ct)).Docks.First(d => d.Cargo == CargoKind.General).Id;
    private static Book Booking(Guid? dock = null, int hour = 0) => new("ABC1234", CargoKind.General, 1000, At.AddHours(hour), At.AddHours(hour + 1), dock);
    private static async Task Reject(int status, Func<Task> run) => Assert.Equal(status, (await Assert.ThrowsAsync<OperationRejected>(run)).Status);

    [Fact]
    public async Task OneOfOneHundredWinsAndAdjacentWindowIsFree()
    {
        var dock = await Dock();
        var attempts = await Task.WhenAll(Enumerable.Range(0, 100).Select(async i =>
        {
            try { return await Ops.BookAsync(A, Booking(dock), $"race-{i}", Ct); }
            catch (OperationRejected e) when (e.Status == 409 && e.Code == "reservation_overlap") { return null; }
        }));
        Assert.Single(attempts.OfType<Visit>());
        Assert.Equal(dock, (await Ops.BookAsync(B, Booking(dock, 1), "adjacent", Ct)).DockId);
        Assert.Equal(2L, await Count("SELECT count(*) FROM public.outbox WHERE kind='AppointmentScheduled'"));
        Assert.Equal(2L, await Count("SELECT count(*) FROM public.audit_log WHERE kind='AppointmentScheduled'"));
    }

    [Fact]
    public async Task IdempotencyScopesAndReplaysOriginalResultAcrossStateChanges()
    {
        var dock = await Dock();
        var input = Booking(dock) with { Start = At.ToOffset(TimeSpan.FromHours(-3)).AddSeconds(40) };
        var results = await Task.WhenAll(Enumerable.Range(0, 15).Select(_ => Ops.BookAsync(A, input, "same-key", Ct)));
        var visit = results[0];
        Assert.All(results, v => Assert.Equal(visit, v));
        Assert.Equal(At, visit.Start);
        Assert.Equal(TimeSpan.Zero, visit.Start.Offset);
        Assert.Null(await Ops.FindAsync(B, visit.Id, Ct));
        Assert.Empty((await Ops.BoardAsync(B, Ct)).Visits);
        await Reject(404, () => Ops.TransitionAsync(B, visit.Id, VisitAction.Cancel, visit.Version, Ct));
        await Reject(403, () => Ops.TransitionAsync(A, visit.Id, VisitAction.CheckIn, visit.Version, Ct));
        await Reject(409, () => Ops.BookAsync(A, input with { WeightKg = 1001 }, "same-key", Ct));
        await Ops.TransitionAsync(A, visit.Id, VisitAction.Cancel, visit.Version, Ct);
        Assert.Equal(visit, await Ops.BookAsync(A, input, "same-key", Ct));
        Assert.NotEqual(visit.Id, (await Ops.BookAsync(B, input, "same-key", Ct)).Id);
    }

    [Fact]
    public async Task TenantAndCarrierMembershipAreEnforced()
    {
        var c = new Actor(Guid.Parse("22222222-2222-2222-2222-222222222222"), Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), "driver-c");
        var dock = await Dock();
        var visit = await Ops.BookAsync(A, Booking(dock), "a", Ct);
        Assert.Null(await Ops.FindAsync(c, visit.Id, Ct));
        await Reject(404, () => Ops.BookAsync(c, Booking(dock), "wrong-dock", Ct));
        await Reject(403, () => Ops.BoardAsync(c with { CarrierId = A.CarrierId }, Ct));
        Assert.NotEqual(dock, (await Ops.BookAsync(c, Booking(), "c", Ct)).DockId);
    }

    [Fact]
    public async Task OccupancyOutlivesReservationAndReassignmentCannotMoveIt()
    {
        var dock = await Dock();
        var first = await Ops.BookAsync(A, Booking(dock, -100000), "first", Ct);
        var next = await Ops.BookAsync(B, Booking(dock, -99999), "next", Ct);
        first = await Ops.TransitionAsync(Operator, first.Id, VisitAction.CheckIn, first.Version, Ct);
        next = await Ops.TransitionAsync(Operator, next.Id, VisitAction.CheckIn, next.Version, Ct);
        var stale = await Assert.ThrowsAsync<OperationRejected>(() => Ops.TransitionAsync(Operator, first.Id, VisitAction.Assign, first.Version - 1, Ct));
        Assert.Equal((409, "stale_version"), (stale.Status, stale.Code));
        Assert.Equal(first, await Ops.FindAsync(A, first.Id, Ct));
        first = await Ops.TransitionAsync(Operator, first.Id, VisitAction.Assign, first.Version, Ct);
        var occupied = await Assert.ThrowsAsync<OperationRejected>(() => Ops.TransitionAsync(Operator, next.Id, VisitAction.Assign, next.Version, Ct));
        Assert.Equal((409, "dock_occupied"), (occupied.Status, occupied.Code));
        await Ops.BlockAsync(Operator, dock, "maintenance", Ct);
        await Reject(409, () => Ops.ReassignAsync(Operator, first.Id, first.Version, Ct));
        Assert.Equal(dock, (await Ops.FindAsync(B, next.Id, Ct))!.DockId);
        var moved = await Ops.ReassignAsync(Operator, next.Id, next.Version, Ct);
        Assert.NotEqual(dock, moved.DockId);
        first = await Ops.TransitionAsync(Operator, first.Id, VisitAction.Start, first.Version, Ct);
        Assert.Equal(VisitStatus.Completed, (await Ops.TransitionAsync(Operator, first.Id, VisitAction.Complete, first.Version, Ct)).Status);
    }

    [Fact]
    public async Task MaintenanceSerializesWithBookingAtDecisiveDock()
    {
        var dock = await Dock();
        await using var conn = new NpgsqlConnection(pg.GetConnectionString());
        await conn.OpenAsync();
        await using var hold = await conn.BeginTransactionAsync();
        await using var update = new NpgsqlCommand("UPDATE public.docks SET operational=false WHERE id=@dock", conn, hold);
        update.Parameters.AddWithValue("dock", dock);
        await update.ExecuteNonQueryAsync();

        // A competing booking must wait for the uncommitted operational flag to become visible.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var booking = Ops.BookAsync(A, Booking(dock), "maintenance-race", timeout.Token);
        try
        {
            while (await Count("SELECT count(*) FROM pg_stat_activity WHERE wait_event_type='Lock' AND query LIKE '%FOR NO KEY UPDATE'") == 0)
            {
                Assert.False(booking.IsCompleted, "Booking completed without waiting on the dock lock.");
                await Task.Delay(20, timeout.Token);
            }
        }
        finally { await hold.CommitAsync(); }
        Assert.Equal("dock_unavailable", (await Assert.ThrowsAsync<OperationRejected>(() => booking)).Code);
        Assert.Equal(0L, await Count("SELECT count(*) FROM public.outbox"));
    }

    [Fact]
    public async Task IncidentProjectionScopesCarrierAndDeduplicatesNullVisit()
    {
        var visit = await Ops.BookAsync(A, Booking(), "incident", Ct);
        await Ops.BlockAsync(Operator, visit.DockId, "maintenance", Ct);
        await using var conn = new NpgsqlConnection(pg.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO public.incidents(id,tenant_id,dock_id,visit_id,kind,detected_at,status)
            VALUES (gen_random_uuid(),@tenant,@dock,@visit,'Maintenance',now(),'Open'),
                (gen_random_uuid(),@tenant,@dock,NULL,'Inspection',now(),'Open'),
                (gen_random_uuid(),@tenant,@dock,NULL,'Inspection',now(),'Open')
            ON CONFLICT (tenant_id,dock_id,visit_id,kind) DO NOTHING
            """, conn);
        cmd.Parameters.AddWithValue("tenant", A.TenantId);
        cmd.Parameters.AddWithValue("dock", visit.DockId);
        cmd.Parameters.AddWithValue("visit", visit.Id);
        Assert.Equal(2, await cmd.ExecuteNonQueryAsync());
        Assert.Equal(2, (await Ops.BoardAsync(Operator, Ct)).Incidents.Count);
        Assert.Equal(visit.Id, Assert.Single((await Ops.BoardAsync(A, Ct)).Incidents).VisitId);
        Assert.Empty((await Ops.BoardAsync(B, Ct)).Incidents);
        Assert.Equal(1L, await Count("SELECT count(*) FROM public.outbox o JOIN public.docks d ON d.id=o.aggregate_id WHERE o.kind='DockUnavailable' AND NOT d.operational"));
    }

    [Fact]
    public async Task AutomaticAllocationPrefersGeneralAndSmallestCompatibleDocks()
    {
        var board = await Ops.BoardAsync(Operator, Ct);
        var first = await Ops.BookAsync(A, Booking(), "first", Ct);
        var second = await Ops.BookAsync(A, Booking(), "second", Ct);
        var chilled = await Ops.BookAsync(A, Booking() with { Cargo = CargoKind.Refrigerated }, "cold", Ct);
        Assert.Equal(board.Docks.Where(d => d.Cargo == CargoKind.General).MinBy(d => d.MaxWeightKg)!.Id, first.DockId);
        Assert.Equal(board.Docks.Where(d => d.Cargo == CargoKind.General).MaxBy(d => d.MaxWeightKg)!.Id, second.DockId);
        Assert.Equal(Assert.Single(board.Docks, d => d.Cargo == CargoKind.Refrigerated).Id, chilled.DockId);
    }

    [Fact]
    public async Task RuntimeCanAppendAuditButCannotModifyHistory()
    {
        Assert.Equal(1L, await Count("""
            SELECT count(*) WHERE has_table_privilege('dockflow','public.audit_log','INSERT')
                AND NOT has_table_privilege('dockflow','public.audit_log','UPDATE')
                AND NOT has_table_privilege('dockflow','public.audit_log','DELETE')
                AND NOT has_table_privilege('dockflow','public.audit_log','TRUNCATE')
            """));
    }

    private async Task<long> Count(string sql)
    {
        await using var conn = new NpgsqlConnection(pg.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
