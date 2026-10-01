using System;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

using ACE.Database.Market;
using ACE.Database.Models.Auth;

namespace ACE.MarketApi
{
    /// <summary>
    /// The signed-in account's own data. Nothing here changes it, beyond expiring overdue listings.
    /// </summary>
    public static class AccountEndpoints
    {
        public static void Map(IEndpointRouteBuilder app)
        {
            app.MapGet("/me", Me).RequireAuthorization();
            app.MapGet("/vault", Vault).RequireAuthorization();
        }

        /// <summary>
        /// The account, its characters, its MMD balance and whether it's frozen (banned)
        /// </summary>
        private static async Task<IResult> Me(HttpContext context, MarketDatabase database, TimeProvider time, IMarketPause pause)
        {
            var accountId = MarketHttp.AccountId(context);

            var account = await database.FindAccountAsync(accountId);

            if (account == null)
                return MarketHttp.Error(StatusCodes.Status401Unauthorized, "unauthorized");

            MarketUpkeep.Expire(database, time.GetUtcNow().UtcDateTime);

            using var shard = database.CreateShard();

            var characters = await shard.Character.AsNoTracking()
                .Where(c => c.AccountId == accountId && !c.IsDeleted)
                .OrderBy(c => c.Name)
                .Select(c => new { id = c.Id, name = c.Name })
                .ToListAsync();

            return Results.Json(new
            {
                accountId = account.AccountId,
                accountName = account.AccountName,
                characters,
                balance = Ledger.GetBalance(shard, accountId),
                frozen = account.IsBanned(time.GetUtcNow().UtcDateTime),
                paused = pause.IsPaused,
                vaultCount = await shard.MarketVaultItems.CountAsync(v => v.AccountId == accountId),
                vaultCap = MarketSettings.Get(shard, MarketSettings.VaultSize),
                listingCount = await shard.MarketListings.CountAsync(l => l.SellerAccountId == accountId && l.Status == Database.Models.Shard.Market.ListingStatus.Active),
                listingCap = MarketSettings.Get(shard, MarketSettings.ActiveListings),
            });
        }

        /// <summary>
        /// The account's Vault items and their states (held, listed, withdrawing)
        /// </summary>
        private static async Task<IResult> Vault(HttpContext context, MarketDatabase database, TimeProvider time)
        {
            var accountId = MarketHttp.AccountId(context);

            // an item whose listing has outlived its lifetime shows as held
            MarketUpkeep.Expire(database, time.GetUtcNow().UtcDateTime);

            using var shard = database.CreateShard();

            var items = await shard.MarketVaultItems.AsNoTracking()
                .Where(v => v.AccountId == accountId)
                .OrderBy(v => v.DepositedTime).ThenBy(v => v.ItemGuid)
                .ToListAsync();

            return Results.Json(new
            {
                items = items.Select(v => new
                {
                    itemGuid = v.ItemGuid,
                    wcid = v.Wcid,
                    name = v.Name,
                    itemType = v.ItemType,
                    stackSize = v.StackSize,
                    state = v.State,
                    characterId = v.CharacterId,
                    // stored as UTC; EF reads datetime(6) as Unspecified
                    depositedTime = DateTime.SpecifyKind(v.DepositedTime, DateTimeKind.Utc),
                }),
            });
        }
    }
}
