using System;
using System.Collections.Generic;
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
            app.MapPost("/vault/withdraw", VaultWithdraw).Json<TicketResponse>(202, 400, 401, 404, 409).RequireAuthorization();
            app.MapPost("/vault/deposit", VaultDeposit).Json<TicketResponse>(202, 400, 401, 404, 409).RequireAuthorization();
            app.MapPost("/inventory/snapshot", InventorySnapshot).Json<TicketResponse>(202, 400, 401, 404, 409).RequireAuthorization();
            app.MapPost("/mmd/withdraw", MmdWithdraw).Json<TicketResponse>(202, 400, 401, 404, 409, 503).RequireAuthorization();
            app.MapGet("/tickets", List).Json<TicketResponse[]>(200, 401).RequireAuthorization();
            app.MapGet("/tickets/{id:long}", Get).Json<TicketResponse>(200, 401, 404).RequireAuthorization();
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

        private static TicketResponse View(Ticket ticket, GameData gameData)
        {
            var payload = TicketPayload.FromJson(ticket.Payload);

            return new TicketResponse(
                ticket.Id,
                ticket.Kind,
                ticket.Status,
                ticket.CharacterId,
                payload?.ItemGuid,
                payload?.Amount,
                ticket.ResultCode,
                ticket.ResultMessage,
                ticket.Progress,
                MarketHttp.Utc(ticket.ProgressTime),
                MarketHttp.Utc(ticket.ProgressUntil),
                ParseResult(ticket, gameData),
                MarketHttp.Utc(ticket.CreatedTime),
                MarketHttp.Utc(ticket.ClaimedTime),
                MarketHttp.Utc(ticket.FinishedTime));
        }

        private static JsonElement? ParseResult(Ticket ticket, GameData gameData)
        {
            if (ticket.Result == null)
                return null;

            try
            {
                using var document = JsonDocument.Parse(ticket.Result);
                var root = document.RootElement;

                if (ticket.Kind == TicketKind.InventorySnapshot)
                {
                    if (root.ValueKind != JsonValueKind.Object
                        || !root.TryGetProperty("snapshotTime", out var snapshotTime)
                        || snapshotTime.ValueKind != JsonValueKind.String
                        || !root.TryGetProperty("items", out var items)
                        || items.ValueKind != JsonValueKind.Array)
                        return null;

                    var projected = new List<object>(items.GetArrayLength());

                    foreach (var item in items.EnumerateArray())
                    {
                        if (!TryProjectSnapshotItem(item, gameData, out var projectedItem))
                            return null;

                        projected.Add(projectedItem);
                    }

                    return JsonSerializer.SerializeToElement(new
                    {
                        snapshotTime = snapshotTime.GetString(),
                        items = projected,
                    }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.Never });
                }

                return root.Clone();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static bool TryProjectSnapshotItem(JsonElement item, GameData gameData, out object projected)
        {
            projected = null;

            if (item.ValueKind != JsonValueKind.Object
                || !TryReadUInt(item, "itemGuid", out var guid)
                || !TryReadString(item, "name", out var name)
                || !TryReadInt(item, "stackSize", out var stackSize)
                || !TryReadInt(item, "itemType", out var itemType)
                || !TryReadUInt(item, "icon", out var icon)
                || !TryReadNullableString(item, "refusalCode", out var refusalCode)
                || !TryReadNullableUInt(item, "iconUnderlay", out var iconUnderlay)
                || !TryReadNullableUInt(item, "iconOverlay", out var iconOverlay)
                || !TryReadNullableUInt(item, "iconOverlaySecondary", out var iconOverlaySecondary)
                || !TryReadNullableInt(item, "uiEffects", out var uiEffects)
                || !TryReadNullableInt(item, "paletteTemplate", out var paletteTemplate)
                || !TryReadNullableUInt(item, "clothingBase", out var clothingBase))
                return false;

            var row = new VaultItem
            {
                ItemGuid = guid,
                ItemType = itemType,
                Icon = icon,
                IconUnderlay = iconUnderlay,
                IconOverlay = iconOverlay,
                IconOverlaySecondary = iconOverlaySecondary,
                UiEffects = uiEffects,
                PaletteTemplate = paletteTemplate,
                ClothingBase = clothingBase,
            };

            projected = new
            {
                itemGuid = guid,
                name,
                stackSize,
                refusalCode,
                icon = ItemIcons.For(row, gameData),
            };

            return true;
        }

        private static bool TryReadUInt(JsonElement item, string property, out uint value)
        {
            value = default;
            return item.TryGetProperty(property, out var element)
                && element.ValueKind == JsonValueKind.Number
                && element.TryGetUInt32(out value);
        }

        private static bool TryReadInt(JsonElement item, string property, out int value)
        {
            value = default;
            return item.TryGetProperty(property, out var element)
                && element.ValueKind == JsonValueKind.Number
                && element.TryGetInt32(out value);
        }

        private static bool TryReadString(JsonElement item, string property, out string value)
        {
            value = null;
            if (!item.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
                return false;

            value = element.GetString();
            return true;
        }

        private static bool TryReadNullableString(JsonElement item, string property, out string value)
        {
            value = null;
            if (!item.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null)
                return true;
            if (element.ValueKind != JsonValueKind.String)
                return false;

            value = element.GetString();
            return true;
        }

        private static bool TryReadNullableUInt(JsonElement item, string property, out uint? value)
        {
            value = null;
            if (!item.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null)
                return true;
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetUInt32(out var parsed))
                return false;

            value = parsed;
            return true;
        }

        private static bool TryReadNullableInt(JsonElement item, string property, out int? value)
        {
            value = null;
            if (!item.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null)
                return true;
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var parsed))
                return false;

            value = parsed;
            return true;
        }
    }
}
