using Collector.Engine.Serialization;
using Collector.Server;
using Collector.Server.Auth;
using Collector.Server.Data;
using Collector.Server.Game;
using Collector.Server.Packs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<CollectorOptions>(builder.Configuration.GetSection(CollectorOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PackRegistry>();
builder.Services.AddSingleton<PlayerLocks>();
builder.Services.AddSingleton<SigningKeyStore>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddScoped<GameService>();

builder.Services.AddDbContext<CollectorDb>((services, db) =>
{
    var dataDir = DataPaths.Resolve(services.GetRequiredService<IOptions<CollectorOptions>>().Value, builder.Environment);
    db.UseSqlite($"Data Source={Path.Combine(dataDir, "collector.db")}");
});

builder.Services.ConfigureHttpJsonOptions(o => EngineJson.ApplyTo(o.SerializerOptions));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<SigningKeyStore>((jwt, keys) =>
    {
        jwt.MapInboundClaims = false;
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = TokenService.Issuer,
            ValidAudience = TokenService.Audience,
            IssuerSigningKey = keys.Key,
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddOpenApi();

var app = builder.Build();

// Fail fast on a broken pack, and create the database on first run.
var packs = app.Services.GetRequiredService<PackRegistry>();
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<CollectorDb>().Database.EnsureCreated();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseWebAssemblyDebugging();
}

// Pack art (assets/...) is served from the packs folder.
app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(packs.Root), RequestPath = "/packs" });

app.UseAuthentication();
app.UseAuthorization();

app.MapCollectorApi();
// The Blazor WebAssembly client is served from the same origin, so no CORS is needed. Static assets
// are served by their plain and fingerprinted names (the client's import map points at the latter).
app.MapStaticAssets();
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
