using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;

using ACE.Database.Market;

namespace ACE.MarketApi
{
    /// <summary>
    /// The API's OpenAPI document, from its routes and contract types. The build writes it to obj/openapi.json, and a test compares that with the committed openapi.json.
    /// Generating needs no database, no DATs and no game data, and it starts no server, so it binds no socket whatever ASPNETCORE_URLS says.
    /// </summary>
    public static class MarketOpenApi
    {
        private const string DocumentName = "v1";

        public static async Task WriteAsync(string path)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(MarketOpenApi).Assembly.GetName().Name,
            });
            // a build step: a failure throws, and nothing else is worth printing
            builder.Logging.ClearProviders();
            builder.Services.AddOpenApi(DocumentName, options =>
            {
                options.AddDocumentTransformer((document, context, cancellationToken) =>
                {
                    document.Servers = null;
                    return Task.CompletedTask;
                });
                options.AddSchemaTransformer((schema, context, cancellationToken) =>
                {
                    if (context.JsonPropertyInfo?.DeclaringType == typeof(ApiError) && context.JsonPropertyInfo.Name == "error")
                        schema.Enum = ApiError.Codes.Select(code => (JsonNode)JsonValue.Create(code)).ToList();

                    // The API's JSON reads quoted numbers too, so ASP.NET documents each number as number-or-patterned-string. The API always
                    // writes plain numbers, and the document describes the plain-number requests the website sends: a subset of what the API accepts.
                    if (schema.Type is JsonSchemaType type && (type & (JsonSchemaType.Integer | JsonSchemaType.Number)) != 0)
                    {
                        schema.Type = type & ~JsonSchemaType.String;
                        schema.Pattern = null;
                    }
                    return Task.CompletedTask;
                });
            });
            MarketApi.AddJson(builder.Services);
            builder.Services.AddAuthorization();

            // Handler binding asks which parameters are services; nothing resolves them, since no request is served
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
            // registers the routes with routing, as starting the app would; the app is never started
            app.UseRouting();
            app.UseEndpoints(_ => { });

            var document = await app.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>(DocumentName).GetOpenApiDocumentAsync();
            var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_1);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            await File.WriteAllTextAsync(path, json.ReplaceLineEndings("\n") + "\n");
        }
    }
}
