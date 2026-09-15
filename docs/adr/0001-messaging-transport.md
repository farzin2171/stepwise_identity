# Message bus transport: RabbitMQ by default, Azure Service Bus documented for real use

`Mini.AuthorizationService` needs to notify `Mini.MessageCenter` of policy changes across a
process boundary. The real `Libraries.Infrastructure/DIT.MessageQueue` wraps MassTransit
over three providers (`InMemory`, `RabbitMQ`, `AzureServiceBus`), and DIT's own production
default is `AzureServiceBus`.

`InMemory` was the first choice discussed, but it doesn't cross process boundaries — two
separate ASP.NET Core processes can't share an in-memory bus, which rules it out for this
specific hop entirely (it may still be useful for demonstrating same-process publish/consume
in a later phase).

A live Azure Service Bus namespace was rejected as the default because it's a billed,
account-gated resource — this repo has never required anyone to have an Azure subscription
just to run `test-phaseN.ps1` (see the Key Vault precedent, Phase 8: a real-cloud path is
documented and supported, but the default developer path needs nothing external).

**Decision**: `run-all.ps1` starts RabbitMQ via `docker-compose` as the default transport,
verified end-to-end by an automated test script. `MessageQueueProvider.AzureServiceBus` is
implemented and documented (a setup doc mirroring `azure-key-vault-setup.md`) for real
deployments, but isn't exercised by the automated tests — the same split Phase 8 already
established for signing keys.

This is this repo's first Docker dependency. Accepted as a reasonable one-time ask, in
exchange for a stronger regression test than manually verifying a cloud-only path would allow.
