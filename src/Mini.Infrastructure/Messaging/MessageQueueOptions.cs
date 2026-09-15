namespace Mini.Infrastructure.Messaging;

/// <summary>
/// Ported from <c>Libraries.Infrastructure/DIT.MessageQueue/Configuration/MessageQueueOptions.cs</c>
/// (assembly <c>DigitalInsuranceTools.MessageQueue</c>). The real class supports the same three
/// providers with the same default; this cut-down version keeps only the settings this sample's
/// providers actually need — the real <c>AzureServiceBusAuthenticationMode</c> enum (connection
/// string, managed identity, or client-secret credential) is NOT ported, since nothing in this repo
/// exercises it. See "Where this sample simplifies" in Mini.Infrastructure/README.md's Phase 19
/// section.
/// </summary>
public enum MessageQueueProvider
{
    /// <summary>
    /// Same default as the real library, and the same warning applies: fine for the same-process
    /// publish/consume proof in <c>StepwiseIdentity.Tests</c>, but useless the moment two separate
    /// processes need to share a bus — see docs/adr/0001-messaging-transport.md.
    /// </summary>
    InMemory,
    RabbitMQ,
    AzureServiceBus
}

public sealed class MessageQueueOptions
{
    public const string SectionName = "MessageBus";

    public MessageQueueProvider Provider { get; set; } = MessageQueueProvider.InMemory;

    public RabbitMqOptions RabbitMQ { get; set; } = new();

    public AzureServiceBusOptions AzureServiceBus { get; set; } = new();
}

public sealed class RabbitMqOptions
{
    public string Host { get; set; } = "localhost";

    public string VirtualHost { get; set; } = "/";

    public string Username { get; set; } = "guest";

    public string Password { get; set; } = "guest";
}

/// <summary>
/// Deliberately minimal — a connection string is enough to prove the branch is wired and
/// documented, the way Phase 8's <c>AzureKeyVault</c> provider is documented and code-complete
/// without ever being exercised by an automated test in this repo.
/// </summary>
public sealed class AzureServiceBusOptions
{
    public string? ConnectionString { get; set; }
}
