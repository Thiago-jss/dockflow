using System.Security.Claims;
using DockFlow.Application;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace DockFlow.Web;

internal static class Identity
{
    public const string Browser = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string Oidc = OpenIdConnectDefaults.AuthenticationScheme;
    public const string Bearer = JwtBearerDefaults.AuthenticationScheme;
    public const string Staff = "Staff";
    public const string Carrier = "CarrierApi";
    public const string Managers = "LogisticsManager,Administrator";
    public const string Gate = "GateOperator," + Managers;
    public const string Dock = "DockOperator," + Managers;

    public static Actor? ActorFor(ClaimsPrincipal? user, bool carrier = false)
    {
        if (user?.Identity?.IsAuthenticated != true ||
            !Guid.TryParse(One(user, "tenant_id"), out var tenant) || tenant == Guid.Empty ||
            One(user, "sub") is not { } subject || string.IsNullOrWhiteSpace(subject)) return null;
        if (!carrier) return new Actor(tenant, null, subject);
        return Guid.TryParse(One(user, "carrier_id"), out var id) && id != Guid.Empty
            ? new Actor(tenant, id, subject) : null;
    }

    private static string? One(ClaimsPrincipal user, string type) =>
        user.FindAll(type).Take(2).ToArray() is [var c] ? c.Value : null;

    public static bool Can(ClaimsPrincipal user, string roles) => roles.Split(',').Any(user.IsInRole);

    public static void Configure(WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration.GetSection("Identity");
        var allowHttp = cfg.GetValue<bool>("AllowHttp");
        var issuer = Authority(cfg["Authority"], allowHttp);
        var internalIssuer = Authority(cfg["InternalAuthority"] ?? issuer, allowHttp);
        var client = cfg["ClientId"] ?? "dockflow-web";
        var secret = cfg["ClientSecret"];
        if (string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("Identity:ClientSecret is required.");
        TokenValidationParameters Parameters(string audience) => new()
        {
            ValidateIssuer = true, ValidIssuer = issuer,
            IssuerValidator = (actual, _, _) => string.Equals(actual, issuer, StringComparison.Ordinal)
                ? actual : throw new SecurityTokenInvalidIssuerException("Unexpected issuer."),
            ValidateAudience = true, ValidAudience = audience, IgnoreTrailingSlashWhenValidatingAudience = false,
            RequireSignedTokens = true, ValidateIssuerSigningKey = true, ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30), RoleClaimType = "roles", NameClaimType = "sub"
        };
        builder.Services.AddAuthentication(o =>
        {
            o.DefaultScheme = Browser;
            o.DefaultChallengeScheme = Browser;
        }).AddCookie(Browser, o =>
        {
            o.LoginPath = "/entrar";
            o.Cookie.Name = "__Host-DockFlow";
            o.Cookie.Path = "/";
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.HttpOnly = true;
            o.SlidingExpiration = false;
            o.ExpireTimeSpan = TimeSpan.FromHours(2);
            o.Events.OnRedirectToLogin = c =>
            {
                if (c.Request.Path.StartsWithSegments("/hubs")) c.Response.StatusCode = 401;
                else c.Response.Redirect(c.RedirectUri);
                return Task.CompletedTask;
            };
            o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
        }).AddOpenIdConnect(Oidc, o =>
        {
            o.SignInScheme = Browser;
            o.Authority = issuer;
            o.ClientId = client;
            o.ClientSecret = secret;
            o.ResponseType = "code";
            o.ResponseMode = "query";
            o.UsePkce = true;
            o.SaveTokens = false;
            o.GetClaimsFromUserInfoEndpoint = false;
            o.MapInboundClaims = false;
            o.RequireHttpsMetadata = !allowHttp;
            // Keycloak does not advertise/support pushed authorization in every lab version.
            o.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
            o.BackchannelHttpHandler = new RealmBackchannel(issuer, internalIssuer);
            o.TokenValidationParameters = Parameters(client);
            o.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
            o.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Events.OnRemoteFailure = c =>
            {
                c.HandleResponse();
                c.Response.StatusCode = 400;
                return c.Response.WriteAsync("Não foi possível iniciar a sessão. Volte ao painel e tente novamente.");
            };
        }).AddJwtBearer(Bearer, o =>
        {
            o.Authority = issuer;
            o.RequireHttpsMetadata = !allowHttp;
            o.BackchannelHttpHandler = new RealmBackchannel(issuer, internalIssuer);
            o.MapInboundClaims = false;
            o.IncludeErrorDetails = false;
            o.TokenValidationParameters = Parameters("dockflow-api");
            o.Events = new JwtBearerEvents
            {
                OnTokenValidated = c =>
                {
                    // An ID token must never be usable as an access token, even if mis-issued with this audience.
                    if (c.Principal?.FindFirst("typ")?.Value != "Bearer") c.Fail("Expected a Keycloak access token.");
                    return Task.CompletedTask;
                }
            };
        });
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(Staff, p => p.AddAuthenticationSchemes(Browser).RequireAuthenticatedUser()
                .RequireRole(("GateOperator,DockOperator," + Managers).Split(','))
                .RequireAssertion(c => ActorFor(c.User) is not null))
            .AddPolicy(Carrier, p => p.AddAuthenticationSchemes(Bearer).RequireAuthenticatedUser().RequireRole("Carrier")
                .RequireAssertion(c => ActorFor(c.User, true) is not null));
    }

    private static string Authority(string? value, bool allowHttp)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !(uri.Scheme == "https" || (allowHttp && uri.Scheme == "http")) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath == "/")
            throw new InvalidOperationException("Configure a valid Identity authority. HTTP is permitted only in the lab.");
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private sealed class RealmBackchannel(string issuer, string destination) : DelegatingHandler(
        new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            // Only these well-known realm endpoints can receive backchannel requests or client credentials.
            var uri = req.RequestUri?.AbsoluteUri;
            var path = uri?.StartsWith(issuer + "/", StringComparison.Ordinal) == true ? uri[issuer.Length..] : null;
            if (path is not ("/.well-known/openid-configuration" or "/protocol/openid-connect/certs" or "/protocol/openid-connect/token"))
                throw new HttpRequestException("Identity backchannel endpoint not allowed.");
            req.RequestUri = new Uri(destination + path);
            return base.SendAsync(req, ct);
        }
    }
}
