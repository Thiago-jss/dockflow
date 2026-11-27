using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using RabbitMQ.Client;
using DockFlow.Worker;

var builder = Host.CreateApplicationBuilder(args);
var configuration = builder.Configuration;
string Required(string key) => configuration[key] is { } value && !string.IsNullOrWhiteSpace(value)
    ? value : throw new InvalidOperationException($"Configuration key '{key}' is required.");

var database = Required("ConnectionStrings:DockFlow");
try
{
    var settings = new NpgsqlConnectionStringBuilder(database);
    if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.Database) ||
        string.IsNullOrWhiteSpace(settings.Username))
        throw new ArgumentException();
}
catch (Exception error) when (error is ArgumentException or FormatException or KeyNotFoundException)
{
    throw new InvalidOperationException("ConnectionStrings:DockFlow must specify a valid PostgreSQL Host, Database and Username.");
}

var host = Required("Messaging:Host");
if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
    throw new InvalidOperationException("Messaging:Host must be a hostname or IP address.");
var port = 5672;
if (configuration["Messaging:Port"] is { } rawPort &&
    (!int.TryParse(rawPort, out port) || port is < 1 or > 65535))
    throw new InvalidOperationException("Messaging:Port must be a TCP port from 1 to 65535.");

builder.Services.AddSingleton(new ConnectionFactory
{
    HostName = host,
    Port = port,
    UserName = Required("Messaging:User"),
    Password = Required("Messaging:Password"),
    AutomaticRecoveryEnabled = false,
    RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
    ContinuationTimeout = TimeSpan.FromSeconds(5),
    RequestedHeartbeat = TimeSpan.FromSeconds(10),
    ConsumerDispatchConcurrency = 1
});
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(database));
builder.Services.AddSingleton<WorkerStore>();
builder.Services.AddHostedService<OutboxRelay>();
builder.Services.AddHostedService<IncidentConsumer>();
builder.Services.AddHostedService<OverrunScanner>();
using var app = builder.Build();
await app.RunAsync();
