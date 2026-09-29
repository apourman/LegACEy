using System;
using System.Globalization;
using System.Security.Claims;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

using ACE.Common;
using ACE.Common.Cryptography;
using ACE.Database.Models.Auth;

namespace ACE.MarketApi
{
    /// <summary>
    /// Web sign-in with the game account and password
    /// </summary>
    public static class AuthEndpoints
    {
        public sealed record LoginRequest(string Account, string Password);

        /// <summary>
        /// Verified against when the account doesn't exist, so an unknown name takes as long as a wrong password
        /// </summary>
        private static readonly Lazy<string> unknownAccountHash = new Lazy<string>(() => BCryptProvider.HashPassword(Guid.NewGuid().ToString(), Math.Clamp(ConfigManager.Config.Server.Accounts.PasswordHashWorkFactor, 4, 31)));

        public static void Map(WebApplication app)
        {
            app.MapPost("/auth/login", Login);
            // cast: a handler taking only HttpContext would otherwise bind as a RequestDelegate and drop its result
            app.MapPost("/auth/logout", (Delegate)Logout);
        }

        /// <summary>
        /// Order: IP block, account lock, password, ban. A locked attempt never checks the password, so it's refused even when right.
        /// Nothing here writes the account.
        /// </summary>
        private static async Task<IResult> Login(LoginRequest request, HttpContext context, MarketDatabase database, SignInLimiter limiter, TimeProvider time)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Account) || request.Password == null)
                return MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_request");

            var now = time.GetUtcNow();
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            if (limiter.IsIpBlocked(ip, now))
                return MarketHttp.Error(StatusCodes.Status429TooManyRequests, "ip_blocked");

            var account = await database.FindAccountAsync(request.Account);

            // an existing account is counted by id, whatever spelling the name was typed in (the column is case-insensitive)
            var accountKey = account != null ? "id:" + account.AccountId.ToString(CultureInfo.InvariantCulture) : "name:" + request.Account.Trim().ToLowerInvariant();

            if (limiter.IsAccountLocked(accountKey, now))
                return MarketHttp.Error(StatusCodes.Status429TooManyRequests, "account_locked");

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
                return MarketHttp.Error(StatusCodes.Status401Unauthorized, "invalid_credentials");
            }

            if (account.IsBanned(now.UtcDateTime))
                return MarketHttp.Error(StatusCodes.Status403Forbidden, "banned");

            limiter.RecordSuccess(accountKey);

            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, account.AccountId.ToString(CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, account.AccountName),
            }, CookieAuthenticationDefaults.AuthenticationScheme);

            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

            return Results.Json(new { accountId = account.AccountId, accountName = account.AccountName });
        }

        private static async Task<IResult> Logout(HttpContext context)
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

            return Results.Json(new { ok = true });
        }
    }
}
