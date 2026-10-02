using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

using ACE.Database.Market;
using ACE.Database.Models.Auth;
using ACE.Database.Models.Shard.Market;

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
            app.MapGet("/vault/{itemGuid}", VaultDetail).RequireAuthorization();
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
        private static async Task<IResult> Vault(HttpContext context, MarketDatabase database, TimeProvider time, GameData gameData)
        {
            var accountId = MarketHttp.AccountId(context);

            // an item whose listing has outlived its lifetime shows as held
            MarketUpkeep.Expire(database, time.GetUtcNow().UtcDateTime);

            using var shard = database.CreateShard();

            var items = await shard.MarketVaultItems.AsNoTracking()
                .Where(v => v.AccountId == accountId)
                .OrderBy(v => v.DepositedTime).ThenBy(v => v.ItemGuid)
                .ToListAsync();

            var itemGuids = items.Select(v => v.ItemGuid).ToHashSet();
            var listings = itemGuids.Count == 0 ? new Dictionary<uint, Database.Models.Shard.Market.Listing>() :
                await shard.MarketListings.AsNoTracking()
                    .Where(l => itemGuids.Contains(l.ItemGuid) && l.SellerAccountId == accountId && l.Status == Database.Models.Shard.Market.ListingStatus.Active)
                    .ToDictionaryAsync(l => l.ItemGuid);
            var ticketIds = new Dictionary<uint, long>();

            if (items.Any(v => v.State == VaultItemState.Withdrawing))
            {
                var tickets = await shard.MarketTickets.AsNoTracking()
                    .Where(t => t.AccountId == accountId && t.Kind == TicketKind.VaultWithdraw &&
                        (t.Status == TicketStatus.Waiting || t.Status == TicketStatus.Claimed))
                    .OrderBy(t => t.Id)
                    .ToListAsync();

                foreach (var ticket in tickets)
                {
                    var itemGuid = TicketPayload.FromJson(ticket.Payload)?.ItemGuid;

                    if (itemGuid.HasValue && itemGuids.Contains(itemGuid.Value) && !ticketIds.ContainsKey(itemGuid.Value))
                        ticketIds.Add(itemGuid.Value, ticket.Id);
                }
            }

            var listingLifetime = ListingStore.Lifetime(shard);

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
                    icon = ItemIcons.For(v, gameData),
                    listingId = listings.TryGetValue(v.ItemGuid, out var listing) ? listing.Id : (long?)null,
                    price = listing?.Price,
                    expiresTime = listing == null ? (DateTime?)null : MarketHttp.Utc(listing.CreatedTime + listingLifetime),
                    ticketId = ticketIds.TryGetValue(v.ItemGuid, out var ticketId) ? ticketId : (long?)null,
                    // stored as UTC; EF reads datetime(6) as Unspecified
                    depositedTime = DateTime.SpecifyKind(v.DepositedTime, DateTimeKind.Utc),
                }),
            });
        }

        /// <summary>
        /// The signed-in account's appraisal for its own Vault item.
        /// </summary>
        private static async Task<IResult> VaultDetail(uint itemGuid, HttpContext context, MarketDatabase database, GameData gameData, AppraisalRules rules)
        {
            var accountId = MarketHttp.AccountId(context);

            using var shard = database.CreateShard();

            var item = await shard.MarketVaultItems.AsNoTracking()
                .FirstOrDefaultAsync(v => v.ItemGuid == itemGuid && v.AccountId == accountId);

            if (item == null)
                return MarketHttp.Error(StatusCodes.Status404NotFound, "not_found");

            var appraisal = AppraisalItem.Load(shard, gameData, new[] { itemGuid })[itemGuid];

            return Results.Json(new
            {
                lines = rules.Format(appraisal),
                spells = AppraisalRules.Spells(appraisal),
            });
        }
    }
}
