using Microsoft.EntityFrameworkCore;
using Mini.UserService.Connectors.Data;
using Mini.UserService.Connectors.Handlers;
using Mini.UserService.Connectors.Repositories;

namespace Mini.UserService.Connectors;

// Real counterpart: DIT.Connectors.AspNetCore's Extensions.cs, whose AddConnectors() registers
// "CascadingConnectorDbContext + RestClient + WebApiConnector + all four read repositories" — the
// runtime read side, for services that execute connectors.
//
// The real library has three entry points; this ports one. AddConnectorsManagement<TRepository>() and
// AddWebApiConnectorsManagement() exist to import desired-state configuration (validate → diff →
// add/delete/update → ConfigurationResults, the same reconciliation idea as Terraform). This sample's
// connector configuration arrives through HasData in the migration instead, which is the same choice
// Phase 11 made for the tenant rows and for the same reason: a migration carrying baseline reference
// data is a different thing from an app seeding itself at boot. Porting the import service is a phase
// of its own, and it is named in the README's "deliberately still missing."
//
// One line about where this lives. The repo's rule is that a concern needed by MORE THAN ONE project
// belongs in Mini.Infrastructure, and this is needed by exactly one, so it stays here as a folder in
// the service that consumes it. The real DIT.Connectors is four separate csprojs; the reason it is
// layered that way (Data ← Domain ← HTTP ← AspNetCore, dependencies pointing down and never up) is
// EqusoftInfra Series 4's subject and is not re-explained here. When Mini.AuthorizationService needs
// the same extension-point machinery, THAT is the phase that earns the move — extracting now would be
// designing for a duplication that has not happened, which is precisely what Phase 10 was written to
// argue against.
public static class ConnectorsExtensions
{
    public static IServiceCollection AddConnectors(this IServiceCollection services, IConfiguration configuration)
    {
        // The second DbContext over the SAME database as ServiceDbContext, with its own migrations
        // history table because both contexts otherwise default to "__EFMigrationsHistory".
        //
        // Worth being precise about why, because the obvious reason is wrong and this was checked
        // rather than assumed: removing MigrationsHistoryTable and starting against a freshly dropped
        // MiniUsers does NOT fail. Both migrations apply, both rows land in the one
        // __EFMigrationsHistory, and `dotnet ef migrations list` for each context still reports only
        // its own — because a context's migration set comes from its assembly, not from the table.
        // Nothing complains, at all. See the README's Phase 12 "things that broke" #1.
        //
        // It stays separated as bookkeeping hygiene rather than as crash avoidance: one table with a
        // MigrationId primary key shared by two independent histories is a latent collision, and it
        // makes "revert this context to migration X" ambiguous to anyone reading the table. Cheap
        // insurance — but not the load-bearing safety measure it looks like.
        services.AddDbContext<CascadingConnectorDbContext>(options =>
            options.UseSqlServer(
                configuration.GetConnectionString("ServiceDb"),
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory_Connectors")));

        // The read repositories — the "all four" of the real AddConnectors(), minus the Azure Graph
        // one this sample has no connector for.
        services.AddScoped<IConnectorsHandlersTenantsRepository, ConnectorsHandlersTenantsRepository>();
        services.AddScoped<IWebApiConnectorConfigurationRoutesRepository, WebApiConnectorConfigurationRoutesRepository>();
        services.AddScoped<IClaimConnectorConfigurationsRepository, ClaimConnectorConfigurationsRepository>();

        // ClaimConnector needs the caller's raw ClaimsPrincipal, because the claim it reads is named at
        // runtime by a database row. See ClaimConnector.cs for why IIdentityContext can't serve this.
        services.AddHttpContextAccessor();

        services.AddScoped<IWebApiConnector, WebApiConnector>();
        services.AddScoped<IClaimConnector, ClaimConnector>();

        // One handler per extension point.
        services.AddScoped<GetUserRoleHandler>();
        services.AddScoped<GetUserByEmailHandler>();

        // The named client WebApiConnector resolves — and the one HttpClient in this repo with NO
        // resilience policy at all. Both omissions are deliberate and neither is an oversight; this is
        // the only place Mini.Infrastructure's ResiliencePolicies is available and not used.
        //
        // NO RETRY, and this one is measured rather than argued. With ResiliencePolicies.Retry()
        // attached and Acme's API stopped, acme's role lookup took a consistent 18.3 seconds: three
        // attempts at ~4.1s of connect timeout each, plus the policy's 2s and 4s backoff. One raw
        // attempt at the same dead port takes 4.1s. So the retry added twelve seconds of pure delay
        // TO EVERY LOGIN for that tenant and changed the answer not at all.
        //
        // The reason it is wrong here is structural, not just expensive: a cascade's next link IS the
        // fallback. Retrying in front of it multiplies the very latency the cascade exists to avoid,
        // to reach a link that was always going to be reached. Retry belongs where there is nothing
        // behind the call — which is exactly where MvcClient and IdentityServerHost use it.
        //
        // NO CIRCUIT BREAKER, and this one is reasoning, said plainly as reasoning:
        // ResiliencePolicies.CircuitBreaker() returns a policy whose state is per-instance, and
        // AddPolicyHandler attaches ONE instance to the named client — so the failure count is shared
        // by every request through it, whichever tenant's host it was aimed at. With per-tenant hosts
        // that is the wrong granularity in a way that matters: three consecutive failures against one
        // tenant's dead integration would open the circuit for thirty seconds against EVERY tenant's,
        // turning one tenant's outage into everybody's. Not reproduced end to end — Initech's
        // misconfiguration answers 403 and HandleTransientHttpError does not count 4xx, so nothing in
        // this sample opens the breaker.
        //
        // What that leaves unsolved is real and is named in the README's "deliberately still missing":
        // 4.1 seconds per login is still the cost of a dead tenant integration, and a breaker is
        // precisely the tool for it — a PER-HOST one, keyed off the request URI. That is the actual
        // fix, and it is a phase of its own rather than a line here.
        services.AddHttpClient("connectors");

        return services;
    }
}
