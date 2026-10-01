using System;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

using ACE.Database.Market;
using ACE.Database.Models.Auth;

namespace ACE.MarketApi
{
    /// <summary>
    /// Plugin sign-in: a /vault link code exchanged for a token, and the account's token list and revoke (the website's, also open to the plugin)
    /// </summary>
    public static class PluginTokenEndpoints
    {
        public sealed record PluginTokenRequest(string Code, string Label);

        public static void Map(WebApplication app)
        {
            app.MapPost("/auth/plugin-token", Exchange);
            app.MapGet("/tokens", ListTokens).RequireAuthorization();
            app.MapPost("/tokens/{id:long}/revoke", Revoke).RequireAuthorization();
        }

        /// <summary>
        /// Order: request shape, IP block, code, account and ban, then one save that uses up the code and stores the token.
        /// Unknown, used and expired codes all answer invalid_code, and each counts toward the IP's sign-in block, so codes can't be guessed at speed.
        /// A banned account's code is refused without being used up.
        /// </summary>
        private static async Task<IResult> Exchange(PluginTokenRequest request, HttpContext context, MarketDatabase database, SignInLimiter limiter, TimeProvider time)
        {
            var label = string.IsNullOrWhiteSpace(request?.Label) ? null : request.Label.Trim();

            if (request == null || string.IsNullOrWhiteSpace(request.Code) || label?.Length > PluginAuth.LabelMaxLength)
                return MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_request");

            var now = time.GetUtcNow();
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            if (limiter.IsIpBlocked(ip, now))
                return MarketHttp.Error(StatusCodes.Status429TooManyRequests, "ip_blocked");

            using var shard = database.CreateShard();

            var linkCode = PluginAuth.FindLinkCode(shard, request.Code, now.UtcDateTime);

            if (linkCode == null)
            {
                limiter.RecordIpFailure(ip, SignInLimits.Read(shard).Ip, now);
                return MarketHttp.Error(StatusCodes.Status401Unauthorized, "invalid_code");
            }

            var account = await database.FindAccountAsync(linkCode.AccountId);

            if (account == null)
                return MarketHttp.Error(StatusCodes.Status401Unauthorized, "invalid_code");

            if (account.IsBanned(now.UtcDateTime))
                return MarketHttp.Error(StatusCodes.Status403Forbidden, "banned");

            var token = PluginAuth.Redeem(shard, linkCode, label, account.PasswordHash, now.UtcDateTime, out var secret);

            // another exchange of the same code saved first
            if (token == null)
                return MarketHttp.Error(StatusCodes.Status401Unauthorized, "invalid_code");

            return Results.Json(new
            {
                token = secret,
                tokenId = token.Id,
                label = token.Label,
                expiresTime = Utc(token.ExpiresTime),
            });
        }

        /// <summary>
        /// The account's working tokens, newest first. Never the token or its hash.
        /// </summary>
        private static async Task<IResult> ListTokens(HttpContext context, MarketDatabase database, TimeProvider time)
        {
            var account = await database.FindAccountAsync(MarketHttp.AccountId(context));

            if (account == null)
                return MarketHttp.Error(StatusCodes.Status401Unauthorized, "unauthorized");

            using var shard = database.CreateShard();

            var tokens = PluginAuth.ListTokens(shard, account.AccountId, account.PasswordHash, time.GetUtcNow().UtcDateTime)
                .Select(t => new
                {
                    id = t.Id,
                    label = t.Label,
                    createdTime = Utc(t.CreatedTime),
                    lastUsedTime = t.LastUsedTime is DateTime used ? Utc(used) : (DateTime?)null,
                    expiresTime = Utc(t.ExpiresTime),
                });

            return Results.Json(new { tokens });
        }

        /// <summary>
        /// Revokes one of the account's tokens; it stops working on its next request. 404 for another account's token or an unknown id.
        /// </summary>
        private static IResult Revoke(long id, HttpContext context, MarketDatabase database, TimeProvider time)
        {
            using var shard = database.CreateShard();

            if (!PluginAuth.Revoke(shard, MarketHttp.AccountId(context), id, time.GetUtcNow().UtcDateTime))
                return MarketHttp.Error(StatusCodes.Status404NotFound, "not_found");

            return Results.Json(new { ok = true });
        }

        private static DateTime Utc(DateTime stored) => DateTime.SpecifyKind(stored, DateTimeKind.Utc);
    }
}
