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
    /// The BFF's sign-in: "Authorization: Bearer ws.…", a web session from POST /api/auth/session. The default authentication scheme, so a
    /// route that allows anonymous callers still sees a signed-in visitor, and in the default authorization policy with the plugin token, so every
    /// route that requires a signed-in account takes it.
    /// Each use reads the session and the account again. A ban in force revokes every web session of the account (MarketApi.NoticeBan) and
    /// refuses this one; a revoked session, one past its idle or absolute expiry, or one started under another password is refused.
    /// Otherwise a use slides the idle expiry, written at most once an hour (WebSessions.Touch).
    /// </summary>
    public sealed class WebSessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "WebSession";

        public const string SessionIdClaim = "web_session";

        /// <summary>
        /// The authorization policy that takes a web session and nothing else (sign-out)
        /// </summary>
        public const string PolicyName = "WebSessionOnly";

        public WebSessionAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var secret = MarketHttp.BearerToken(Request);

            // no bearer token, or a plugin token, which is the plugin scheme's
            if (!WebSessions.IsSessionToken(secret))
                return AuthenticateResult.NoResult();

            var database = Context.RequestServices.GetRequiredService<MarketDatabase>();
            var now = Context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;

            using var shard = database.CreateShard();

            var session = WebSessions.Find(shard, secret);

            if (session == null)
                return AuthenticateResult.Fail("unknown web session");

            var account = await database.FindAccountAsync(session.AccountId);

            if (account == null)
                return AuthenticateResult.Fail("web session's account no longer exists");

            // before the session's own checks, so even a dead session's request ends every other session of a banned account
            if (MarketApi.NoticeBan(database, account, now))
                return AuthenticateResult.Fail("account banned");

            if (!WebSessions.IsUsable(session, account.PasswordHash, now))
                return AuthenticateResult.Fail("web session revoked, expired or started before a password change");

            WebSessions.Touch(shard, session, now);

            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, account.AccountId.ToString(CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, account.AccountName),
                new Claim(SessionIdClaim, session.Id.ToString(CultureInfo.InvariantCulture)),
            }, SchemeName);

            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
        }

        /// <summary>
        /// The API's 401 JSON body, unless an earlier challenge already wrote it. In the default policy the plugin token scheme, challenged
        /// first, has named Bearer in WWW-Authenticate; on sign-out this is the only scheme.
        /// </summary>
        protected override Task HandleChallengeAsync(AuthenticationProperties properties) => MarketHttp.WriteUnauthorized(Response);
    }
}
