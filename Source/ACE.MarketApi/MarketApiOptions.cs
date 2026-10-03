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
        /// Replaces Config.js's MySQL user for the auth and shard databases. A container mounts a Config.js without a login and gets this from docker.env.
        /// </summary>
        public string DatabaseUsername { get; set; }

        /// <summary>
        /// Replaces Config.js's MySQL password for the auth and shard databases (see DatabaseUsername)
        /// </summary>
        public string DatabasePassword { get; set; }

        /// <summary>
        /// Where the cookie signing keys are kept. Put it on a volume, so a restart or redeploy doesn't sign everyone out.
        /// </summary>
        public string KeysPath { get; set; } = "keys";

        /// <summary>
        /// Where icon PNGs are cached once made from the portal DAT. Safe to delete: they are made again on the next request.
        /// </summary>
        public string IconCachePath { get; set; } = "icon-cache";

        /// <summary>
        /// Marks the session cookie Secure, so browsers send it over HTTPS only. Turn off only for local HTTP testing.
        /// </summary>
        public bool SecureCookies { get; set; } = true;

        /// <summary>
        /// IP addresses of reverse proxies whose X-Forwarded-For is trusted. Without them, behind a proxy every client shares
        /// the proxy's IP, and the sign-in IP block would block everyone.
        /// </summary>
        public string[] TrustedProxies { get; set; } = System.Array.Empty<string>();

        /// <summary>
        /// The secret every request but the health check must carry in X-Market-Service-Key (ServiceGate). Required, at least
        /// ServiceGate.MinimumKeyLength characters: the API refuses to start without it. Never in a file in git: locally it comes from
        /// MARKET_SERVICE_KEY in the untracked docker.env, as Market__ServiceKey.
        /// </summary>
        public string ServiceKey { get; set; }
    }
}
