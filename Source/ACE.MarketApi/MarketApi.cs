using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using ACE.Common;
using ACE.Database.Market;
using ACE.Database.Models.Auth;

namespace ACE.MarketApi
{
    /// <summary>
    /// The Market API: its own process, on ACE's configuration and ACE.Database. It changes market tables only, never items.
    /// </summary>
    public static class MarketApi
    {
        public const string SessionCookieName = "market_session";

        /// <summary>
        /// Every route is served under this path; the website owns the rest of the origin
        /// </summary>
        public const string PathBase = "/api";

        /// <summary>
        /// Builds the API. Throws MarketUnavailableException, and so never starts, when the market's tables or the portal DAT are missing.
        /// </summary>
        /// <param name="configure">runs first, so a test host can swap in its own server, clock or services</param>
        public static WebApplication Create(string[] args, Action<WebApplicationBuilder> configure = null)
        {
            var builder = WebApplication.CreateBuilder(args);

            configure?.Invoke(builder);

            var options = builder.Configuration.GetSection("Market").Get<MarketApiOptions>() ?? new MarketApiOptions();

            // first: without the service key the API would refuse everything, so it doesn't start
            var gate = ServiceGate.For(options.ServiceKey);

            if (ConfigManager.Config == null)
                ConfigManager.Initialize(options.AceConfigPath);

            var database = new MarketDatabase(ConfigManager.Config.MySql, options.AuthDatabase, options.ShardDatabase, options.DatabaseUsername, options.DatabasePassword);

            // the update runner marks even a failed script as applied, so check the tables ourselves
            using (var shard = database.CreateShard())
            {
                var check = MarketSchema.Check(shard);

                if (check.Status != MarketSchemaStatus.Ok)
                    throw new MarketUnavailableException($"Market schema check: {check.Report}. The market will not start until the market update script has been applied.");
            }

            // the appraisal names spells and materials from the game's own data files
            GameData gameData;
            try
            {
                gameData = GameData.Load(ConfigManager.Config.Server.DatFilesDirectory);
            }
            catch (FileNotFoundException e)
            {
                throw new MarketUnavailableException(e.Message);
            }

            AddJson(builder.Services);
            builder.Services.AddSingleton(database);
            builder.Services.AddSingleton(gameData);
            builder.Services.AddSingleton(new IconStore(gameData, options.IconCachePath));
            builder.Services.TryAddSingleton(AppraisalRules.Default);
            builder.Services.AddSingleton<SignInLimiter>();
            builder.Services.AddSingleton<PurchaseLimiter>();
            builder.Services.TryAddSingleton<IFeePolicy, ZeroFeePolicy>();
            builder.Services.TryAddSingleton<IMarketPause, DatabaseMarketPause>();
            builder.Services.TryAddSingleton(LedgerAuditSchedule.Default);
            builder.Services.AddHostedService<LedgerAuditService>();
            builder.Services.TryAddSingleton(TimeProvider.System);

            builder.Services.AddDataProtection()
                .SetApplicationName("ACE.MarketApi")
                .PersistKeysToFileSystem(new DirectoryInfo(options.KeysPath));

            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(cookie =>
                {
                    cookie.Cookie.Name = SessionCookieName;
                    cookie.Cookie.HttpOnly = true;
                    cookie.Cookie.SameSite = SameSiteMode.Lax;
                    cookie.Cookie.SecurePolicy = options.SecureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
                    cookie.ExpireTimeSpan = TimeSpan.FromDays(14);
                    cookie.SlidingExpiration = true;

                    // a JSON API: answer 401/403 instead of redirecting to a login page
                    cookie.Events.OnRedirectToLogin = context => MarketHttp.WriteError(context.Response, StatusCodes.Status401Unauthorized, "unauthorized");
                    cookie.Events.OnRedirectToAccessDenied = context => MarketHttp.WriteError(context.Response, StatusCodes.Status403Forbidden, "forbidden");
                    cookie.Events.OnValidatePrincipal = ValidateSession;
                })
                .AddScheme<AuthenticationSchemeOptions, PluginTokenAuthenticationHandler>(PluginTokenAuthenticationHandler.SchemeName, null)
                .AddScheme<AuthenticationSchemeOptions, WebSessionAuthenticationHandler>(WebSessionAuthenticationHandler.SchemeName, null);

            // RequireAuthorization takes the plugin's bearer token, the BFF's web session bearer token or the website's cookie. The bearer schemes
            // are challenged first and write no body (only the 401 status and WWW-Authenticate), so the cookie scheme writes the 401 body.
            builder.Services.AddAuthorization(authorization =>
                authorization.DefaultPolicy = new AuthorizationPolicyBuilder(PluginTokenAuthenticationHandler.SchemeName, WebSessionAuthenticationHandler.SchemeName, CookieAuthenticationDefaults.AuthenticationScheme)
                    .RequireAuthenticatedUser()
                    .Build());

            // behind a reverse proxy the connection's IP is the proxy's; trust X-Forwarded-For from the configured proxies only
            if (options.TrustedProxies.Length > 0)
            {
                builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
                {
                    forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

                    foreach (var proxy in options.TrustedProxies)
                        forwarded.KnownProxies.Add(IPAddress.Parse(proxy));
                });
            }

            // answers name no server software
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);

            var app = builder.Build();

            // the connection's address only: X-Forwarded-For from a configured proxy. Removed with TrustedProxies once the BFF is the only caller.
            if (options.TrustedProxies.Length > 0)
                app.UseForwardedHeaders();

            // health, the service key and X-Market-Client-Ip, before routing and authentication. UseRouting is explicit so that
            // WebApplication doesn't add it at the start of the pipeline, ahead of the gate.
            app.Use((HttpContext context, RequestDelegate next) => gate.InvokeAsync(context, () => next(context)));
            app.UseRouting();

            app.UseAuthentication();
            app.UseAuthorization();

            // the website and the API share one origin: the website owns /, the API /api. No CORS policy, so other origins can't read answers.
            MapEndpoints(app);

            return app;
        }

        /// <summary>
        /// The API's JSON, for MarketApi.Create and the OpenAPI generator alike: ASP.NET's web defaults, stated. Requests may quote numbers
        /// ("3" reads as 3), which a game plugin may rely on, so this stays lenient; answers always write numbers.
        /// </summary>
        public static void AddJson(IServiceCollection services) =>
            services.ConfigureHttpJsonOptions(json =>
            {
                json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                json.SerializerOptions.NumberHandling = JsonNumberHandling.AllowReadingFromString;
            });

        /// <summary>
        /// Maps every route under /api with the CSRF filter. Mapping needs the services registered, not the database or the DATs,
        /// so the OpenAPI generator (MarketOpenApi) maps the same routes on placeholders.
        /// </summary>
        public static void MapEndpoints(IEndpointRouteBuilder app)
        {
            var api = app.MapGroup(PathBase);
            api.AddEndpointFilter(RequireRequestHeader);

            // the document shows the answers the group adds: 401 where sign-in is required, and 403 csrf where RequireRequestHeader can refuse.
            // A finally convention runs after each route's own conventions, so it sees RequireAuthorization.
            ((IEndpointConventionBuilder)api).Finally(endpoint =>
            {
                if (endpoint.Metadata.OfType<IAuthorizeData>().Any())
                    DeclareError(endpoint, StatusCodes.Status401Unauthorized);

                if (endpoint.Metadata.OfType<HttpMethodMetadata>().SelectMany(m => m.HttpMethods).Any(IsChange))
                    DeclareError(endpoint, StatusCodes.Status403Forbidden);
            });

            AuthEndpoints.Map(api);
            AccountEndpoints.Map(api);
            ListingEndpoints.Map(api);
            CatalogEndpoints.Map(api);
            PurchaseEndpoints.Map(api);
            HistoryEndpoints.Map(api);
            PluginTokenEndpoints.Map(api);
            IconEndpoints.Map(api);
            TicketEndpoints.Map(api);
        }

        /// <summary>
        /// The header every website request carries. Another site can make a browser send the session cookie, but not a custom header,
        /// without a CORS preflight the API never approves.
        /// </summary>
        public const string RequestHeader = "X-Market-Request";

        /// <summary>
        /// CSRF: a request that changes something and is signed in by the session cookie must carry X-Market-Request: 1, or it's refused (403 csrf).
        /// A request signed in by a bearer token (a plugin token or a web session) is exempt: a browser never sends one on its own.
        /// </summary>
        private static async ValueTask<object> RequireRequestHeader(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
        {
            var context = invocation.HttpContext;

            if (!IsChange(context.Request.Method))
                return await next(invocation);

            if (context.Request.Headers[RequestHeader] == "1")
                return await next(invocation);

            // both are cached for the request, so the authorization middleware's own authentication isn't repeated
            if ((await context.AuthenticateAsync(PluginTokenAuthenticationHandler.SchemeName)).Succeeded)
                return await next(invocation);

            if ((await context.AuthenticateAsync(WebSessionAuthenticationHandler.SchemeName)).Succeeded)
                return await next(invocation);

            if (!(await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme)).Succeeded)
                return await next(invocation);

            return MarketHttp.Error(StatusCodes.Status403Forbidden, "csrf");
        }

        /// <summary>
        /// Every method but GET, HEAD, OPTIONS and TRACE may change something
        /// </summary>
        private static bool IsChange(string method) =>
            !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method));

        private static void DeclareError(EndpointBuilder endpoint, int statusCode)
        {
            if (!endpoint.Metadata.OfType<IProducesResponseTypeMetadata>().Any(m => m.StatusCode == statusCode))
                endpoint.Metadata.Add(new ProducesResponseTypeMetadata(statusCode, typeof(ApiError), new[] { "application/json" }));
        }

        /// <summary>
        /// True when the account is banned now. The market has then noticed the ban: the account's listings go back to its Vault, and every
        /// web session of the account is revoked for good. Every signed-in request (cookie, plugin token or web session) asks this.
        /// </summary>
        public static bool NoticeBan(MarketDatabase database, Account account, DateTime now)
        {
            if (!account.IsBanned(now))
                return false;

            MarketUpkeep.NoticeBans(database, new[] { account.AccountId }, now);
            return true;
        }

        /// <summary>
        /// Bans are checked on every request, so a session stops working as soon as a ban is in force
        /// </summary>
        private static async Task ValidateSession(CookieValidatePrincipalContext context)
        {
            var services = context.HttpContext.RequestServices;
            var now = services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;

            Account account = null;

            if (MarketHttp.TryGetAccountId(context.Principal, out var accountId))
                account = await services.GetRequiredService<MarketDatabase>().FindAccountAsync(accountId);

            if (account == null || NoticeBan(services.GetRequiredService<MarketDatabase>(), account, now))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        }
    }

    /// <summary>
    /// The market can't run: its tables or the game's data files are missing
    /// </summary>
    public sealed class MarketUnavailableException : Exception
    {
        public MarketUnavailableException(string message) : base(message) { }
    }
}
