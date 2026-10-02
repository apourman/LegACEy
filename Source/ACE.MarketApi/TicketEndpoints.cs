using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using ACE.Database.Market;
using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;

namespace ACE.MarketApi
{
    /// <summary>
    /// The game bridge: asking the game server to move something out of the Vault. The API only writes a ticket; the game server claims it,
    /// does the work on the world thread through the same Vault code as /vault (an item withdrawal takes the channel, so the character must be online),
    /// and writes the result, which GET /api/tickets/{id} shows.
    /// </summary>
    public static class TicketEndpoints
    {
        /// <param name="IdempotencyKey">the caller's key for this request (up to 64 characters): a repeat returns the same ticket</param>
        public sealed record VaultWithdrawRequest(uint? CharacterId, uint? ItemGuid, string IdempotencyKey);

        /// <param name="Amount">whole MMD, at least 1; a decimal so that 1.5 is refused as an amount rather than as unreadable JSON</param>
        public sealed record MmdWithdrawRequest(uint? CharacterId, decimal? Amount, string IdempotencyKey);

        public sealed record InventorySnapshotRequest(uint? CharacterId, string IdempotencyKey);

        public sealed record VaultDepositRequest(uint? CharacterId, uint? ItemGuid, string IdempotencyKey);

        public static void Map(IEndpointRouteBuilder app)
        {
            app.MapPost("/vault/withdraw", VaultWithdraw).RequireAuthorization();
            app.MapPost("/vault/deposit", VaultDeposit).RequireAuthorization();
            app.MapPost("/inventory/snapshot", InventorySnapshot).RequireAuthorization();
            app.MapPost("/mmd/withdraw", MmdWithdraw).RequireAuthorization();
            app.MapGet("/tickets", List).RequireAuthorization();
            app.MapGet("/tickets/{id:long}", Get).RequireAuthorization();
        }

        private static IResult VaultWithdraw(VaultWithdrawRequest request, HttpContext context, MarketDatabase database, TimeProvider time, GameData gameData)
        {
            var replay = Replay(context, database, TicketKind.VaultWithdraw, request?.IdempotencyKey, gameData);
            if (replay != null)
                return replay;

            if (request.ItemGuid == null)
                return MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_request");

            return Create(context, database, time, request.CharacterId, TicketKind.VaultWithdraw, new TicketPayload(ItemGuid: request.ItemGuid), request.IdempotencyKey, gameData);
        }

        private static IResult MmdWithdraw(MmdWithdrawRequest request, HttpContext context, MarketDatabase database, TimeProvider time, IMarketPause pause, GameData gameData)
        {
            var replay = Replay(context, database, TicketKind.MmdWithdraw, request?.IdempotencyKey, gameData);
            if (replay != null)
                return replay;

            if (pause.IsPaused)
                return MarketHttp.Error(StatusCodes.Status503ServiceUnavailable, "paused");

            if (!MarketHttp.TryWholeMmd(request.Amount, out var amount))
                return MarketHttp.Error(StatusCodes.Status400BadRequest, "invalid_amount");

            return Create(context, database, time, request.CharacterId, TicketKind.MmdWithdraw, new TicketPayload(Amount: amount), request.IdempotencyKey, gameData);
        }

        private static IResult Get(long id, HttpContext context, MarketDatabase database, GameData gameData)
        {
            using var shard = database.CreateShard();

            var ticket = TicketStore.Find(shard, MarketHttp.AccountId(context), id);

            return ticket == null ? MarketHttp.Error(StatusCodes.Status404NotFound, "not_found") : Results.Json(View(ticket, gameData));
        }

        private static IResult List(HttpContext context, MarketDatabase database, TimeProvider time, GameData gameData)
        {
            using var shard = database.CreateShard();
            var tickets = TicketStore.VisibleToPlayer(shard, MarketHttp.AccountId(context), time.GetUtcNow().UtcDateTime);
            return Results.Json(tickets.Select(ticket => View(ticket, gameData)));
        }

        private static IResult InventorySnapshot(InventorySnapshotRequest request, HttpContext context, MarketDatabase database, TimeProvider time, GameData gameData)
        {
            var replay = Replay(context, database, TicketKind.InventorySnapshot, request?.IdempotencyKey, gameData);
            if (replay != null)
                return replay;

            return Create(context, database, time, request?.CharacterId, TicketKind.InventorySnapshot, new TicketPayload(), request.IdempotencyKey, gameData);
        }

        private static IResult VaultDeposit(VaultDepositRequest request, HttpContext context, MarketDatabase database, TimeProvider time, GameData gameData)
        {
            var replay = Replay(context, database, TicketKind.VaultDeposit, request?.IdempotencyKey, gameData);
            if (replay != null)
                return replay;

            if (request?.ItemGuid == null)
                return MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_request");

            return Create(context, database, time, request.CharacterId, TicketKind.VaultDeposit, new TicketPayload(ItemGuid: request.ItemGuid), request.IdempotencyKey, gameData);
        }

        /// <summary>
        /// The answer a ticket-creating request gets before any other check: 400 for a missing or malformed key, and for a key the account
        /// already used, its original ticket (or 409 key_reused for another kind). Null when the key is new and the request goes on.
        /// </summary>
        private static IResult Replay(HttpContext context, MarketDatabase database, string kind, string idempotencyKey, GameData gameData)
        {
            if (!MarketHttp.IsIdempotencyKey(idempotencyKey))
                return MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_request");

            using var shard = database.CreateShard();
            var existing = TicketStore.FindByKey(shard, MarketHttp.AccountId(context), idempotencyKey, kind);

            if (existing == null)
                return null;

            if (existing.Outcome == TicketCreateOutcome.KeyReused)
                return MarketHttp.Error(StatusCodes.Status409Conflict, "key_reused");

            return Results.Accepted($"{MarketApi.PathBase}/tickets/{existing.Ticket.Id}", View(existing.Ticket, gameData));
        }

        /// <summary>
        /// Writes the ticket, or finds the one the key already made. Either way the answer is the ticket as it is now.
        /// The balance and Vault row are left to the game server, which checks them when it does the work.
        /// </summary>
        private static IResult Create(HttpContext context, MarketDatabase database, TimeProvider time, uint? characterId, string kind, TicketPayload payload, string idempotencyKey, GameData gameData)
        {
            var accountId = MarketHttp.AccountId(context);

            using var shard = database.CreateShard();

            if (characterId == null || !IsOwnCharacter(shard, accountId, characterId.Value))
                return MarketHttp.Error(StatusCodes.Status400BadRequest, "invalid_character");

            var result = TicketStore.Create(shard, accountId, characterId.Value, kind, payload, idempotencyKey, time.GetUtcNow().UtcDateTime);

            if (result.Outcome == TicketCreateOutcome.KeyReused)
                return MarketHttp.Error(StatusCodes.Status409Conflict, "key_reused");

            return Results.Accepted($"{MarketApi.PathBase}/tickets/{result.Ticket.Id}", View(result.Ticket, gameData));
        }

        private static bool IsOwnCharacter(ShardDbContext shard, uint accountId, uint characterId) =>
            shard.Character.Any(c => c.Id == characterId && c.AccountId == accountId && !c.IsDeleted);

        private static object View(Ticket ticket, GameData gameData)
        {
            var payload = TicketPayload.FromJson(ticket.Payload);

            return new
            {
                id = ticket.Id,
                kind = ticket.Kind,
                status = ticket.Status,
                characterId = ticket.CharacterId,
                itemGuid = payload?.ItemGuid,
                amount = payload?.Amount,
                resultCode = ticket.ResultCode,
                resultMessage = ticket.ResultMessage,
                progress = ticket.Progress,
                progressTime = MarketHttp.Utc(ticket.ProgressTime),
                progressUntil = MarketHttp.Utc(ticket.ProgressUntil),
                result = ParseResult(ticket, gameData),
                createdTime = MarketHttp.Utc(ticket.CreatedTime),
                claimedTime = MarketHttp.Utc(ticket.ClaimedTime),
                finishedTime = MarketHttp.Utc(ticket.FinishedTime),
            };
        }

        private static JsonElement? ParseResult(Ticket ticket, GameData gameData)
        {
            if (ticket.Result == null)
                return null;

            try
            {
                using var document = JsonDocument.Parse(ticket.Result);

                if (ticket.Kind == TicketKind.InventorySnapshot && document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("items", out var items))
                {
                    var projected = items.EnumerateArray().Select(item =>
                    {
                        var guid = item.GetProperty("itemGuid").GetUInt32();
                        var row = new VaultItem
                        {
                            ItemGuid = guid,
                            ItemType = item.GetProperty("itemType").GetInt32(),
                            Icon = ReadUInt(item, "icon"),
                            IconUnderlay = ReadNullableUInt(item, "iconUnderlay"),
                            IconOverlay = ReadNullableUInt(item, "iconOverlay"),
                            IconOverlaySecondary = ReadNullableUInt(item, "iconOverlaySecondary"),
                            UiEffects = ReadNullableInt(item, "uiEffects"),
                            PaletteTemplate = ReadNullableInt(item, "paletteTemplate"),
                            ClothingBase = ReadNullableUInt(item, "clothingBase"),
                        };

                        return new
                        {
                            itemGuid = guid,
                            name = item.GetProperty("name").GetString(),
                            stackSize = item.GetProperty("stackSize").GetInt32(),
                            refusalCode = item.TryGetProperty("refusalCode", out var refusal) && refusal.ValueKind != JsonValueKind.Null ? refusal.GetString() : null,
                            icon = ItemIcons.For(row, gameData),
                        };
                    }).ToArray();

                    return JsonSerializer.SerializeToElement(new
                    {
                        snapshotTime = document.RootElement.GetProperty("snapshotTime").GetString(),
                        items = projected,
                    }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.Never });
                }

                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static uint ReadUInt(JsonElement item, string property) => item.GetProperty(property).GetUInt32();

        private static uint? ReadNullableUInt(JsonElement item, string property) => item.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetUInt32() : null;

        private static int? ReadNullableInt(JsonElement item, string property) => item.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetInt32() : null;
    }
}
