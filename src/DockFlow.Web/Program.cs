using System.Net;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using DockFlow.Infrastructure;
using DockFlow.Web;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var migrate = args is ["--migrate"];
var builder = WebApplication.CreateBuilder(migrate ? [] : args);
// Hosting request-start logs include the query string, which carries the OIDC code and state.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Services.AddDockInfrastructure(builder.Configuration);
if (migrate)
{
    await using var migrationApp = builder.Build();
    await InfrastructureServices.MigrateAsync(migrationApp.Services, CancellationToken.None);
    return;
}

Identity.Configure(builder);
builder.Services.AddSingleton<BoardAnalytics>();
if (builder.Configuration.GetConnectionString("Redis") is { Length: > 0 } redis)
    builder.Services.AddStackExchangeRedisCache(o => o.Configuration = redis);
else
    builder.Services.AddDistributedMemoryCache();
if (builder.Configuration.GetValue<bool>("Telemetry:Enabled"))
{
    // Export aggregate framework metrics only. Do not collect request headers, URLs, bodies or tokens.
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("DockFlow.Web"))
        .WithMetrics(m => m.AddMeter("Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Routing",
            "Microsoft.AspNetCore.RateLimiting", BoardAnalytics.InstrumentationName).AddOtlpExporter())
        .WithTracing(t => t.AddSource(BoardAnalytics.InstrumentationName).AddOtlpExporter());
}
builder.Services.AddControllersWithViews(o => o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()))
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.Configure<ApiBehaviorOptions>(o => o.InvalidModelStateResponseFactory = _ =>
    new BadRequestObjectResult(new ProblemDetails { Status = 400, Title = "Dados da solicitação inválidos." }));
builder.Services.AddProblemDetails();
builder.Services.Configure<CookieTempDataProviderOptions>(o =>
{
    o.Cookie.Name = "__Host-DockFlow-Flash";
    o.Cookie.Path = "/";
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});
builder.Services.AddAntiforgery(o =>
{
    o.Cookie.Name = "__Host-DockFlow-CSRF";
    o.Cookie.Path = "/";
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});
builder.Services.AddSignalR();
builder.Services.AddSingleton<BoardUpdates>();
builder.Services.AddHealthChecks().AddCheck<PostgresReady>("postgres", tags: ["ready"], timeout: TimeSpan.FromSeconds(3));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("carrier-api", c => RateLimitPartition.GetTokenBucketLimiter(
        Identity.ActorFor(c.User, true) is { } a ? $"{a.TenantId:N}/{a.CarrierId:N}" : "anonymous",
        _ => new TokenBucketRateLimiterOptions { TokenLimit = 60, TokensPerPeriod = 60,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
});
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 1;
    foreach (var proxy in builder.Configuration.GetSection("Ingress:KnownProxies").Get<string[]>() ?? [])
        o.KnownProxies.Add(IPAddress.Parse(proxy));
    if (builder.Configuration.GetValue<bool>("Ingress:TrustLabProxy"))
    {
        if (!builder.Configuration.GetValue<bool>("Identity:AllowHttp"))
            throw new InvalidOperationException("Lab proxy trust requires explicit lab identity mode.");
        o.KnownProxies.Clear();
        o.KnownIPNetworks.Clear();
    }
});

var app = builder.Build();
app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseRequestLocalization(o => o.SetDefaultCulture("pt-BR").AddSupportedCultures("pt-BR").AddSupportedUICultures("pt-BR"));
app.Use(async (c, next) =>
{
    c.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'none'";
    c.Response.Headers["X-Content-Type-Options"] = "nosniff";
    if (!c.Request.IsHttps && !c.Request.Path.StartsWithSegments("/health"))
    {
        c.Response.StatusCode = 400;
        await c.Response.WriteAsync("HTTPS obrigatório.");
        return;
    }
    c.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (c.Request.Path.StartsWithSegments("/hubs") && c.Request.Headers.TryGetValue("Origin", out var origin) &&
        !string.Equals(origin.ToString(), $"{c.Request.Scheme}://{c.Request.Host}", StringComparison.OrdinalIgnoreCase))
    {
        c.Response.StatusCode = 403;
        return;
    }
    await next();
});
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();
app.MapHub<BoardHub>("/hubs/board", o =>
{
    // SSE avoids cross-origin WebSocket upgrades and is sufficient for one-way invalidations.
    o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.ServerSentEvents;
    o.CloseOnAuthenticationExpiration = true;
}).RequireAuthorization(Identity.Staff);
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = r => r.Tags.Contains("ready") });
app.Run();

internal sealed class PostgresReady(NpgsqlDataSource source) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        await using var command = source.CreateCommand("SELECT 1");
        return await command.ExecuteScalarAsync(ct) is 1 ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy();
    }
}
