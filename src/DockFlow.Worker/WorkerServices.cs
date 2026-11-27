using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace DockFlow.Worker;

internal static class RecoveryLoop
{
    internal static async Task RunAsync(Func<CancellationToken, Task> session, ILogger log, CancellationToken ct)
    {
        var delay = 1;
        while (!ct.IsCancellationRequested)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try { await session(ct); }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                if (System.Diagnostics.Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(30)) delay = 1;
                log.LogWarning("Worker loop failed ({FailureType}); retrying in {Seconds}s", error.GetType().Name, delay);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            delay = Math.Min(delay * 2, 15);
        }
    }
}

internal sealed class OutboxRelay(ConnectionFactory factory, WorkerStore store, ILogger<OutboxRelay> log) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => RecoveryLoop.RunAsync(RelayAsync, log, stoppingToken);

    private async Task RelayAsync(CancellationToken ct)
    {
        await using var connection = await factory.CreateConnectionAsync("dockflow-outbox", ct);
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(
            publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        await EventTopology.DeclareAsync(channel, ct);
        while (!ct.IsCancellationRequested)
        {
            if (!channel.IsOpen) throw new InvalidOperationException("Outbox channel closed.");
            var batch = await store.ReadOutboxAsync(ct);
            foreach (var message in batch)
            {
                using var confirm = CancellationTokenSource.CreateLinkedTokenSource(ct);
                confirm.CancelAfter(TimeSpan.FromSeconds(5));
                var properties = new BasicProperties
                {
                    Persistent = true,
                    ContentType = "application/json",
                    Type = message.Kind,
                    MessageId = message.Id.ToString("D"),
                    Timestamp = new AmqpTimestamp(message.OccurredAt.ToUnixTimeSeconds())
                };
                await channel.BasicPublishAsync(EventTopology.Exchange, "v1.event", mandatory: true,
                    basicProperties: properties, body: message.Encode(), cancellationToken: confirm.Token);
                await store.MarkPublishedAsync(message.Id, ct);
            }
            if (batch.Count == 0) await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }
}

internal sealed class IncidentConsumer(ConnectionFactory factory, WorkerStore store, ILogger<IncidentConsumer> log) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => RecoveryLoop.RunAsync(ConsumeAsync, log, stoppingToken);

    private async Task ConsumeAsync(CancellationToken ct)
    {
        await using var connection = await factory.CreateConnectionAsync("dockflow-incidents", ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ended = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task End(Exception error)
        {
            ended.TrySetResult(error);
            return Task.CompletedTask;
        }
        channel.ChannelShutdownAsync += (_, _) => End(new InvalidOperationException("Consumer channel closed."));
        await EventTopology.DeclareAsync(channel, ct);
        await channel.BasicQosAsync(0, 1, global: false, cancellationToken: ct);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.UnregisteredAsync += (_, _) => End(new InvalidOperationException("Consumer unregistered."));
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            using var processing = CancellationTokenSource.CreateLinkedTokenSource(session.Token, delivery.CancellationToken);
            try { await HandleAsync(channel, delivery, processing.Token); }
            catch (OperationCanceledException) when (processing.IsCancellationRequested) { }
            catch (Exception error) { await End(error); }
        };
        try
        {
            await channel.BasicConsumeAsync(EventTopology.Queue, autoAck: false, consumer: consumer, cancellationToken: ct);
            throw await ended.Task.WaitAsync(ct);
        }
        finally { await session.CancelAsync(); }
    }

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        if (EventEnvelope.Decode(delivery.Body.Span) is not { } message)
        {
            log.LogWarning("Dead-lettering invalid event envelope");
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, cancellationToken: ct);
            return;
        }
        try { await store.ApplyAsync(message, ct); }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            var transient = error is NpgsqlException { IsTransient: true } or TimeoutException;
            log.LogWarning("Incident transaction failed ({FailureType}); requeue requested: {Requeue}", error.GetType().Name, transient);
            if (transient) await Task.Delay(TimeSpan.FromSeconds(1), ct);
            // BasicReject counts as a processing failure on RabbitMQ versions where BasicNack does not.
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: transient, cancellationToken: ct);
            return;
        }
        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken: ct);
    }
}

internal sealed class OverrunScanner(WorkerStore store, ILogger<OverrunScanner> log) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => RecoveryLoop.RunAsync(ScanAsync, log, stoppingToken);

    private async Task ScanAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (await store.ScanOverrunsAsync(ct) < 100) await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }
}
