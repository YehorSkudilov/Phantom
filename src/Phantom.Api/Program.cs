using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Phantom.Api;
using Phantom.Api.Data;
using Phantom.Api.Services;
using Phantom.Api.Services.Metadata;
using Phantom.Api.Services.Sources;
using Phantom.Api.Services.Torrents;
using Phantom.Api.Swagger;
using Phantom.Shared;

// Settings from .env next to the project, the same file docker-compose uses (see .env.example). Must run before the
// builder reads environment variables.
DotEnv.Load(Path.Combine(Directory.GetCurrentDirectory(), ".env"));

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<PhantomOptions>(builder.Configuration.GetSection(PhantomOptions.Section));
var phantom = builder.Configuration.GetSection(PhantomOptions.Section).Get<PhantomOptions>() ?? new PhantomOptions();
Directory.CreateDirectory(phantom.DataDirectory);

builder.Services.AddDbContext<PhantomDbContext>(o =>
    o.UseSqlite($"Data Source={phantom.DatabasePath}", s => s.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)));

// Core services
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<SessionTracker>();
builder.Services.AddSingleton<Transcoder>();
builder.Services.AddScoped<LibraryService>();
builder.Services.AddScoped<LibraryImporter>();

// Torrent engine + background workers
builder.Services.AddHttpClient();
builder.Services.AddSingleton<TorrentEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TorrentEngine>());
builder.Services.AddHostedService<ServerDownloadService>();
builder.Services.AddSingleton<SourceSyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SourceSyncService>());

// Sources & metadata
builder.Services.AddHttpClient<IMovieSource, InternetArchiveSource>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient<IMovieSource, TorznabSource>(c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddHttpClient<IMovieSource, YtsSource>(SourceClient);
builder.Services.AddHttpClient<IMovieSource, PirateBaySource>(SourceClient);
builder.Services.AddHttpClient<IMetadataProvider, TmdbMetadataProvider>(c => c.Timeout = TimeSpan.FromSeconds(15));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<TokenService>((o, tokens) =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = "phantom",
            ValidAudience = "phantom",
            IssuerSigningKey = tokens.SigningKey,
            NameClaimType = System.Security.Claims.ClaimTypes.Name,
            RoleClaimType = System.Security.Claims.ClaimTypes.Role,
        };
        // Video players can't send headers, so media URLs carry the token in the query string.
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api/stream") && ctx.Request.Query.TryGetValue("access_token", out var token))
                    ctx.Token = token;
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddProblemDetails();
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSwagger(builder.Configuration);
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// Only trusts X-Forwarded-* from loopback, plus any networks in ReverseProxy:KnownNetworks (comma-separated
// CIDRs) - otherwise behind a proxy every request looks like it came from the proxy over plain HTTP, and the
// stream URLs handed to players would point at the wrong scheme and host.
var forwarded = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
};
foreach (var network in (app.Configuration["ReverseProxy:KnownNetworks"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
app.UseForwardedHeaders(forwarded);

await SeedAsync(app.Services);

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler();

app.UseSwaggerWithAuth();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", () => Results.Ok()).AllowAnonymous();

app.Run();

// Public torrent sites turn away requests with no browser-like user agent.
static void SourceClient(HttpClient c)
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Phantom");
}

static async Task SeedAsync(IServiceProvider services)
{
    using var scope = services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
    await db.Database.EnsureCreatedAsync();

    if (!await db.Settings.AnyAsync()) db.Settings.Add(new ServerSettings());

    var options = scope.ServiceProvider.GetRequiredService<IOptions<PhantomOptions>>().Value;
    if (options.SeedDefaultSources && !await db.Sources.AnyAsync())
    {
        // Ships with a legal, public-domain source. Torznab indexers are added by the admin.
        db.Sources.Add(new Source
        {
            Name = "Internet Archive – Feature Films",
            Type = SourceType.InternetArchive,
            Query = InternetArchiveSource.DefaultQuery,
            Categories = "100",
            SyncIntervalMinutes = 720,
            Enabled = true,
        });
    }
    await db.SaveChangesAsync();
}

public partial class Program;

