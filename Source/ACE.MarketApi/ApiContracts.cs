// The contracts' nullable annotations are how the OpenAPI document tells a field that may be null from one that is always present.
// Without them ASP.NET documents every reference-type field (strings, lists, records) as nullable: checked by generating without this line.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ACE.MarketApi
{
    // Named wire contracts. These are used by OpenAPI metadata; JSON property names use ASP.NET's web defaults.

    /// <param name="Price">only on price_changed: the listing's current price</param>
    public sealed record ApiError(string Error, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Price = null)
    {
        public static readonly string[] Codes =
        {
            "account_locked", "attuned", "bad_cursor", "bad_limit", "bad_price", "bad_request", "bad_sort", "bad_type",
            "banned", "blocked_wcid", "busy", "channelling", "confirmation_busy", "confirm_timeout", "container_not_empty",
            "contains_attuned", "csrf", "declined", "gone", "in_trade", "insufficient_funds", "invalid_character", "invalid_count",
            "invalid_code", "invalid_credentials", "invalid_fee", "invalid_price", "invalid_amount", "ip_blocked", "key_reused", "listing_limit",
            "network", "not_active", "not_available", "not_found", "not_held", "not_in_pack", "not_in_vault", "ok",
            "own_listing", "paused", "pet_out", "price_changed", "rate_limited", "recent_player_fight", "server", "trading",
            "unauthorized", "vault_full", "worn",
        };
    }
    public sealed record OkResponse(bool Ok);
    public sealed record LoginResponse(uint AccountId, string AccountName);
    public sealed record CharacterResponse(uint Id, string Name);
    public sealed record MeResponse(uint AccountId, string AccountName, IReadOnlyList<CharacterResponse> Characters, long Balance, bool Frozen, bool Paused, int VaultCount, long VaultCap, int ListingCount, long ListingCap);
    /// <param name="PaletteTemplate">only on a base icon chosen from the item's clothing table</param>
    public sealed record IconLayerResponse(string Kind, uint Id, string Url, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PaletteTemplate = null);
    public sealed record IconResponse(IReadOnlyList<IconLayerResponse> Layers, string? Glow);
    public sealed record SpellResponse(string Name, bool Cantrip);
    // Both flat on purpose: nesting the listing would change the wire shape, and deriving the detail from the listing drops the listing's fields from the document's required list
    public sealed record ListingResponse(long Id, uint ItemGuid, uint Wcid, string Name, string ItemType, string? Material, int? Workmanship, int? Level, int? ArcaneLore, string Summary, int Quantity, long Price, string Seller, DateTime ListedTime, string? Wield, IconResponse Icon);
    public sealed record ListingDetailResponse(long Id, uint ItemGuid, uint Wcid, string Name, string ItemType, string? Material, int? Workmanship, int? Level, int? ArcaneLore, string Summary, int Quantity, long Price, string Seller, DateTime ListedTime, string? Wield, IconResponse Icon, IReadOnlyList<string> Lines, IReadOnlyList<SpellResponse> Spells);
    public sealed record BrowseResponse(IReadOnlyList<ListingResponse> Listings, string? NextCursor);
    public sealed record SuggestionsResponse(IReadOnlyList<string> Suggestions);
    public sealed record FacetItemResponse(string Value, string Label, int Count);
    public sealed record SortResponse(string Value, string Label, string DefaultDir);
    public sealed record FacetsResponse(IReadOnlyList<FacetItemResponse> ItemTypes, IReadOnlyList<SortResponse> Sorts);
    public sealed record ListedResponse(long Id, uint ItemGuid, long Price, string Status, DateTime ListedTime);
    public sealed record DelistedResponse(long Id, uint ItemGuid, string Status);
    public sealed record VaultItemResponse(uint ItemGuid, uint Wcid, string Name, int ItemType, int StackSize, string State, uint CharacterId, IconResponse Icon, long? ListingId, long? Price, DateTime? ExpiresTime, long? TicketId, DateTime DepositedTime);
    public sealed record VaultResponse(IReadOnlyList<VaultItemResponse> Items);
    public sealed record AppraisalResponse(IReadOnlyList<string> Lines, IReadOnlyList<SpellResponse> Spells);
    public sealed record HistoryTransferResponse(long Sequence, long TransferId, string Kind, long Amount, long BalanceAfter, DateTime Time, string Text, string? Memo);
    public sealed record HistoryItemResponse(long Id, uint ItemGuid, string Kind, string Name, long? ListingId, DateTime Time, string Text);
    public sealed record HistoryResponse(long Balance, long Head, IReadOnlyList<HistoryTransferResponse> Transfers, long? NextTransfersBefore, long? NextSince, bool More, IReadOnlyList<HistoryItemResponse> Items, long? NextItemsBefore);

    /// <param name="Result">an inventory snapshot ticket's snapshot, once it's done; null for every other ticket</param>
    public sealed record TicketResponse(long Id, string Kind, string Status, uint? CharacterId, uint? ItemGuid, long? Amount, string? ResultCode, string? ResultMessage, string? Progress, DateTime? ProgressTime, DateTime? ProgressUntil, InventorySnapshotResponse? Result, DateTime CreatedTime, DateTime? ClaimedTime, DateTime? FinishedTime);
    public sealed record InventorySnapshotItemResponse(uint ItemGuid, string Name, int StackSize, string? RefusalCode, IconResponse Icon);
    public sealed record InventorySnapshotResponse(DateTime SnapshotTime, IReadOnlyList<InventorySnapshotItemResponse> Items);
    public sealed record PluginTokenResponse(string Token, long TokenId, string Label, DateTime ExpiresTime);
    public sealed record PluginTokenInfoResponse(long Id, string Label, DateTime CreatedTime, DateTime? LastUsedTime, DateTime ExpiresTime);
    public sealed record PluginTokensResponse(IReadOnlyList<PluginTokenInfoResponse> Tokens);

    internal static class ApiContractViews
    {
        public static IconResponse Icon(ItemIcons.IconView icon) => new(
            icon.Layers.Select(layer => new IconLayerResponse(layer.Kind, layer.Id, layer.Url, layer.PaletteTemplate)).ToList(), icon.Glow);

        public static SpellResponse Spell(GameData.Spell spell) => new(spell.Name, spell.Cantrip);

        public static ListingResponse Listing(ListingCatalog.ListingView view) => new(
            view.Id, view.ItemGuid, view.Wcid, view.Name, view.ItemType, view.Material, view.Workmanship, view.Level,
            view.ArcaneLore, view.Summary, view.Quantity, view.Price, view.Seller, view.ListedTime, view.Wield, Icon(view.Icon));

        public static ListingDetailResponse Detail(ListingCatalog.ListingView view) => new(
            view.Id, view.ItemGuid, view.Wcid, view.Name, view.ItemType, view.Material, view.Workmanship, view.Level,
            view.ArcaneLore, view.Summary, view.Quantity, view.Price, view.Seller, view.ListedTime, view.Wield, Icon(view.Icon),
            view.Lines, view.Spells.Select(Spell).ToList());
    }

    internal static class ApiRouteMetadata
    {
        public static RouteHandlerBuilder Json<T>(this RouteHandlerBuilder route, int success = 200, params int[] errors)
        {
            route.Produces<T>(success);
            foreach (var status in errors)
                route.Produces<ApiError>(status);
            return route;
        }

        public static RouteHandlerBuilder File(this RouteHandlerBuilder route, int success, string contentType, params int[] errors)
        {
            route.Produces(success, typeof(byte[]), contentType);
            foreach (var status in errors)
                route.Produces<ApiError>(status);
            return route;
        }

        /// <summary>
        /// Documents the query parameters a handler reads by hand: one optional parameter per property of T, typed from the property.
        /// The handler reads each key through MarketHttp.QueryName(nameof(T.Property)) and keeps its own parsing, so a bad value is still refused
        /// with the handler's error code.
        /// </summary>
        public static RouteHandlerBuilder Query<T>(this RouteHandlerBuilder route) => route.AddOpenApiOperationTransformer(async (operation, context, cancellationToken) =>
        {
            operation.Parameters ??= new List<IOpenApiParameter>();
            foreach (var property in typeof(T).GetProperties())
            {
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = MarketHttp.QueryName(property.Name),
                    In = ParameterLocation.Query,
                    Required = false,
                    Schema = await context.GetOrCreateSchemaAsync(Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType, null, cancellationToken),
                });
            }
        });
    }
}
