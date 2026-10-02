using System;
using System.Globalization;
using System.Security.Claims;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using ACE.Common;
using ACE.Common.Cryptography;
using ACE.Database.Market;
using ACE.Database.Models.Auth;

namespace ACE.MarketApi
{
    /// <summary>
    /// Web sign-in with the game account and password: the website's cookie (until the BFF replaces it), and the BFF's web sessions
    /// </summary>
    public static class AuthEndpoints
    {
        public sealed record LoginRequest(string Account, string Password);

        /// <summary>
        /// Verified against when the account doesn't exist, so an unknown name takes as long as a wrong password
        /// </summary>
        private static readonly Lazy<string> unknownAccountHash = new Lazy<string>(() => BCryptProvider.HashPassword(Guid.NewGuid().ToString(), Math.Clamp(ConfigManager.Config.Server.Accounts.PasswordHashWorkFactor, 4, 31)));

        public static void Map(IEndpointRouteBuilder app)
        {
            app.MapPost("/auth/login", Login).Json<LoginResponse>(200, 400, 401, 403, 429);
            // cast: a handler taking only HttpContext would otherwise bind as a RequestDelegate and drop its result
            app.MapPost("/auth/logout", (Delegate)Logout).Json<OkResponse>();

            app.MapPost("/auth/session", SignIn).Json<SessionResponse>(200, 400, 401, 403, 429);
            app.MapDelete("/auth/session", SignOut).Json<OkResponse>().RequireAuthorization();
        }

        /// <summary>
        /// The website's sign-in: sets the session cookie
        /// </summary>
        private static async Task<IResult> Login(LoginRequest request, HttpContext context, MarketDatabase database, SignInLimiter limiter, TimeProvider time)
        {
            var (refusal, account) = await CheckPassword(request, context, database, limiter, time);

            if (refusal != null)
                return refusal;

            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, account.AccountId.ToString(CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, account.AccountName),
            }, CookieAuthenticationDefaults.AuthenticationScheme);

            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

            return Results.Json(new LoginResponse(account.AccountId, account.AccountName));
        }

        private static async Task<IResult> Logout(HttpContext context)
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

            return Results.Json(new OkResponse(true));
        }

        /// <summary>
        /// The BFF's sign-in: the same checks as the website's, with the client IP from X-Market-Client-Ip (ServiceGate). Starts a web session and
        /// returns its token, which is stored only as a hash, and its expiry times. Sets no cookie: the BFF keeps the token in its own.
        /// </summary>
        private static async Task<IResult> SignIn(LoginRequest request, HttpContext context, MarketDatabase database, SignInLimiter limiter, TimeProvider time)
        {
            var (refusal, account) = await CheckPassword(request, context, database, limiter, time);

            if (refusal != null)
                return refusal;

            using var shard = database.CreateShard();

            var session = WebSessions.Create(shard, account.AccountId, account.PasswordHash, time.GetUtcNow().UtcDateTime, out var token);

            return Results.Json(new SessionResponse(token, account.AccountId, account.AccountName, MarketHttp.Utc(session.IdleExpiresTime), MarketHttp.Utc(session.AbsoluteExpiresTime)));
        }

        /// <summary>
        /// Revokes the web session the request is signed in with; its token is refused from then on. A cookie or plugin token has no web session
        /// to end, so it gets 401.
        /// </summary>
        private static IResult SignOut(HttpContext context, MarketDatabase database, TimeProvider time)
        {
            if (!long.TryParse(context.User.FindFirstValue(WebSessionAuthenticationHandler.SessionIdClaim), NumberStyles.None, CultureInfo.InvariantCulture, out var sessionId))
                return MarketHttp.Error(StatusCodes.Status401Unauthorized, "unauthorized");

            using var shard = database.CreateShard();
            WebSessions.Revoke(shard, sessionId, time.GetUtcNow().UtcDateTime);

            return Results.Json(new OkResponse(true));
        }

        /// <summary>
        /// Order: IP block, account lock, password, ban. A locked attempt never checks the password, so it's refused even when right.
        /// Nothing here writes the account. A ban seen here is noticed (MarketApi.NoticeBan), so the account's web sessions end.
        /// Returns the refusal, or the account whose password matched.
        /// </summary>
        private static async Task<(IResult Refusal, Account Account)> CheckPassword(LoginRequest request, HttpContext context, MarketDatabase database, SignInLimiter limiter, TimeProvider time)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Account) || request.Password == null)
                return (MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_request"), null);

            var now = time.GetUtcNow();
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            if (limiter.IsIpBlocked(ip, now))
                return (MarketHttp.Error(StatusCodes.Status429TooManyRequests, "ip_blocked"), null);

            var account = await database.FindAccountAsync(request.Account);

            // an existing account is counted by id, whatever spelling the name was typed in (the column is case-insensitive)
            var accountKey = account != null ? "id:" + account.AccountId.ToString(CultureInfo.InvariantCulture) : "name:" + request.Account.Trim().ToLowerInvariant();

            if (limiter.IsAccountLocked(accountKey, now))
                return (MarketHttp.Error(StatusCodes.Status429TooManyRequests, "account_locked"), null);

            bool passwordMatches;

            if (account != null)
                passwordMatches = account.PasswordMatchesReadOnly(request.Password);
            else
            {
                BCryptProvider.Verify(request.Password, unknownAccountHash.Value);
                passwordMatches = false;
            }

            if (!passwordMatches)
            {
                SignInLimits limits;
                using (var shard = database.CreateShard())
                    limits = SignInLimits.Read(shard);

                limiter.RecordFailure(accountKey, ip, limits, now);
                return (MarketHttp.Error(StatusCodes.Status401Unauthorized, "invalid_credentials"), null);
            }

            if (MarketApi.NoticeBan(database, account, now.UtcDateTime))
                return (MarketHttp.Error(StatusCodes.Status403Forbidden, "banned"), null);

            limiter.RecordSuccess(accountKey);

            return (null, account);
        }
    }
}
