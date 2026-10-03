using System;
using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ACE.Database.Market;

namespace ACE.MarketApi
{
    /// <summary>
    /// The plugin's sign-in: "Authorization: Bearer &lt;token&gt;", next to the website's cookie. Both are in the default authorization policy,
    /// so every route that requires a signed-in account takes either.
    /// Each use reads the token and the account again: a revoked or expired token, a changed password (the fingerprint no longer matches)
    /// or a ban in force refuses it. A use renews the token's lifetime.
    /// </summary>
    public sealed class PluginTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "PluginToken";

        public const string TokenIdClaim = "plugin_token";

        public PluginTokenAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var secret = MarketHttp.BearerToken(Request);

            // no bearer token, or a web session, which is the web session scheme's
            if (secret == null || WebSessions.IsSessionToken(secret))
                return AuthenticateResult.NoResult();

            var database = Context.RequestServices.GetRequiredService<MarketDatabase>();
            var now = Context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;

            using var shard = database.CreateShard();

            var token = PluginAuth.FindToken(shard, secret);

            if (token == null)
                return AuthenticateResult.Fail("unknown plugin token");

            var account = await database.FindAccountAsync(token.AccountId);

            if (account == null || !PluginAuth.IsUsable(token, account.PasswordHash, now))
                return AuthenticateResult.Fail("plugin token revoked, expired or issued before a password change");

            if (MarketApi.NoticeBan(database, account, now))
                return AuthenticateResult.Fail("account banned");

            PluginAuth.Renew(shard, token, now);

            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, account.AccountId.ToString(CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, account.AccountName),
                new Claim(TokenIdClaim, token.Id.ToString(CultureInfo.InvariantCulture)),
            }, SchemeName);

            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
        }

        /// <summary>
        /// Names the scheme the caller can use. The cookie scheme, challenged after this one, writes the 401 and its JSON body.
        /// </summary>
        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            if (!Response.HasStarted)
                Response.Headers.WWWAuthenticate = "Bearer";

            return Task.CompletedTask;
        }
    }
}
