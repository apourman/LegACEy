using System;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
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
        /// Builds the API. Throws MarketUnavailableException, and so never starts, when the market's tables are missing.
        /// </summary>
        /// <param name="configure">runs first, so a test host can swap in its own server, clock or services</param>
        public static WebApplication Create(string[] args, Action<WebApplicationBuilder> configure = null)
        {
            var builder = WebApplication.CreateBuilder(args);

            configure?.Invoke(builder);

            var options = builder.Configuration.GetSection("Market").Get<MarketApiOptions>() ?? new MarketApiOptions();

            if (ConfigManager.Config == null)
                ConfigManager.Initialize(options.AceConfigPath);

            var database = new MarketDatabase(ConfigManager.Config.MySql, options.AuthDatabase, options.ShardDatabase);

            // the update runner marks even a failed script as applied, so check the tables ourselves
            using (var shard = database.CreateShard())
            {
                var check = MarketSchema.Check(shard);

                if (check.Status != MarketSchemaStatus.Ok)
                    throw new MarketUnavailableException($"Market schema check: {check.Report}. The market will not start until the market update script has been applied.");
            }

            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(database);
            builder.Services.AddSingleton<SignInLimiter>();
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
                    cookie.Events.OnRedirectToLogin = context => WriteError(context.Response, StatusCodes.Status401Unauthorized, "unauthorized");
                    cookie.Events.OnRedirectToAccessDenied = context => WriteError(context.Response, StatusCodes.Status403Forbidden, "forbidden");
                    cookie.Events.OnValidatePrincipal = ValidateSession;
                });

            builder.Services.AddAuthorization();

            var app = builder.Build();

            app.UseAuthentication();
            app.UseAuthorization();

            AuthEndpoints.Map(app);
            AccountEndpoints.Map(app);

            return app;
        }

        /// <summary>
        /// Bans are checked on every request, so a session stops working as soon as a ban is in force
        /// </summary>
        private static async Task ValidateSession(CookieValidatePrincipalContext context)
        {
            var services = context.HttpContext.RequestServices;
            var now = services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;

            Account account = null;

            if (TryGetAccountId(context.Principal, out var accountId))
            {
                using var auth = services.GetRequiredService<MarketDatabase>().CreateAuth();
                account = await auth.Account.AsNoTracking().FirstOrDefaultAsync(a => a.AccountId == accountId);
            }

            if (account == null || account.IsBanned(now))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        }

        public static bool TryGetAccountId(ClaimsPrincipal principal, out uint accountId)
        {
            accountId = 0;
            return principal != null && uint.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out accountId);
        }

        public static uint AccountId(HttpContext context)
        {
            if (!TryGetAccountId(context.User, out var accountId))
                throw new InvalidOperationException("no signed-in account");

            return accountId;
        }

        public static Task WriteError(HttpResponse response, int statusCode, string error)
        {
            response.StatusCode = statusCode;
            return response.WriteAsJsonAsync(new { error });
        }

        public static IResult Error(int statusCode, string error) => Results.Json(new { error }, statusCode: statusCode);
    }

    /// <summary>
    /// The market can't run: its tables are missing
    /// </summary>
    public sealed class MarketUnavailableException : Exception
    {
        public MarketUnavailableException(string message) : base(message) { }
    }
}
