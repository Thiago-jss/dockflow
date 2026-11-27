using RabbitMQ.Client;

namespace DockFlow.Worker;

internal static class EventTopology
{
    internal const string Exchange = "dockflow.events";
    internal const string Queue = "dockflow.incidents.v1";
    internal const string DeadQueue = "dockflow.incidents.dead.v1";
    private const string DeadExchange = "dockflow.incidents.dlx";

    internal static async Task DeclareAsync(IChannel channel, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(DeadExchange, ExchangeType.Direct, durable: true, cancellationToken: ct);
        await channel.QueueDeclareAsync(DeadQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum", ["x-delivery-limit"] = -1 }, cancellationToken: ct);
        await channel.QueueBindAsync(DeadQueue, DeadExchange, DeadQueue, cancellationToken: ct);
        await channel.QueueDeclareAsync(Queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-dead-letter-exchange"] = DeadExchange,
                ["x-dead-letter-routing-key"] = DeadQueue,
                ["x-dead-letter-strategy"] = "at-least-once",
                ["x-overflow"] = "reject-publish",
                ["x-delivery-limit"] = 3 // Quorum bounds unsuccessful deliveries even across worker restarts.
            }, cancellationToken: ct);
        await channel.QueueBindAsync(Queue, Exchange, "v1.#", cancellationToken: ct);
    }
}
