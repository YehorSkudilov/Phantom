using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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
builder.Services.AddSingleton<PlaybackActivity>();
builder.Services.AddSingleton<Transcoder>();
builder.Services.AddSingleton<HlsSessions>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HlsSessions>());
builder.Services.AddSingleton<TrickplayService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TrickplayService>());
builder.Services.AddScoped<LibraryService>();
builder.Services.AddScoped<ShowLibraryService>();
builder.Services.AddScoped<DiscoverService>();
builder.Services.AddScoped<LibraryImporter>();

// Torrent engine + background workers
builder.Services.AddHttpClient();
builder.Services.AddSingleton<TorrentEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TorrentEngine>());
builder.Services.AddHostedService<ServerDownloadService>();
builder.Services.AddSingleton<SourceSyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SourceSyncService>());
builder.Services.AddHostedService<MetadataRefreshService>();

// Sources & metadata
builder.Services.AddHttpClient<IMovieSource, InternetArchiveSource>(c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddHttpClient<IMovieSource, YtsSource>(SourceClient);
builder.Services.AddHttpClient<IMovieSource, PirateBaySource>(SourceClient);
builder.Services.AddHttpClient<IMovieSource, KnabenSource>(SourceClient);
builder.Services.AddHttpClient<IMovieSource, EztvSource>(SourceClient);
builder.Services.AddHttpClient<IMovieSource, WorldCinemaSource>(SourceClient);
builder.Services.AddHttpClient<TvMazeClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Phantom (self-hosted media server)");
});
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
// JSON shrinks ~15x gzipped (the home screen's rows: ~75 KB to ~5 KB), which is most of a reply's time on a phone.
// Only the default text/JSON types are compressed: video goes out as it is (it's compressed already, and range
// requests need its real length). On for HTTPS too, as the reverse proxy forwards it as such. Gzip alone: it's what
// the app asks for, and Brotli's fast setting came out larger.
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
});

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
app.UseResponseCompression();

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
    c.Timeout = TimeSpan.FromSeconds(60);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Phantom");
}

static async Task SeedAsync(IServiceProvider services)
{
    using var scope = services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<PhantomDbContext>();
    await MigrateAsync(db);

    if (!await db.Settings.AnyAsync()) db.Settings.Add(new ServerSettings());

    var options = scope.ServiceProvider.GetRequiredService<IOptions<PhantomOptions>>().Value;
    if (options.SeedDefaultSources) await SeedSourcesAsync(db, options);
    await db.SaveChangesAsync();
}

// The schema is kept by EF Core migrations (Data/Migrations; add one with `dotnet ef migrations add <Name>` in
// Phantom.Api). Databases from before migrations were made by EnsureCreated: they're brought up to the first
// migration by the patches below and marked as having it, so only the later migrations run on them.
static async Task MigrateAsync(PhantomDbContext db)
{
    var tables = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'").ToListAsync();
    if (tables.Contains("Movies") && !tables.Contains("__EFMigrationsHistory"))
    {
        await UpgradeLegacySchemaAsync(db);
        var history = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IHistoryRepository>();
        var baseline = db.Database.GetMigrations().First();
        await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript());
        await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new Microsoft.EntityFrameworkCore.Migrations.HistoryRow(
            baseline, ProductInfo.GetVersion())));
    }
    await db.Database.MigrateAsync();

    // Write-ahead logging: imports and metadata workers write while people browse and play, and with it reads carry on
    // during a write instead of waiting for it. It's a setting of the database file, so it stays on.
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    await DatabaseStatistics.EnsureAsync(db);
}

// Changes made to EnsureCreated databases before there were migrations; they match the first migration afterwards.
static async Task UpgradeLegacySchemaAsync(PhantomDbContext db)
{
    // Settings added since the database was made.
    var settingsColumns = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('Settings')").ToListAsync();
    if (settingsColumns.Count > 0 && !settingsColumns.Contains("KeepServerDownloadsDays"))
        await db.Database.ExecuteSqlRawAsync("""ALTER TABLE "Settings" ADD COLUMN "KeepServerDownloadsDays" INTEGER NOT NULL DEFAULT 7""");

    // Profile names used to be unique across the whole server; now only within their account.
    var usernameIndex = await db.Database.SqlQueryRaw<string>(
        "SELECT sql AS Value FROM sqlite_master WHERE type = 'index' AND name = 'IX_Users_Username'").ToListAsync();
    if (usernameIndex.FirstOrDefault() is { } sql && !sql.Contains("WHERE", StringComparison.OrdinalIgnoreCase))
    {
        await db.Database.ExecuteSqlRawAsync("""
            DROP INDEX "IX_Users_Username";
            CREATE UNIQUE INDEX "IX_Users_Username" ON "Users" ("Username") WHERE "ParentUserId" IS NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_ParentUserId_Username" ON "Users" ("ParentUserId", "Username") WHERE "ParentUserId" IS NOT NULL;
            """);
    }
}

// The sources are built in: each exists on every server and keeps the definition below (added on first start, put back
// if missing, and corrected if a stored copy drifted). Admins can only switch them on or off.
static async Task SeedSourcesAsync(PhantomDbContext db, PhantomOptions options)
{
    Source[] builtIn =
    [
        new() { Name = "YTS", Type = SourceType.Yts, Categories = "4", SyncIntervalMinutes = 180 },
        new() { Name = "Knaben (many sites)", Type = SourceType.Knaben, SyncIntervalMinutes = 180 },
        new() { Name = "The Pirate Bay", Type = SourceType.PirateBay, Categories = PirateBaySource.DefaultCategories, SyncIntervalMinutes = 180 },
        new() { Name = "EZTV (TV)", Type = SourceType.Eztv, SyncIntervalMinutes = 120 },
        new()
        {
            Name = "World cinema (Chinese, Russian)", Type = SourceType.WorldCinema,
            Query = WorldCinemaSource.DefaultLanguages, SyncIntervalMinutes = 720,
        },
        new()
        {
            Name = "Internet Archive - Feature Films", Type = SourceType.InternetArchive,
            Query = InternetArchiveSource.DefaultQuery, Categories = "100", SyncIntervalMinutes = 720,
        },
    ];
    foreach (var definition in builtIn)
    {
        var stored = await db.Sources.FirstOrDefaultAsync(s => s.Type == definition.Type);
        if (stored is null)
        {
            db.Sources.Add(definition);
            continue;
        }
        stored.Name = definition.Name;
        stored.BaseUrl = null;
        stored.ApiKey = null;
        stored.Query = definition.Query;
        stored.Categories = definition.Categories;
        stored.SyncIntervalMinutes = definition.SyncIntervalMinutes;
    }
}


public partial class Program;

