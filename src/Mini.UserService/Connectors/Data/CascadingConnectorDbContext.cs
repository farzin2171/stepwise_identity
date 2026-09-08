using Microsoft.EntityFrameworkCore;

namespace Mini.UserService.Connectors.Data;

// Real counterpart: DIT.Connectors.Data's CascadingConnectorDbContext — the context the user service
// (and only the user service) uses, because it is the only one that needs the cascading choice table.
// Document services use WebApiConnectorDbContext instead, which omits it. Both extend
// BaseConnectorDbContext<T>, and both carry doc comments warning "do not inherit these — inject them
// into your repositories instead."
//
// Three things about this context matter more than the schema itself:
//
// 1. It is a SECOND context over the SAME database. MiniUsers now holds both this service's own
//    tables (ServiceDbContext: Tenants, UserIdentityRoles) and the connector configuration. In
//    production that is exactly the arrangement, because DIT.Connectors is a shared library that
//    brings its own context into whichever service consumes it. It gets its own migrations history
//    table (see the MigrationsHistoryTable call in ../ConnectorsExtensions.cs) — for bookkeeping
//    hygiene, not because sharing one crashes, which was checked and does not: README's Phase 12
//    "things that broke" #1.
//
// 2. It cannot join to Tenants. TenantId here is a bare Guid with no navigation property and no
//    foreign key, because the table it would point at belongs to the other context. So "which tenant
//    is this" has to be resolved in a separate query before any connector lookup — the same
//    constraint a real service boundary imposes, arriving one layer earlier than expected.
//
// 3. The three-layer split (catalog / choice / settings) is the design, not an artifact. From the
//    library map: "catalog says what's possible, choice says what's picked, settings say where to go."
//    Adding a tenant touches choice + settings only; adding a whole integration type touches the
//    catalog only.
public class CascadingConnectorDbContext(DbContextOptions<CascadingConnectorDbContext> options) : DbContext(options)
{
    // ---- CATALOG: what exists, shared across tenants ------------------------------------------
    public DbSet<Connector> Connectors => Set<Connector>();
    public DbSet<Handler> Handlers => Set<Handler>();
    public DbSet<ConnectorHandler> ConnectorHandlers => Set<ConnectorHandler>();

    // ---- CHOICE: what this tenant picked ------------------------------------------------------
    public DbSet<ConnectorHandlerTenant> ConnectorHandlerTenants => Set<ConnectorHandlerTenant>();
    public DbSet<ConnectorHandlerCascadingTenant> ConnectorHandlerCascadingTenants => Set<ConnectorHandlerCascadingTenant>();

    // ---- SETTINGS: where and how, one table per connector type --------------------------------
    public DbSet<WebApiConnectorConfiguration> WebApiConnectorConfigurations => Set<WebApiConnectorConfiguration>();
    public DbSet<WebApiConnectorConfigurationRoute> WebApiConnectorConfigurationRoutes => Set<WebApiConnectorConfigurationRoute>();
    public DbSet<ClaimConnectorConfiguration> ClaimConnectorConfigurations => Set<ClaimConnectorConfiguration>();

    // The three tenants from ServiceDbContext, repeated as bare GUIDs because this context has no way
    // to reference that table (see #2 above). Repeating a GUID literal across two contexts is the
    // same "agree only by convention" problem docs/architecture/README.md describes between the three
    // tenant registries — and here it shows up INSIDE one service, between two of its own contexts.
    private static readonly Guid Acme = Guid.Parse("8f14e45f-ceea-467e-bd42-05d1a4a6b3f0");
    private static readonly Guid Globex = Guid.Parse("c9f0f895-fb98-4d75-8d81-7d7c7f4a6b1e");
    private static readonly Guid Initech = Guid.Parse("a3f5b2c1-9d84-4e17-b6a0-2c8e5f1d7b93");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Connector>(entity =>
        {
            entity.HasKey(c => c.ConnectorId);
            entity.HasIndex(c => c.Type).IsUnique();

            // One row per integration TYPE — not per tenant, not per configured endpoint. This is the
            // table people expect to be bigger than it is.
            entity.HasData(
                new Connector { ConnectorId = 1, Type = ConnectorTypes.WebApi },
                new Connector { ConnectorId = 2, Type = ConnectorTypes.AzureGraph },
                new Connector { ConnectorId = 3, Type = ConnectorTypes.Claim });
        });

        modelBuilder.Entity<Handler>(entity =>
        {
            entity.HasKey(h => h.HandlerId);
            entity.HasIndex(h => h.Name).IsUnique();
            entity.Property(h => h.Name).HasMaxLength(128);

            entity.HasData(
                new Handler { HandlerId = 1, Name = HandlerNames.GetUserRole },
                new Handler { HandlerId = 2, Name = HandlerNames.GetUserByEmail });
        });

        modelBuilder.Entity<ConnectorHandler>(entity =>
        {
            entity.HasKey(ch => ch.ConnectorHandlerId);
            entity.HasIndex(ch => new { ch.ConnectorId, ch.HandlerId }).IsUnique();
            entity.HasOne(ch => ch.Connector).WithMany().HasForeignKey(ch => ch.ConnectorId);
            entity.HasOne(ch => ch.Handler).WithMany().HasForeignKey(ch => ch.HandlerId);

            // Which TYPES can serve which EXTENSION POINTS, plus the first of the two kill switches.
            // IsEnabled here is the BASE flag: false takes the pairing away from every tenant at once,
            // without deleting anybody's choice or settings rows.
            entity.HasData(
                new ConnectorHandler { ConnectorHandlerId = 1, ConnectorId = 1, HandlerId = 1, IsEnabled = true },  // WebApi × GetUserRole
                new ConnectorHandler { ConnectorHandlerId = 2, ConnectorId = 3, HandlerId = 1, IsEnabled = true },  // Claim  × GetUserRole
                new ConnectorHandler { ConnectorHandlerId = 3, ConnectorId = 1, HandlerId = 2, IsEnabled = true },  // WebApi × GetUserByEmail

                // AzureGraph × GetUserRole, disabled at the base. Globex has PICKED this pairing (see
                // ConnectorHandlerTenant below) with its own IsEnabled = true, and still gets nothing,
                // because a lookup only succeeds when BOTH flags are true. That is what having two is
                // for: a tenant's configuration stays intact while the platform withdraws the
                // integration. It is also why a vanished connector has two indistinguishable causes —
                // the same shape as DynamicIdentityProviderEnabled in Phase 9 (see CONTEXT.md).
                new ConnectorHandler { ConnectorHandlerId = 4, ConnectorId = 2, HandlerId = 1, IsEnabled = false });
        });

        modelBuilder.Entity<ConnectorHandlerTenant>(entity =>
        {
            entity.HasKey(t => t.ConnectorHandlerTenantId);
            entity.HasIndex(t => new { t.TenantId, t.ConnectorHandlerId }).IsUnique();
            entity.HasOne(t => t.ConnectorHandler).WithMany().HasForeignKey(t => t.ConnectorHandlerId);

            // The NON-cascading choice table: one connector per (tenant, handler), take it or leave it.
            entity.HasData(
                // Globex picked AzureGraph for GetUserRole. Its own flag is on; the base flag on
                // ConnectorHandler 4 is off. Resolution therefore finds nothing, logs a warning and
                // returns null — and the endpoint falls back to the UserIdentityRoles table, which is
                // exactly the Phase 11 behaviour. Globex is the control case: a tenant that has opted
                // in on paper and is still served by the old path.
                new ConnectorHandlerTenant { ConnectorHandlerTenantId = 1, TenantId = Globex, ConnectorHandlerId = 4, IsEnabled = true },

                // Acme and Initech both use a WebApi connector for GetUserByEmail, non-cascading. This
                // is the pairing that lets a failure be fatal: with nothing behind it, an endpoint that
                // refuses or cannot be reached has to surface as an error rather than as "no such user."
                new ConnectorHandlerTenant { ConnectorHandlerTenantId = 2, TenantId = Acme, ConnectorHandlerId = 3, IsEnabled = true },
                new ConnectorHandlerTenant { ConnectorHandlerTenantId = 3, TenantId = Initech, ConnectorHandlerId = 3, IsEnabled = true });
        });

        modelBuilder.Entity<ConnectorHandlerCascadingTenant>(entity =>
        {
            entity.HasKey(t => t.ConnectorHandlerCascadingTenantId);
            entity.HasIndex(t => new { t.TenantId, t.ConnectorHandlerId }).IsUnique();
            entity.HasOne(t => t.ConnectorHandler).WithMany().HasForeignKey(t => t.ConnectorHandlerId);

            // The CASCADING choice table — the one that exists only in the user service's context, and
            // the one this phase is named after. Same shape as the table above plus an Order byte: try
            // connectors in sequence until one answers.
            entity.HasData(
                // Acme: its own web API first, a claim on the incoming token second.
                new ConnectorHandlerCascadingTenant { ConnectorHandlerCascadingTenantId = 1, TenantId = Acme, ConnectorHandlerId = 1, Order = 1, IsEnabled = true },
                new ConnectorHandlerCascadingTenant { ConnectorHandlerCascadingTenantId = 2, TenantId = Acme, ConnectorHandlerId = 2, Order = 2, IsEnabled = true },

                // Initech: the same chain, and its WebApi configuration names Acme's host (below).
                // That is a deliberately planted misconfiguration, and what it demonstrates is
                // uncomfortable: the cascade ABSORBS it. Acme's API answers 403, the chain moves on,
                // the claim connector finds nothing, and every Initech user quietly becomes "Member".
                // Nothing fails, nothing 500s, no login breaks — which is why nobody notices.
                new ConnectorHandlerCascadingTenant { ConnectorHandlerCascadingTenantId = 3, TenantId = Initech, ConnectorHandlerId = 1, Order = 1, IsEnabled = true },
                new ConnectorHandlerCascadingTenant { ConnectorHandlerCascadingTenantId = 4, TenantId = Initech, ConnectorHandlerId = 2, Order = 2, IsEnabled = true });
        });

        modelBuilder.Entity<WebApiConnectorConfiguration>(entity =>
        {
            entity.HasKey(c => c.WebApiConnectorConfigurationId);
            entity.HasIndex(c => c.TenantId).IsUnique();
            entity.Property(c => c.Host).HasMaxLength(512);

            // Host is per TENANT (1:n to routes, which are per handler) — the real table's shape, and
            // it constrains more than it looks: a tenant has ONE integration host, and every extension
            // point is a route on it. Two handlers cannot point at two different hosts for one tenant.
            entity.HasData(
                new WebApiConnectorConfiguration { WebApiConnectorConfigurationId = 1, TenantId = Acme, Host = "https://localhost:5014" },

                // Initech's host is Acme's host. See the cascading rows above. Globex has no row at
                // all, which is the third distinct state: not "configured and off," simply absent.
                new WebApiConnectorConfiguration { WebApiConnectorConfigurationId = 2, TenantId = Initech, Host = "https://localhost:5014" });
        });

        modelBuilder.Entity<WebApiConnectorConfigurationRoute>(entity =>
        {
            entity.HasKey(r => r.WebApiConnectorConfigurationRouteId);
            entity.HasIndex(r => new { r.WebApiConnectorConfigurationId, r.HandlerId }).IsUnique();
            entity.Property(r => r.Route).HasMaxLength(512);
            entity.HasOne(r => r.WebApiConnectorConfiguration).WithMany(c => c.Routes)
                  .HasForeignKey(r => r.WebApiConnectorConfigurationId);
            entity.HasOne(r => r.Handler).WithMany().HasForeignKey(r => r.HandlerId);

            // Parameterised route templates, filled in by WebApiConnector.BuildUri. These strings are
            // why "Acme renamed a route" is a SQL update rather than a deployment: no C# file in this
            // repo knows that Acme serves users at "users/{id}".
            entity.HasData(
                new WebApiConnectorConfigurationRoute { WebApiConnectorConfigurationRouteId = 1, WebApiConnectorConfigurationId = 1, HandlerId = 1, Route = "users/{id}" },
                new WebApiConnectorConfigurationRoute { WebApiConnectorConfigurationRouteId = 2, WebApiConnectorConfigurationId = 1, HandlerId = 2, Route = "users/by-email/{email}" },
                new WebApiConnectorConfigurationRoute { WebApiConnectorConfigurationRouteId = 3, WebApiConnectorConfigurationId = 2, HandlerId = 1, Route = "users/{id}" },
                new WebApiConnectorConfigurationRoute { WebApiConnectorConfigurationRouteId = 4, WebApiConnectorConfigurationId = 2, HandlerId = 2, Route = "users/by-email/{email}" });
        });

        modelBuilder.Entity<ClaimConnectorConfiguration>(entity =>
        {
            entity.HasKey(c => c.ClaimConnectorConfigurationId);
            entity.HasIndex(c => new { c.TenantId, c.HandlerId }).IsUnique();
            entity.Property(c => c.ClaimName).HasMaxLength(128);
            entity.HasOne(c => c.Handler).WithMany().HasForeignKey(c => c.HandlerId);

            // "Read it off the JWT instead of calling out." The cheapest connector there is: no HTTP,
            // no credential, no latency — and no way to learn anything the caller did not already send.
            entity.HasData(
                new ClaimConnectorConfiguration { ClaimConnectorConfigurationId = 1, TenantId = Acme, HandlerId = 1, ClaimName = "role" },
                new ClaimConnectorConfiguration { ClaimConnectorConfigurationId = 2, TenantId = Initech, HandlerId = 1, ClaimName = "role" });
        });
    }
}

// ---- CATALOG ---------------------------------------------------------------------------------

// One row per integration TYPE. Real counterpart: DIT.Connectors.Data's Connector entity.
public class Connector
{
    public int ConnectorId { get; set; }
    public ConnectorTypes Type { get; set; }
}

// One row per EXTENSION POINT. Name is what a handler class matches itself to — see
// Handlers/ActionHandlerBase.cs's HandlerName.
public class Handler
{
    public int HandlerId { get; set; }
    public required string Name { get; set; }
}

// Which types CAN serve which points, plus the BASE kill switch.
public class ConnectorHandler
{
    public int ConnectorHandlerId { get; set; }
    public int ConnectorId { get; set; }
    public int HandlerId { get; set; }
    public bool IsEnabled { get; set; } = true;

    public Connector? Connector { get; set; }
    public Handler? Handler { get; set; }
}

// ---- CHOICE ----------------------------------------------------------------------------------

// tenant × handler → connector, plus the TENANT kill switch. Points at ConnectorHandler rather than at
// Connector and Handler separately, which is what makes "both flags must be true" a single join — the
// library map describes the real repository's query as exactly that:
// "JOIN ConnectorHandlerTenants ⋈ ConnectorHandlers".
public class ConnectorHandlerTenant
{
    public int ConnectorHandlerTenantId { get; set; }
    public Guid TenantId { get; set; }
    public int ConnectorHandlerId { get; set; }
    public bool IsEnabled { get; set; } = true;

    public ConnectorHandler? ConnectorHandler { get; set; }
}

// The same, plus Order. Present only in this context in the real library too — cascading is a user
// service concept, because looking a user up in three places and taking the first hit only makes sense
// for identity data.
public class ConnectorHandlerCascadingTenant
{
    public int ConnectorHandlerCascadingTenantId { get; set; }
    public Guid TenantId { get; set; }
    public int ConnectorHandlerId { get; set; }

    // A byte in the real schema, and kept as one: an integration chain with more than 255 links is a
    // different problem than this design solves.
    public byte Order { get; set; }
    public bool IsEnabled { get; set; } = true;

    public ConnectorHandler? ConnectorHandler { get; set; }
}

// ---- SETTINGS --------------------------------------------------------------------------------

// tenant → Host, 1:n to routes. One host per tenant, on purpose (real shape).
public class WebApiConnectorConfiguration
{
    public int WebApiConnectorConfigurationId { get; set; }
    public Guid TenantId { get; set; }
    public required string Host { get; set; }

    public List<WebApiConnectorConfigurationRoute> Routes { get; set; } = [];
}

// handler → route template, e.g. "users/{id}".
public class WebApiConnectorConfigurationRoute
{
    public int WebApiConnectorConfigurationRouteId { get; set; }
    public int WebApiConnectorConfigurationId { get; set; }
    public int HandlerId { get; set; }
    public required string Route { get; set; }

    public WebApiConnectorConfiguration? WebApiConnectorConfiguration { get; set; }
    public Handler? Handler { get; set; }
}

// tenant × handler → ClaimName.
public class ClaimConnectorConfiguration
{
    public int ClaimConnectorConfigurationId { get; set; }
    public Guid TenantId { get; set; }
    public int HandlerId { get; set; }
    public required string ClaimName { get; set; }

    public Handler? Handler { get; set; }
}
