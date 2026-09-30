using System.Collections.Concurrent;
using System.Diagnostics;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Wolverine;
using Wolverine.Postgresql;

var connectionString =
    Environment.GetEnvironmentVariable("ConnectionStrings__wolverine")
    ?? throw new InvalidOperationException();

await Repro.ExecuteAsync(
    connectionString,
    $"drop schema if exists {Repro.StoreSchema} cascade; drop schema if exists {Repro.QueueSchema} cascade;"
);

// 1. Fill the queue while no node listens to it.
using (var sender = await Repro.StartNodeAsync(connectionString, listen: false))
{
    var bus = sender.Services.GetRequiredService<IMessageBus>();
    for (var number = 1; number <= Repro.MessageCount; number++)
    {
        await bus.PublishAsync(new ReproMessage(number));
    }

    while (await Repro.QueueDepthAsync(connectionString) < Repro.MessageCount)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(100));
    }

    await sender.StopAsync();
}

Repro.Log($"{await Repro.QueueDepthAsync(connectionString)} messages waiting in the queue, starting the node");

// 2. Start a node on top of that backlog, and let it run until it has been idle for a while.
using var node = await Repro.StartNodeAsync(connectionString, listen: true);

var waited = Stopwatch.StartNew();
while (!Repro.IsIdle(TimeSpan.FromSeconds(15)) && waited.Elapsed < TimeSpan.FromMinutes(5))
{
    await Task.Delay(TimeSpan.FromMilliseconds(500));
}

await node.StopAsync();

return Repro.Report();

public record ReproMessage(int Number);

public class ReproMessageHandler
{
    public static async Task Handle(ReproMessage message, Envelope envelope, CancellationToken cancellation)
    {
        Repro.RecordExecution(message.Number, envelope.Id);
        await Task.Delay(Repro.HandlerDuration, cancellation);
    }
}

public record Execution(int Number, Guid EnvelopeId, DateTime At);

public static class Repro
{
    public const string StoreSchema = "repro_store";
    public const string QueueSchema = "repro_queues";
    public const string QueueName = "repro";
    public const int MessageCount = 30;
    public static readonly TimeSpan HandlerDuration = TimeSpan.FromSeconds(2);

    private static readonly ConcurrentQueue<Execution> Executions = new();

    public static bool IsIdle(TimeSpan quietPeriod) =>
        Executions.Select(e => e.Number).Distinct().Count() == MessageCount
        && DateTime.Now - Executions.Max(e => e.At) > quietPeriod;

    public static async Task<IHost> StartNodeAsync(string connectionString, bool listen)
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Logging.ClearProviders();
        if (listen)
        {
            // Node startup, orphaned-message sweep and recovery
            builder.Logging.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss.fff ";
            });
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.Logging.AddFilter("Wolverine.Runtime.WolverineRuntime", LogLevel.Information);
            builder.Logging.AddFilter("Wolverine.Transports.ListeningAgent", LogLevel.Information);
            builder.Logging.AddFilter("Wolverine.Runtime.Agents.NodeAgentController", LogLevel.Information);
            builder.Logging.AddFilter("Wolverine.RDBMS.DurabilityAgent", LogLevel.Information);
        }

        builder.UseWolverine(opts =>
        {
            opts.ServiceName = "repro";
            opts.Durability.Mode = DurabilityMode.Balanced; // the default
            opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;
            opts.UseRuntimeCompilation();
            opts.Discovery.DisableConventionalDiscovery().IncludeType<ReproMessageHandler>();

            opts.PersistMessagesWithPostgresql(connectionString, StoreSchema)
                .EnableMessageTransport(transport => transport.TransportSchemaName(QueueSchema).AutoProvision());

            opts.PublishMessage<ReproMessage>().ToPostgresqlQueue(QueueName);
            if (listen)
            {
                opts.ListenToPostgresqlQueue(QueueName).UseDurableInbox().MaximumParallelMessages(1);
            }
        });

        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    public static void RecordExecution(int number, Guid envelopeId)
    {
        Executions.Enqueue(new Execution(number, envelopeId, DateTime.Now));
        var count = Executions.Count(e => e.Number == number);
        if (count > 1)
        {
            Log($"message #{number} (envelope {envelopeId}) is executing again (execution {count})");
        }
    }

    public static void Log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");

    public static async Task<long> QueueDepthAsync(string connectionString) =>
        (long)(await ScalarAsync(connectionString, $"select count(*) from {QueueSchema}.wolverine_queue_{QueueName}") ?? 0L);

    public static int Report()
    {
        var executions = Executions.ToArray();
        var duplicates = executions.GroupBy(e => e.Number).Where(g => g.Count() > 1).OrderBy(g => g.Key).ToArray();

        Console.WriteLine();
        Console.WriteLine($"Published {MessageCount} messages, {executions.Length} executions");
        Console.WriteLine($"Messages executed more than once: {duplicates.Length}");
        foreach (var group in duplicates)
        {
            var envelopeIds = string.Join(", ", group.Select(e => e.EnvelopeId).Distinct());
            var times = string.Join(" and ", group.Select(e => $"{e.At:HH:mm:ss.fff}"));
            Console.WriteLine($"  #{group.Key} envelope {envelopeIds}: executed at {times}");
        }

        Console.WriteLine(duplicates.Length > 0 ? "REPRODUCED" : "NOT REPRODUCED");
        return duplicates.Length > 0 ? 1 : 0;
    }

    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }
}
