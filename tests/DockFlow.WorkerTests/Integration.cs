using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RabbitMQ.Client;
using DockFlow.Worker;

internal static class Integration
{
    internal static async Task RunAsync(string database, int brokerPort)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var ct = timeout.Token;
        await using var source = NpgsqlDataSource.Create(database);
        var store = new WorkerStore(source);
        var factory = new ConnectionFactory
        {
            HostName = "127.0.0.1", Port = brokerPort, UserName = "dockflowtest",
            Password = Environment.GetEnvironmentVariable("TestBrokerPassword")!, AutomaticRecoveryEnabled = false
        };
        async Task<int> Count(string sql)
        {
            await using var command = source.CreateCommand(sql);
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
        }
        async Task Sql(string sql)
        {
            await using var command = source.CreateCommand(sql);
            await command.ExecuteNonQueryAsync(ct);
        }
        static void Check(bool condition, string behavior)
        {
            if (!condition) throw new InvalidOperationException(behavior);
        }
        await Sql("""
            INSERT INTO public.tenants VALUES ('10000000-0000-0000-0000-000000000001'), ('10000000-0000-0000-0000-000000000002');
            INSERT INTO public.docks (id, tenant_id, code, cargo, max_weight_kg, operational) VALUES
                ('20000000-0000-0000-0000-000000000001','10000000-0000-0000-0000-000000000001','A','General',20000,false),
                ('20000000-0000-0000-0000-000000000002','10000000-0000-0000-0000-000000000001','B','General',20000,true);
            INSERT INTO public.visits (id,tenant_id,dock_id,vehicle,cargo,weight_kg,starts_at,ends_at,status,version)
            SELECT id::uuid,'10000000-0000-0000-0000-000000000001',dock::uuid,'ABC','General',100,
                   date_trunc('minute',now()) + make_interval(hours => starts),
                   date_trunc('minute',now()) + make_interval(hours => ends),status,1
            FROM (VALUES
                ('30000000-0000-0000-0000-000000000001','20000000-0000-0000-0000-000000000001',-2,-1,'InProgress'),
                ('30000000-0000-0000-0000-000000000002','20000000-0000-0000-0000-000000000001',1,2,'Scheduled'),
                ('30000000-0000-0000-0000-000000000003','20000000-0000-0000-0000-000000000001',-4,-3,'Completed'),
                ('30000000-0000-0000-0000-000000000004','20000000-0000-0000-0000-000000000002',-1,1,'InProgress')
            ) v(id,dock,starts,ends,status);
            INSERT INTO public.outbox VALUES
                ('40000000-0000-0000-0000-000000000001','10000000-0000-0000-0000-000000000001','DockUnavailable','20000000-0000-0000-0000-000000000001',now(),null),
                ('40000000-0000-0000-0000-000000000002','10000000-0000-0000-0000-000000000002','DockUnavailable','20000000-0000-0000-0000-000000000001',now(),null),
                ('40000000-0000-0000-0000-000000000003','10000000-0000-0000-0000-000000000001','DockUnavailable','20000000-0000-0000-0000-000000000002',now(),null);
            """);
        var messages = await store.ReadOutboxAsync(ct);
        var duplicate = messages.Single(m => m.AggregateId == Guid.Parse("20000000-0000-0000-0000-000000000001") &&
                                             m.TenantId == Guid.Parse("10000000-0000-0000-0000-000000000001"));
        using var relay = new OutboxRelay(factory, store, NullLogger<OutboxRelay>.Instance);
        using var consumer = new IncidentConsumer(factory, store, NullLogger<IncidentConsumer>.Instance);
        using var scanner = new OverrunScanner(store, NullLogger<OverrunScanner>.Instance);
        await relay.StartAsync(ct);
        await consumer.StartAsync(ct);
        await scanner.StartAsync(ct);
        try
        {
            while (await Count("SELECT count(*) FROM public.inbox") != 3 ||
                   await Count("SELECT count(*) FROM public.outbox WHERE published_at IS NOT NULL") != 3 ||
                   await Count("SELECT count(*) FROM public.incidents WHERE kind = 'Overrun'") != 1)
                await Task.Delay(100, ct);
            Check(await Count("SELECT count(*) FROM public.incidents WHERE kind = 'Maintenance'") == 2,
                "Maintenance applies to current nonterminal visits on unavailable docks only, scoped by tenant");
            Check(await store.ScanOverrunsAsync(ct) == 0, "Repeat scan must not duplicate incidents");
            await store.ApplyAsync(duplicate, ct);
            await store.ApplyAsync(duplicate with { Id = Guid.NewGuid() }, ct);
            Check(await Count("SELECT count(*) FROM public.incidents") == 3, "Redelivery and distinct events cannot duplicate incidents");
            Check(await Count("SELECT count(*) FROM public.inbox") == 4, "Exact redelivery must not duplicate inbox");
            Console.WriteLine("PASS: confirmed relay, maintenance eligibility, overrun scan, and duplicate suppression");

            await using var connection = await factory.CreateConnectionAsync(ct);
            await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true), ct);
            var properties = new BasicProperties { Persistent = true };
            for (var index = 0; index < 10; index++)
                await channel.BasicPublishAsync(EventTopology.Exchange, "v1.event", true, properties, duplicate.Encode(), ct);
            // Prefetch=1 and a marker in the same queue prove previous deliveries drained.
            await channel.BasicPublishAsync(EventTopology.Exchange, "v1.event", true, properties,
                (duplicate with { Id = Guid.NewGuid(), Kind = "AppointmentScheduled" }).Encode(), ct);
            while (await Count("SELECT count(*) FROM public.inbox") != 5) await Task.Delay(50, ct);
            Check(await Count("SELECT count(*) FROM public.incidents") == 3, "Ten broker duplicates produced an extra incident");
            Console.WriteLine("PASS: ten RabbitMQ duplicates applied no new effect");

            await Sql("""
                CREATE FUNCTION test_transient_incident_failure() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
                    IF NEW.kind = 'Maintenance' THEN
                        RAISE EXCEPTION 'synthetic serialization conflict' USING ERRCODE = '40001';
                    END IF;
                    RETURN NEW;
                END; $$;
                CREATE TRIGGER test_transient BEFORE INSERT ON public.incidents
                    FOR EACH ROW EXECUTE FUNCTION test_transient_incident_failure();
                """);
            await channel.BasicPublishAsync(EventTopology.Exchange, "v1.event", true, properties, Encoding.UTF8.GetBytes("bad json"), ct);
            await channel.BasicPublishAsync(EventTopology.Exchange, "v1.event", true, properties,
                (duplicate with { Id = Guid.Parse("40000000-0000-0000-0000-000000000004") }).Encode(), ct);
            var reasons = new HashSet<string>();
            while (reasons.Count < 2)
            {
                var dead = await channel.BasicGetAsync(EventTopology.DeadQueue, autoAck: true, cancellationToken: ct);
                if (dead?.BasicProperties.Headers?["x-first-death-reason"] is byte[] reason)
                    reasons.Add(Encoding.UTF8.GetString(reason));
                await Task.Delay(100, ct);
            }
            Check(reasons.SetEquals(["rejected", "delivery_limit"]), "Poison and exhausted transient failure must reach the DLQ");
            Check(await Count("SELECT count(*) FROM public.inbox") == 5, "Uncommitted inbox work must roll back");
            Console.WriteLine("PASS: poison and bounded quorum retry dead-lettering");
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await scanner.StopAsync(stop.Token);
            await consumer.StopAsync(stop.Token);
            await relay.StopAsync(stop.Token);
        }
    }
}
