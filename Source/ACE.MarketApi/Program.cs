using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ACE.Database.Market;

namespace ACE.MarketApi
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length == 2 && args[0] == "--generate-openapi")
                {
                    GenerateOpenApi(args[1]).GetAwaiter().GetResult();
                    return 0;
                }

                MarketApi.Create(args).Run();
                return 0;
            }
            catch (MarketUnavailableException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        public static async Task GenerateOpenApi(string path)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(Program).Assembly.GetName().Name,
            });
            builder.Services.AddOpenApi();
            builder.Services.AddAuthorization();
            // Endpoint filters and handler binding inspect service registrations but never resolve them while documenting routes.
            builder.Services.AddSingleton<MarketDatabase>(_ => null);
            builder.Services.AddSingleton<GameData>(_ => null);
            builder.Services.AddSingleton<IconStore>(_ => null);
            builder.Services.AddSingleton<SignInLimiter>(_ => null);
            builder.Services.AddSingleton<PurchaseLimiter>(_ => null);
            builder.Services.AddSingleton<IMarketPause>(_ => null);
            builder.Services.AddSingleton<IFeePolicy>(_ => null);
            builder.Services.AddSingleton(AppraisalRules.Default);
            builder.Services.AddSingleton(TimeProvider.System);

            await using var app = builder.Build();
            MarketApi.MapEndpoints(app);
            app.MapOpenApi();
            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync();

            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.Single();
            using var client = new HttpClient();
            var json = await client.GetStringAsync(address + "/openapi/v1.json");
            var document = JsonNode.Parse(json).AsObject();
            document.Remove("servers");
            AddQueryParameters(document, "/api/listings", "q", "type", "minPrice", "maxPrice", "seller", "sort", "dir", "limit", "cursor");
            AddQueryParameters(document, "/api/listings/suggest", "q");
            AddQueryParameters(document, "/api/history", "since", "transfersBefore", "itemsBefore", "itemsLimit", "transfersLimit");
            AddInventorySnapshotSchema(document);
            AddTicketResultSchema(document);
            var errorSchema = document["components"]?["schemas"]?["ApiError"]?["properties"]?["error"]?.AsObject();
            if (errorSchema != null)
            {
                var codes = new JsonArray();
                foreach (var code in ApiError.Codes)
                    codes.Add(code);
                errorSchema["enum"] = codes;
            }
            await File.WriteAllTextAsync(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            await app.StopAsync();
        }

        private static void AddQueryParameters(JsonObject document, string route, params string[] names)
        {
            var operation = document["paths"]?[route]?["get"]?.AsObject();
            if (operation == null)
                throw new InvalidOperationException($"OpenAPI route {route} is missing.");

            var parameters = new JsonArray();
            foreach (var name in names)
                parameters.Add(new JsonObject
                {
                    ["name"] = name,
                    ["in"] = "query",
                    ["required"] = false,
                    ["schema"] = new JsonObject { ["type"] = "string" },
                });
            operation["parameters"] = parameters;
        }

        private static void AddInventorySnapshotSchema(JsonObject document)
        {
            var schemas = document["components"]?["schemas"]?.AsObject() ?? throw new InvalidOperationException("OpenAPI schemas are missing.");
            schemas["InventorySnapshotItemResponse"] = new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("itemGuid", "name", "stackSize", "refusalCode", "icon"),
                ["properties"] = new JsonObject
                {
                    ["itemGuid"] = new JsonObject { ["type"] = "integer", ["format"] = "uint32" },
                    ["name"] = new JsonObject { ["type"] = "string" },
                    ["stackSize"] = new JsonObject { ["type"] = "integer", ["format"] = "int32" },
                    ["refusalCode"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
                    ["icon"] = new JsonObject { ["$ref"] = "#/components/schemas/IconResponse" },
                },
            };
            schemas["InventorySnapshotResponse"] = new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("snapshotTime", "items"),
                ["properties"] = new JsonObject
                {
                    ["snapshotTime"] = new JsonObject { ["type"] = "string", ["format"] = "date-time" },
                    ["items"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["$ref"] = "#/components/schemas/InventorySnapshotItemResponse" },
                    },
                },
            };
        }

        private static void AddTicketResultSchema(JsonObject document)
        {
            var schemas = document["components"]?["schemas"]?.AsObject() ?? throw new InvalidOperationException("OpenAPI schemas are missing.");
            schemas["JsonElement"] = new JsonObject
            {
                ["anyOf"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = true,
                    },
                    new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject(),
                    },
                    new JsonObject { ["type"] = "string" },
                    new JsonObject { ["type"] = "number" },
                    new JsonObject { ["type"] = "boolean" },
                    new JsonObject { ["type"] = "null" },
                },
            };

            var result = schemas["TicketResponse"]?["properties"]?["result"]?.AsObject()
                ?? throw new InvalidOperationException("TicketResponse.result is missing from the OpenAPI schema.");
            result.Clear();
            result["anyOf"] = new JsonArray
            {
                new JsonObject { ["type"] = "null" },
                new JsonObject { ["$ref"] = "#/components/schemas/InventorySnapshotResponse" },
                new JsonObject { ["$ref"] = "#/components/schemas/JsonElement" },
            };
        }
    }
}
