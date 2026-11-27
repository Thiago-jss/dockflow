using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using DockFlow.Application;
using DockFlow.Domain;
using Npgsql;
using NpgsqlTypes;

namespace DockFlow.Infrastructure;

internal sealed class PostgresDockOperations(NpgsqlDataSource source, TimeProvider clock) : IDockOperations
{
    private const string VisitColumns = "v.id,v.dock_id,d.code,v.vehicle,v.cargo,v.weight_kg,v.starts_at,v.ends_at,v.status,v.version";
    private const string VisitJoin = " FROM public.visits v JOIN public.docks d ON (d.tenant_id,d.id)=(v.tenant_id,v.dock_id) ";
    private const string Scope = "v.tenant_id=@tenant AND (@carrier IS NULL OR v.carrier_id=@carrier)";

    public Task<Board> BoardAsync(Actor a, CancellationToken ct) => Run(a, async db =>
    {
        var visits = await db.List("SELECT " + VisitColumns + VisitJoin + " WHERE " + Scope + " ORDER BY v.starts_at,v.id", ReadVisit);
        var docks = await db.List("""
            SELECT id,code,cargo,max_weight_kg,operational FROM public.docks WHERE tenant_id=@tenant ORDER BY code,id
            """, ReadDock);
        var incidents = await db.List("""
            SELECT i.id,i.dock_id,i.visit_id,i.kind,i.detected_at,i.status
            FROM public.incidents i WHERE i.tenant_id=@tenant AND (@carrier IS NULL OR EXISTS (
                SELECT FROM public.visits v WHERE v.tenant_id=i.tenant_id AND v.id=i.visit_id AND v.carrier_id=@carrier
            )) ORDER BY i.detected_at,i.id
            """, r => new IncidentView(r.GetGuid(0), r.GetGuid(1), r.IsDBNull(2) ? null : r.GetGuid(2),
                r.GetString(3), r.GetFieldValue<DateTimeOffset>(4), r.GetString(5)));
        return new Board(docks, visits, incidents);
    }, ct, IsolationLevel.RepeatableRead);

    public Task<Visit?> FindAsync(Actor a, Guid id, CancellationToken ct) =>
        Run(a, db => LoadVisit(db, id, false), ct);

    public Task<Visit> BookAsync(Actor a, Book input, string requestKey, CancellationToken ct)
    {
        var window = ReservationWindow.Normalize(input.Start, input.End);
        if (window is null || window.Value.Start == DateTimeOffset.MinValue || input.WeightKg <= 0 || !Enum.IsDefined(input.Cargo) ||
            !ValidText(input.Vehicle, 64) || !ValidText(requestKey, 200)) throw new OperationRejected("invalid_booking", 400);
        var normalized = input with { Vehicle = input.Vehicle.Trim(), Start = window.Value.Start, End = window.Value.End };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized)));
        return Run(a, async db =>
        {
            // The lock protects the absence of a key as well as replay. Hash collisions only serialize unrelated requests.
            var identity = JsonSerializer.Serialize(new { a.TenantId, a.CarrierId, a.Subject, requestKey });
            await db.Execute("SELECT pg_advisory_xact_lock(hashtextextended(@identity, 542607101))", ("identity", identity));
            var request = (await db.List("""
                SELECT fingerprint,response::text FROM public.booking_requests WHERE tenant_id=@tenant
                    AND carrier_id IS NOT DISTINCT FROM @carrier AND subject=@subject AND request_key=@key
                """, r => (Fingerprint: r.GetString(0), Response: r.GetString(1)), ("key", requestKey))).SingleOrDefault();
            if (request.Response is not null)
            {
                if (request.Fingerprint != fingerprint) throw Conflict("idempotency_mismatch");
                return JsonSerializer.Deserialize<Visit>(request.Response)!;
            }

            var dock = normalized.DockId is Guid requested
                ? await LockDock(db, requested)
                : await Allocate(db, normalized, null);
            if (!Eligible(dock, normalized)) throw Conflict("dock_unavailable");
            var visit = new Visit(Guid.NewGuid(), dock.Id, dock.Code, normalized.Vehicle, normalized.Cargo, normalized.WeightKg,
                normalized.Start, normalized.End, VisitStatus.Scheduled, 1);
            await db.Execute("""
                INSERT INTO public.visits(id,tenant_id,carrier_id,dock_id,vehicle,cargo,weight_kg,starts_at,ends_at,status,version)
                VALUES (@id,@tenant,@carrier,@dock,@vehicle,@cargo,@weight,@start,@end,'Scheduled',1)
                """, ("id", visit.Id), ("dock", dock.Id), ("vehicle", visit.Vehicle), ("cargo", visit.Cargo.ToString()),
                ("weight", visit.WeightKg), ("start", visit.Start), ("end", visit.End));
            await db.Execute("""
                INSERT INTO public.booking_requests(tenant_id,carrier_id,subject,request_key,fingerprint,response)
                VALUES (@tenant,@carrier,@subject,@key,@fingerprint,CAST(@response AS jsonb))
                """, ("key", requestKey), ("fingerprint", fingerprint), ("response", JsonSerializer.Serialize(visit)));
            await db.Record("AppointmentScheduled", visit.Id, new { visit.DockId, visit.Version });
            return visit;
        }, ct);
    }

    public Task<Visit> TransitionAsync(Actor a, Guid id, VisitAction action, long version, CancellationToken ct)
    {
        if (action != VisitAction.Cancel) RequireOperator(a);
        return Run(a, async db =>
        {
            var visit = await LoadVersion(db, id, version);
            var (status, kind) = VisitLifecycle.Apply(visit.Status, action, visit.End, clock.GetUtcNow());
            if (action == VisitAction.Assign && !(await LockDock(db, visit.DockId)).Operational) throw Conflict("dock_unavailable");
            // The unique partial index arbitrates physical occupancy independently of reservation end time.
            await db.Execute("UPDATE public.visits SET status=@status,version=version+1 WHERE tenant_id=@tenant AND id=@id",
                ("id", id), ("status", status.ToString()));
            await db.Record(kind, id, new { visit.DockId, From = visit.Status.ToString(), To = status.ToString(), Version = version + 1 });
            return visit with { Status = status, Version = version + 1 };
        }, ct);
    }

    public Task BlockAsync(Actor a, Guid dockId, string reason, CancellationToken ct)
    {
        RequireOperator(a);
        if (!ValidText(reason, 512)) throw new OperationRejected("invalid_reason", 400);
        return Run(a, async db =>
        {
            if (!(await LockDock(db, dockId)).Operational) return false;
            await db.Execute("UPDATE public.docks SET operational=false WHERE tenant_id=@tenant AND id=@id", ("id", dockId));
            await db.Record("DockUnavailable", dockId, new { Reason = reason.Trim() });
            return true;
        }, ct);
    }

    public Task<Visit> ReassignAsync(Actor a, Guid id, long version, CancellationToken ct)
    {
        RequireOperator(a);
        return Run(a, async db =>
        {
            var visit = await LoadVersion(db, id, version);
            if (visit.Status is not (VisitStatus.Scheduled or VisitStatus.CheckedIn)) throw Conflict("invalid_transition");
            var dock = await Allocate(db, new Book(visit.Vehicle, visit.Cargo, visit.WeightKg, visit.Start, visit.End), visit.DockId);
            await db.Execute("UPDATE public.visits SET dock_id=@dock,version=version+1 WHERE tenant_id=@tenant AND id=@id",
                ("id", id), ("dock", dock.Id));
            await db.Record("AppointmentRescheduled", id, new { OldDockId = visit.DockId, DockId = dock.Id, Version = version + 1 });
            return visit with { DockId = dock.Id, DockCode = dock.Code, Version = version + 1 };
        }, ct);
    }

    private static async Task<DockView> Allocate(Session db, Book request, Guid? origin)
    {
        // Lock by stable ID (not preference), including the origin on reassignment. Collect busy docks
        // in a second statement so READ COMMITTED sees any reservation committed while locks were awaited.
        var docks = await db.List("""
            SELECT id,code,cargo,max_weight_kg,operational FROM public.docks
            WHERE tenant_id=@tenant AND (id=@origin OR (max_weight_kg>=@weight AND (@cargo='General' OR cargo=@cargo)))
            ORDER BY id FOR NO KEY UPDATE
            """, ReadDock, ("origin", (object?)origin ?? DBNull.Value), ("weight", request.WeightKg), ("cargo", request.Cargo.ToString()));
        var busy = (await db.List("""
            SELECT DISTINCT dock_id FROM public.visits WHERE tenant_id=@tenant
                AND status IN ('Scheduled','CheckedIn','Assigned','InProgress')
                AND tstzrange(starts_at,ends_at,'[)') && tstzrange(@start,@end,'[)')
            """, r => r.GetGuid(0), ("start", request.Start), ("end", request.End))).ToHashSet();
        return docks.Where(d => d.Id != origin && Eligible(d, request) && !busy.Contains(d.Id))
            .OrderBy(d => d.Cargo != request.Cargo).ThenBy(d => d.MaxWeightKg)
            .ThenBy(d => d.Code, StringComparer.Ordinal).ThenBy(d => d.Id).FirstOrDefault() ?? throw Conflict("dock_unavailable");
    }

    private static bool Eligible(DockView dock, Book request) => dock.Operational && dock.MaxWeightKg >= request.WeightKg &&
        (request.Cargo == CargoKind.General || dock.Cargo == request.Cargo);

    private static async Task<DockView> LockDock(Session db, Guid id) => (await db.List("""
        SELECT id,code,cargo,max_weight_kg,operational FROM public.docks WHERE tenant_id=@tenant AND id=@id FOR NO KEY UPDATE
        """, ReadDock, ("id", id))).SingleOrDefault() ?? throw NotFound();

    private static async Task<Visit?> LoadVisit(Session db, Guid id, bool forUpdate) =>
        (await db.List("SELECT " + VisitColumns + VisitJoin + " WHERE " + Scope + " AND v.id=@id" +
            (forUpdate ? " FOR NO KEY UPDATE OF v" : ""), ReadVisit, ("id", id))).SingleOrDefault();

    private static async Task<Visit> LoadVersion(Session db, Guid id, long version)
    {
        var visit = await LoadVisit(db, id, true) ?? throw NotFound();
        return visit.Version == version ? visit : throw Conflict("stale_version");
    }

    private async Task<T> Run<T>(Actor a, Func<Session, Task<T>> work, CancellationToken ct, IsolationLevel isolation = IsolationLevel.ReadCommitted)
    {
        if (!ValidText(a.Subject, 512)) throw Forbidden();
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(isolation, ct);
        var db = new Session(connection, tx, a, ct);
        var authorized = await db.List("""
            SELECT 1 FROM public.tenants t WHERE t.id=@tenant AND (@carrier IS NULL OR EXISTS (
                SELECT FROM public.carriers c WHERE c.tenant_id=t.id AND c.id=@carrier))
            """, r => r.GetInt32(0));
        if (authorized.Count == 0) throw Forbidden();
        try
        {
            var result = await work(db);
            await tx.CommitAsync(ct);
            return result;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ExclusionViolation && e.ConstraintName == "visits_no_overlapping_reservations")
        {
            throw Conflict("reservation_overlap");
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == "visits_one_physical_occupant")
        {
            throw Conflict("dock_occupied");
        }
    }

    private static Visit ReadVisit(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3),
        Enum.Parse<CargoKind>(r.GetString(4)), r.GetInt32(5), r.GetFieldValue<DateTimeOffset>(6),
        r.GetFieldValue<DateTimeOffset>(7), Enum.Parse<VisitStatus>(r.GetString(8)), r.GetInt64(9));
    private static DockView ReadDock(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), Enum.Parse<CargoKind>(r.GetString(2)), r.GetInt32(3), r.GetBoolean(4));
    private static bool ValidText(string? text, int maximum) => !string.IsNullOrWhiteSpace(text) && text.Length <= maximum && !text.Contains('\0');
    private static OperationRejected Conflict(string code) => new(code, 409);
    private static OperationRejected Forbidden() => new("forbidden", 403);
    private static OperationRejected NotFound() => new("not_found", 404);
    private static void RequireOperator(Actor a) { if (a.CarrierId is not null) throw Forbidden(); }

    private sealed class Session(NpgsqlConnection conn, NpgsqlTransaction tx, Actor actor, CancellationToken ct)
    {
        private NpgsqlCommand Command(string sql, (string Key, object Value)[] args)
        {
            var command = new NpgsqlCommand(sql, conn, tx);
            if (sql.Contains("@tenant", StringComparison.Ordinal)) command.Parameters.AddWithValue("tenant", actor.TenantId);
            if (sql.Contains("@carrier", StringComparison.Ordinal)) command.Parameters.AddWithValue("carrier", NpgsqlDbType.Uuid, (object?)actor.CarrierId ?? DBNull.Value);
            if (sql.Contains("@subject", StringComparison.Ordinal)) command.Parameters.AddWithValue("subject", actor.Subject);
            foreach (var (key, value) in args)
                if (value is DBNull) command.Parameters.AddWithValue(key, NpgsqlDbType.Uuid, value);
                else command.Parameters.AddWithValue(key, value);
            return command;
        }

        internal async Task<List<T>> List<T>(string sql, Func<NpgsqlDataReader, T> map, params (string Key, object Value)[] args)
        {
            await using var command = Command(sql, args);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var results = new List<T>();
            while (await reader.ReadAsync(ct)) results.Add(map(reader));
            return results;
        }

        internal async Task Execute(string sql, params (string Key, object Value)[] args)
        {
            await using var command = Command(sql, args);
            await command.ExecuteNonQueryAsync(ct);
        }

        internal Task Record(string kind, Guid aggregate, object detail) => Execute("""
            WITH evt AS (
                INSERT INTO public.outbox(id,tenant_id,kind,aggregate_id,occurred_at)
                VALUES (@event,@tenant,@kind,@aggregate,clock_timestamp()) RETURNING id,tenant_id,kind,aggregate_id,occurred_at
            ) INSERT INTO public.audit_log(id,tenant_id,kind,aggregate_id,occurred_at,subject,carrier_id,detail)
                SELECT id,tenant_id,kind,aggregate_id,occurred_at,@subject,@carrier,CAST(@detail AS jsonb) FROM evt
            """, ("event", Guid.NewGuid()), ("kind", kind), ("aggregate", aggregate), ("detail", JsonSerializer.Serialize(detail)));
    }
}
