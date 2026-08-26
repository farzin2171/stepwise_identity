using IdentityServerHost;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddIdentityServer(options =>
       {
           options.KeyManagement.Enabled = false;
       })
       .AddInMemoryIdentityResources(Config.IdentityResources)
       .AddInMemoryApiScopes(Config.ApiScopes)
       .AddInMemoryClients(Config.Clients)
       .AddDeveloperSigningCredential();

var app = builder.Build();

app.UseIdentityServer();

app.Run();
