namespace ACE.MarketApi
{
    /// <summary>
    /// The API's own settings, the "Market" section of its configuration (appsettings.json, environment MARKET__*, or --Market:Key=value).
    /// Everything else, the MySQL servers and credentials included, comes from ACE's Config.js.
    /// </summary>
    public sealed class MarketApiOptions
    {
        /// <summary>
        /// ACE's Config.js. A bare file name is looked for in the working directory, then next to the API.
        /// </summary>
        public string AceConfigPath { get; set; } = "Config.js";

        /// <summary>
        /// Overrides Config.js's auth database name (tests use scratch databases)
        /// </summary>
        public string AuthDatabase { get; set; }

        /// <summary>
        /// Overrides Config.js's shard database name (tests use scratch databases)
        /// </summary>
        public string ShardDatabase { get; set; }

        /// <summary>
        /// Where the cookie signing keys are kept. Put it on a volume, so a restart or redeploy doesn't sign everyone out.
        /// </summary>
        public string KeysPath { get; set; } = "keys";

        /// <summary>
        /// Marks the session cookie Secure, so browsers send it over HTTPS only. Turn off only for local HTTP testing.
        /// </summary>
        public bool SecureCookies { get; set; } = true;

        /// <summary>
        /// IP addresses of reverse proxies whose X-Forwarded-For is trusted. Without them, behind a proxy every client shares
        /// the proxy's IP, and the sign-in IP block would block everyone.
        /// </summary>
        public string[] TrustedProxies { get; set; } = System.Array.Empty<string>();
    }
}
