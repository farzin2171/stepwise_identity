using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mini.Infrastructure.Messaging;

/// <summary>
/// The DI entry point, loosely modeled on
/// <c>Libraries.Infrastructure/DIT.MessageQueue/DigitalInsuranceToolsBuilderExtensions.AddMessageQueue</c>.
/// The real method hangs off a <c>IDigitalInsuranceToolsBuilder</c> that this from-scratch sample has
/// no counterpart for, and also wires OpenTelemetry tracing/metrics for MassTransit — not ported here,
/// since nothing in this repo has OpenTelemetry wired up yet. What IS ported: reading a
/// "MessageBus" config section shaped like the real "messageQueue" section, and branching on
/// <see cref="MessageQueueProvider"/> to call the matching MassTransit "Using*" method.
///
/// No outbox pattern, no pre-wired retry/dead-letter handling — confirmed absent in the real
/// library too (see CONTEXT.md's "Message bus" entry). A consumer that wants retries configures
/// MassTransit's own <c>UseMessageRetry</c> itself; Phase 19 doesn't need one because there's
/// nothing yet that can genuinely fail transiently.
/// </summary>
public static class MessageBusExtensions
{
    public static IServiceCollection AddMessageBus(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IBusRegistrationConfigurator>? configureConsumers = null)
    {
        var options = new MessageQueueOptions();
        configuration.GetSection(MessageQueueOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        services.AddMassTransit(x =>
        {
            configureConsumers?.Invoke(x);

            switch (options.Provider)
            {
                case MessageQueueProvider.RabbitMQ:
                    x.UsingRabbitMq((context, cfg) =>
                    {
                        cfg.Host(options.RabbitMQ.Host, options.RabbitMQ.VirtualHost, h =>
                        {
                            h.Username(options.RabbitMQ.Username);
                            h.Password(options.RabbitMQ.Password);
                        });
                        cfg.ConfigureEndpoints(context);
                    });
                    break;

                case MessageQueueProvider.AzureServiceBus:
                    // The option exists and is bound (see AzureServiceBusOptions) so the shape is
                    // honest end to end, but wiring MassTransit's actual Azure Service Bus transport
                    // needs the separate MassTransit.Azure.ServiceBus.Core package, which this repo
                    // doesn't reference — the same "documented, not exercised" split Phase 8 drew for
                    // KeyManagement:Provider's AzureKeyVault branch, and the same "catalog entry whose
                    // dispatcher says not implemented" shape Phase 12's AzureGraph connector uses. See
                    // docs/adr/0001-messaging-transport.md.
                    throw new NotSupportedException(
                        "MessageQueueProvider.AzureServiceBus is documented but not wired in this " +
                        "sample - see docs/adr/0001-messaging-transport.md. Add the " +
                        "MassTransit.Azure.ServiceBus.Core package and configure cfg.Host(...) here " +
                        "to actually use it against a real namespace.");

                case MessageQueueProvider.InMemory:
                default:
                    x.UsingInMemory((context, cfg) =>
                    {
                        cfg.ConfigureEndpoints(context);
                    });
                    break;
            }
        });

        return services;
    }
}
