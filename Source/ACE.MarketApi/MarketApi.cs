using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
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
        /// <summary>
        /// Every route is served under this path; the BFF forwards its allowlisted /api/* routes as they are
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

            // Signed in by "Authorization: Bearer" only: the BFF's web session (the default scheme, so a route that allows anonymous callers still
            // sees a signed-in visitor) or the plugin's token. The API keeps no cookie and has no CSRF check: browsers never reach it, and a
            // browser never sends a bearer token on its own. Cross-site protection lives in the BFF.
            builder.Services.AddAuthentication(WebSessionAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, PluginTokenAuthenticationHandler>(PluginTokenAuthenticationHandler.SchemeName, null)
                .AddScheme<AuthenticationSchemeOptions, WebSessionAuthenticationHandler>(WebSessionAuthenticationHandler.SchemeName, null);

            // RequireAuthorization takes either bearer token. Each challenge that can answer writes the 401 JSON body only if no earlier one has
            // (MarketHttp.WriteUnauthorized), so the scheme order can't leave it empty.
            builder.Services.AddAuthorization(authorization =>
            {
                authorization.DefaultPolicy = new AuthorizationPolicyBuilder(BearerSchemes)
                    .RequireAuthenticatedUser()
                    .Build();

                authorization.AddPolicy(WebSessionAuthenticationHandler.PolicyName, policy => policy
                    .AddAuthenticationSchemes(WebSessionAuthenticationHandler.SchemeName)
                    .RequireAuthenticatedUser());
            });

            // answers name no server software
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);

            var app = builder.Build();

            // health, the service key and X-Market-Client-Ip, before routing and authentication. UseRouting is explicit so that
            // WebApplication doesn't add it at the start of the pipeline, ahead of the gate.
            app.Use((HttpContext context, RequestDelegate next) => gate.InvokeAsync(context, () => next(context)));
            app.UseRouting();

            app.UseAuthentication();
            app.UseAuthorization();

            // the API is private: only the BFF calls it, on the network they share. No CORS policy, and X-Forwarded-For means nothing here:
            // the player's address comes from the BFF's X-Market-Client-Ip (ServiceGate).
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
        /// Maps every route under /api. Mapping needs the services registered, not the database or the DATs, so the OpenAPI generator
        /// (MarketOpenApi) maps the same routes on placeholders.
        /// </summary>
        public static void MapEndpoints(IEndpointRouteBuilder app)
        {
            var api = app.MapGroup(PathBase);

            // the document shows the 401 the group adds where sign-in is required. A finally convention runs after each route's own
            // conventions, so it sees RequireAuthorization.
            ((IEndpointConventionBuilder)api).Finally(endpoint =>
            {
                if (endpoint.Metadata.OfType<IAuthorizeData>().Any())
                    DeclareError(endpoint, StatusCodes.Status401Unauthorized);
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
        /// The schemes signed in by "Authorization: Bearer", in the order they're challenged in the default policy. A new bearer scheme goes here.
        /// </summary>
        private static readonly string[] BearerSchemes = { PluginTokenAuthenticationHandler.SchemeName, WebSessionAuthenticationHandler.SchemeName };

        private static void DeclareError(EndpointBuilder endpoint, int statusCode)
        {
            if (!endpoint.Metadata.OfType<IProducesResponseTypeMetadata>().Any(m => m.StatusCode == statusCode))
                endpoint.Metadata.Add(new ProducesResponseTypeMetadata(statusCode, typeof(ApiError), new[] { "application/json" }));
        }

        /// <summary>
        /// True when the account is banned now. The market has then noticed the ban: the account's listings go back to its Vault, and every
        /// web session of the account is revoked for good. Every signed-in request (web session or plugin token) asks this.
        /// </summary>
        public static bool NoticeBan(MarketDatabase database, Account account, DateTime now)
        {
            if (!account.IsBanned(now))
                return false;

            MarketUpkeep.ReturnListingsAndEndWebSessions(database, new[] { account.AccountId }, now);
            return true;
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
