using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;

namespace ACE.MarketApi
{
    /// <summary>
    /// The API's front door, ahead of routing and authentication. The API is private: only the BFF calls it.
    /// <list type="bullet">
    /// <item>GET or HEAD /health answers a bare "ok", with or without the key, and nothing else.</item>
    /// <item>Every other request needs X-Market-Service-Key, compared in constant time with the configured key; a missing or wrong key gets
    /// a bare 401 (no body, no challenge header), so the answer says nothing about the API behind it.</item>
    /// <item>On a request with the right key, X-Market-Client-Ip (the player's address, as the BFF saw it) becomes the connection's remote address,
    /// so the sign-in limits and logging count the player rather than the BFF. Without it, the connection address is used (after the
    /// TrustedProxies forwarded headers, when configured). A value that isn't one plain IP address gets a bare 400: guessing would pool players.</item>
    /// </list>
    /// </summary>
    public sealed class ServiceGate
    {
        public const string KeyHeader = "X-Market-Service-Key";

        public const string ClientIpHeader = "X-Market-Client-Ip";

        public const string HealthPath = "/health";

        /// <summary>
        /// The shortest key the API starts with (32 random bytes as hex is 64 characters)
        /// </summary>
        public const int MinimumKeyLength = 32;

        /// <summary>
        /// The longest text address: IPv6 with an embedded IPv4 address
        /// </summary>
        private const int MaxAddressLength = 45;

        /// <summary>
        /// Only the key's hash is kept, and presented keys are hashed before comparing, so the comparison takes the same time whatever the
        /// presented key's length
        /// </summary>
        private readonly byte[] keyHash;

        private ServiceGate(string serviceKey)
        {
            keyHash = Hash(serviceKey);
        }

        /// <summary>
        /// The gate for the configured key. Throws MarketUnavailableException, so the API never starts, when the key is missing or too short.
        /// </summary>
        public static ServiceGate For(string serviceKey)
        {
            if (string.IsNullOrWhiteSpace(serviceKey))
                throw new MarketUnavailableException("Market:ServiceKey is not set. The Market API refuses every request without the BFF's service key, so it will not start without one. " +
                    "Locally, set MARKET_SERVICE_KEY in docker.env (for example: openssl rand -hex 32).");

            if (serviceKey.Length < MinimumKeyLength)
                throw new MarketUnavailableException($"Market:ServiceKey is shorter than {MinimumKeyLength} characters. Use a long random secret (for example: openssl rand -hex 32).");

            return new ServiceGate(serviceKey);
        }

        public async Task InvokeAsync(HttpContext context, Func<Task> next)
        {
            var request = context.Request;

            if (request.Path.Equals(HealthPath, StringComparison.OrdinalIgnoreCase) && (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)))
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/plain";

                if (HttpMethods.IsGet(request.Method))
                    await context.Response.WriteAsync("ok");

                return;
            }

            var presented = request.Headers[KeyHeader];

            if (presented.Count != 1 || !IsKey(presented[0]))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var clientIp = request.Headers[ClientIpHeader];

            if (clientIp.Count > 0)
            {
                if (clientIp.Count != 1 || !TryParseClientIp(clientIp[0], out var address))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                context.Connection.RemoteIpAddress = address;
            }

            await next();
        }

        private bool IsKey(string presented) => CryptographicOperations.FixedTimeEquals(Hash(presented ?? ""), keyHash);

        private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

        /// <summary>
        /// One IPv4 address in its usual dotted form, or one IPv6 address without a zone. An IPv4-mapped IPv6 address counts as the IPv4 address,
        /// so one player isn't two keys in the sign-in limits. IPv6 addresses are kept whole: limits are per address, not per /64.
        /// </summary>
        public static bool TryParseClientIp(string text, out IPAddress address)
        {
            address = null;

            // no zone ("%eth0"), brackets or port: TryParse would take "[2001:db8::1]:443" and drop the port
            if (string.IsNullOrEmpty(text) || text.Length > MaxAddressLength || text.IndexOfAny(new[] { '%', '[', ']' }) >= 0 || !IPAddress.TryParse(text, out var parsed))
                return false;

            if (parsed.AddressFamily == AddressFamily.InterNetwork)
            {
                // TryParse also takes "10.1", "167772161" and "010.0.0.1" (octal); only the dotted form it would write itself is accepted
                if (parsed.ToString() != text)
                    return false;
            }
            else if (parsed.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (!text.Contains(':'))
                    return false;

                if (parsed.IsIPv4MappedToIPv6)
                    parsed = parsed.MapToIPv4();
            }
            else
                return false;

            address = parsed;
            return true;
        }
    }
}
