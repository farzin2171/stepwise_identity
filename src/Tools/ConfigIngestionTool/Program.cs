using System.Text.Json;
using ConfigIngestionTool;
using Duende.IdentityServer.EntityFramework.DbContexts;
using Duende.IdentityServer.EntityFramework.Mappers;
using Duende.IdentityServer.EntityFramework.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// Standalone, so it can run independently of IdentityServerHost — the same reason the real IdG's own
// (since-deleted) IdentityGatewayConfigurationExporter was its own project under src/Tools, not a mode
// flag on the main app. Ingesting config is an operational/deployment-time concern, not a startup
// concern: SeedData.cs (IdentityServerHost/Data) now only migrates schema — it no longer seeds rows.
// Resolved against the current directory, not the build output path — this tool is meant to be run
// the same way every other project in this course is: `cd src/Tools/ConfigIngestionTool && dotnet run`.
var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var connectionString = configuration.GetConnectionString("IdentityServer")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:IdentityServer in appsettings.json.");

var configFilePath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), configuration["ConfigFile"]
    ?? throw new InvalidOperationException("Missing ConfigFile in appsettings.json.")));

Console.WriteLine($"Reading {configFilePath}");
var json = await File.ReadAllTextAsync(configFilePath);
var document = JsonSerializer.Deserialize<ConfigDocument>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidOperationException("Config file deserialized to null.");

// ConfigurationDbContext.OnModelCreating reads a ConfigurationStoreOptions out of its own internal DI
// container — inside IdentityServerHost that comes for free from AddConfigurationStore(), but this is
// a plain console app with no ASP.NET Core host to wire that up, so it builds the same minimal
// container by hand.
var services = new ServiceCollection();
services.AddSingleton(new ConfigurationStoreOptions());
services.AddDbContext<ConfigurationDbContext>(b => b.UseSqlServer(connectionString));
await using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<ConfigurationDbContext>();
await db.Database.MigrateAsync();

// Every category follows the same rule: this file is authoritative. A key already in the database gets
// replaced outright (delete the old row — cascades to its children at the database level, see the
// migrations — then insert the JSON's version) rather than patched field-by-field; a key that's new
// gets inserted; nothing in the database that ISN'T in the file gets touched. That last part is a real
// simplification — the real ingestion tool's actual behavior here is unknown (deleted before this
// course could read it); a stricter "full sync" would also delete rows missing from the file.
//
// Deletes are saved in their own pass before any insert runs, so a key being replaced never risks
// tripping the Name/ClientId unique index against its own about-to-be-removed row.

var identityResourceKeys = document.IdentityResources.Select(r => r.Key).ToHashSet();
var apiScopeKeys = document.ApiScopes.Select(s => s.Name).ToHashSet();
var apiResourceKeys = document.ApiResources.Select(r => r.Name).ToHashSet();
var clientKeys = document.Clients.Select(c => c.ClientId).ToHashSet();

var identityResourcesUpdated = await db.IdentityResources.Where(r => identityResourceKeys.Contains(r.Name)).ExecuteDeleteAsync();
var apiScopesUpdated = await db.ApiScopes.Where(s => apiScopeKeys.Contains(s.Name)).ExecuteDeleteAsync();
var apiResourcesUpdated = await db.ApiResources.Where(r => apiResourceKeys.Contains(r.Name)).ExecuteDeleteAsync();
var clientsUpdated = await db.Clients.Where(c => clientKeys.Contains(c.ClientId)).ExecuteDeleteAsync();

db.IdentityResources.AddRange(document.IdentityResources.Select(dto => dto.ToModel().ToEntity()));
db.ApiScopes.AddRange(document.ApiScopes.Select(dto => dto.ToModel().ToEntity()));
db.ApiResources.AddRange(document.ApiResources.Select(dto => dto.ToModel().ToEntity()));
db.Clients.AddRange(document.Clients.Select(dto => dto.ToModel().ToEntity()));
await db.SaveChangesAsync();

Console.WriteLine($"IdentityResources: {document.IdentityResources.Count - identityResourcesUpdated} added, {identityResourcesUpdated} updated");
Console.WriteLine($"ApiScopes:         {document.ApiScopes.Count - apiScopesUpdated} added, {apiScopesUpdated} updated");
Console.WriteLine($"ApiResources:      {document.ApiResources.Count - apiResourcesUpdated} added, {apiResourcesUpdated} updated");
Console.WriteLine($"Clients:           {document.Clients.Count - clientsUpdated} added, {clientsUpdated} updated");
