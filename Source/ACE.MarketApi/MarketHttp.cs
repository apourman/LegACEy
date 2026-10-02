using System;
using System.Security.Claims;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace ACE.MarketApi
{
    /// <summary>
    /// The session's account id claim, and the API's JSON error shape: { "error": "code" }
    /// </summary>
    public static class MarketHttp
    {
        public static bool TryGetAccountId(ClaimsPrincipal principal, out uint accountId)
        {
            accountId = 0;
            return principal != null && uint.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out accountId);
        }

        /// <summary>
        /// The signed-in account's id, for endpoints that require authorization
        /// </summary>
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

        /// <summary>
        /// A price in whole MMD, at least 1. Prices arrive as decimals so that 1.5 is refused as a price rather than as unreadable JSON.
        /// </summary>
        public static bool TryWholeMmd(decimal? value, out long mmd)
        {
            mmd = 0;

            if (value is not decimal price || price < 1 || price != decimal.Truncate(price) || price > long.MaxValue)
                return false;

            mmd = (long)price;
            return true;
        }

        /// <summary>
        /// A query parameter's trimmed text, or null when it's absent or blank
        /// </summary>
        public static string QueryValue(StringValues value)
        {
            var text = value.ToString().Trim();

            return text.Length == 0 ? null : text;
        }

        public static IResult Error(int statusCode, string error) => Results.Json(new ApiError(error), statusCode: statusCode);

        /// <summary>
        /// The longest idempotency key a request may carry (the key columns hold 64 characters)
        /// </summary>
        public const int MaxIdempotencyKeyLength = 64;

        public static bool IsIdempotencyKey(string key) => !string.IsNullOrEmpty(key) && key.Length <= MaxIdempotencyKeyLength;

        /// <summary>
        /// A stored time as UTC: datetime(6) columns are UTC by convention, and EF reads them back unspecified
        /// </summary>
        public static DateTime Utc(DateTime stored) => DateTime.SpecifyKind(stored, DateTimeKind.Utc);

        public static DateTime? Utc(DateTime? stored) => stored == null ? null : Utc(stored.Value);
    }
}
