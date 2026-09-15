using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mini.Infrastructure.Messaging;

namespace StepwiseIdentity.Tests;

/// <summary>
/// A consumer that would live in Mini.MessageCenter starting Phase 20. Here it exists only to prove
/// the bus round-trips a real payload — Phase 19 is scoped to the abstraction and the proof, not the
/// consuming service (see CONTEXT.md's "Message bus" entry and Mini.Infrastructure/README.md's
/// Phase 19 section).
/// </summary>
public sealed class PolicyChangedEventConsumer : IConsumer<PolicyChangedEvent>
{
    public static readonly List<PolicyChangedEvent> Received = new();

    public Task Consume(ConsumeContext<PolicyChangedEvent> context)
    {
        Received.Add(context.Message);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Proves the message-bus abstraction round-trips a publish/consume without needing RabbitMQ
/// running — MassTransit's own <see cref="ITestHarness"/> swaps in an in-memory transport, which is
/// the intended way to unit-test this rather than standing up a broker for every build. The separate,
/// <see cref="RequiresRabbitMqAttribute"/>-tagged test below proves the same event round-trips over a
/// REAL RabbitMQ instance; that one needs docker-compose up first (see test-phase19.ps1).
/// </summary>
public class MessageBusTests
{
    [Fact]
    public async Task PublishedPolicyChangedEvent_IsReceivedByConsumer()
    {
        await using var provider = new ServiceCollection()
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<PolicyChangedEventConsumer>();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var expected = new PolicyChangedEvent
        {
            TenantKey = "acme",
            ResourceName = "sample-api",
            OldCondition = "role == \"Member\"",
            NewCondition = "role == \"Admin\"",
            ChangedAtUtc = DateTimeOffset.UtcNow
        };

        await harness.Bus.Publish(expected);

        Assert.True(await harness.Published.Any<PolicyChangedEvent>());

        var consumerHarness = provider.GetRequiredService<IConsumerTestHarness<PolicyChangedEventConsumer>>();
        Assert.True(await consumerHarness.Consumed.Any<PolicyChangedEvent>());

        var received = Assert.Single(PolicyChangedEventConsumer.Received, e => e.TenantKey == "acme");
        Assert.Equal(expected.ResourceName, received.ResourceName);
        Assert.Equal(expected.OldCondition, received.OldCondition);
        Assert.Equal(expected.NewCondition, received.NewCondition);
    }
}

/// <summary>
/// Proves the RabbitMQ branch of <see cref="MessageBusExtensions.AddMessageBus"/> actually works
/// against a real broker — not just the in-memory harness above. Run via test-phase19.ps1, which
/// starts docker-compose first. Not part of the default `dotnet test` run for everyone else, since
/// it needs a broker on localhost:5672.
/// </summary>
[Trait("Category", "RequiresRabbitMQ")]
public class RabbitMqMessageBusTests
{
    [Fact]
    public async Task PublishedPolicyChangedEvent_RoundTripsOverRealRabbitMq()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MessageBus:Provider"] = "RabbitMQ",
                ["MessageBus:RabbitMQ:Host"] = "localhost",
                ["MessageBus:RabbitMQ:VirtualHost"] = "/",
                ["MessageBus:RabbitMQ:Username"] = "guest",
                ["MessageBus:RabbitMQ:Password"] = "guest"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddMessageBus(configuration, x => x.AddConsumer<PolicyChangedEventConsumer>());
        await using var provider = services.BuildServiceProvider(true);

        var busControl = provider.GetRequiredService<IBusControl>();
        await busControl.StartAsync(TimeSpan.FromSeconds(20));
        try
        {
            PolicyChangedEventConsumer.Received.Clear();

            var expected = new PolicyChangedEvent
            {
                TenantKey = "globex",
                ResourceName = "sample-api",
                OldCondition = null,
                NewCondition = "role == \"Member\"",
                ChangedAtUtc = DateTimeOffset.UtcNow
            };

            await busControl.Publish(expected);

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline &&
                   !PolicyChangedEventConsumer.Received.Any(e => e.TenantKey == "globex"))
            {
                await Task.Delay(200);
            }

            var received = Assert.Single(
                PolicyChangedEventConsumer.Received, e => e.TenantKey == "globex");
            Assert.Equal(expected.ResourceName, received.ResourceName);
            Assert.Equal(expected.NewCondition, received.NewCondition);
        }
        finally
        {
            await busControl.StopAsync(TimeSpan.FromSeconds(10));
        }
    }
}
