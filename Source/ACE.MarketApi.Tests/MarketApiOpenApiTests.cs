using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using ACE.MarketApi.Tests.Support;

namespace ACE.MarketApi.Tests
{
    /// <summary>
    /// The OpenAPI document: it lists every route the API maps with what each answers, its schemas come from the contract records,
    /// and the committed openapi.json is what the build generates.
    /// </summary>
    [TestClass]
    public class MarketApiOpenApiTests
    {
        private const string RefreshCommand = "dotnet build Source/ACE.MarketApi -p:Platform=x64 -p:UpdateOpenApi=true, then npm run generate:api in market-web";

        private sealed record MappedRoute(string Method, string Path, bool SignedIn);

        /// <summary>
        /// The document's bytes, as the build's generator writes them
        /// </summary>
        private static async Task<byte[]> GenerateBytesAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"market-openapi-{Guid.NewGuid():N}.json");
            try
            {
                await MarketOpenApi.WriteAsync(path);
                return await File.ReadAllBytesAsync(path);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static async Task<JsonDocument> GenerateAsync() => JsonDocument.Parse(await GenerateBytesAsync());

        /// <summary>
        /// Every route the running API maps, as the document names it: lower-case method, and the path without route constraints
        /// </summary>
        private static async Task<List<MappedRoute>> MappedRoutesAsync()
        {
            await using var host = await MarketApiHost.StartAsync();

            return ((IEndpointRouteBuilder)host.App).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .SelectMany(endpoint => endpoint.Metadata.GetRequiredMetadata<IHttpMethodMetadata>().HttpMethods.Select(method => new MappedRoute(
                    method.ToLowerInvariant(),
                    Regex.Replace(endpoint.RoutePattern.RawText, @"\{(\w+):[^}]+\}", "{$1}"),
                    endpoint.Metadata.GetMetadata<IAuthorizeData>() != null)))
                .ToList();
        }

        private static JsonElement Operation(JsonDocument document, MappedRoute route) =>
            document.RootElement.GetProperty("paths").GetProperty(route.Path).GetProperty(route.Method);

        private static void AssertDeclaresApiError(JsonDocument document, MappedRoute route, string status)
        {
            var name = $"{route.Method.ToUpperInvariant()} {route.Path}";

            Assert.IsTrue(Operation(document, route).GetProperty("responses").TryGetProperty(status, out var response), $"{name} does not declare {status}.");
            Assert.AreEqual("#/components/schemas/ApiError", response.GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString(), name);
        }

        private static string ProjectFile(string relativePath)
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                var project = Path.Combine(directory.FullName, "Source", "ACE.MarketApi");
                if (File.Exists(Path.Combine(project, "ACE.MarketApi.csproj")))
                    return Path.Combine(project, relativePath);
            }

            throw new DirectoryNotFoundException("Source/ACE.MarketApi was not found above " + AppContext.BaseDirectory);
        }

        [TestMethod]
        public async Task EveryMappedRoute_IsDocumented_WithASuccessResponseSchema()
        {
            var routes = await MappedRoutesAsync();
            using var document = await GenerateAsync();

            var documented = document.RootElement.GetProperty("paths").EnumerateObject()
                .SelectMany(path => path.Value.EnumerateObject().Select(operation => $"{operation.Name} {path.Name}"))
                .ToList();

            CollectionAssert.AreEquivalent(routes.Select(route => $"{route.Method} {route.Path}").ToList(), documented, "the document lists exactly the routes the API maps");

            foreach (var route in routes)
            {
                var success = Operation(document, route).GetProperty("responses").EnumerateObject().FirstOrDefault(response => response.Name.StartsWith('2'));
                Assert.IsNotNull(success.Name, $"{route.Method.ToUpperInvariant()} {route.Path} has no success response.");
                Assert.IsTrue(success.Value.TryGetProperty("content", out var content) && content.EnumerateObject().Any(media => media.Value.TryGetProperty("schema", out _)),
                    $"{route.Method.ToUpperInvariant()} {route.Path} has no success response schema.");
            }
        }

        [TestMethod]
        public async Task SignedInRoutes_Declare401_And403IsDeclaredOnlyWhereARouteRefuses()
        {
            var routes = await MappedRoutesAsync();
            using var document = await GenerateAsync();

            Assert.IsTrue(routes.Any(route => route.SignedIn && route.Path == "/api/listings/{id}/purchase"), "purchase requires sign-in");
            Assert.IsFalse(routes.Any(route => route.SignedIn && route.Path == "/api/facets"), "the catalog does not");

            // the authorization middleware answers 401 unauthorized
            foreach (var route in routes.Where(route => route.SignedIn))
                AssertDeclaresApiError(document, route, "401");

            // routes that refuse with 403 themselves (banned, own_listing) declare it
            foreach (var path in new[] { "/api/auth/session", "/api/listings", "/api/listings/{id}/delist", "/api/listings/{id}/purchase", "/api/auth/plugin-token" })
                AssertDeclaresApiError(document, new MappedRoute("post", path, false), "403");

            // the API has no CSRF filter any more (the BFF checks cross-site requests), so a route that changes something but never refuses
            // with 403 doesn't claim to
            foreach (var path in new[] { "/api/mmd/withdraw", "/api/vault/withdraw", "/api/vault/deposit", "/api/inventory/snapshot", "/api/tokens/{id}/revoke" })
                Assert.IsFalse(Operation(document, new MappedRoute("post", path, true)).GetProperty("responses").TryGetProperty("403", out _), $"POST {path} declares 403");
        }

        [TestMethod]
        public async Task Integers_AreDocumentedAsIntegers()
        {
            using var document = await GenerateAsync();

            var text = document.RootElement.GetRawText();
            Assert.IsFalse(text.Contains("\"pattern\""), "no number is documented as a patterned string");

            var listing = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("ListingResponse").GetProperty("properties");
            Assert.AreEqual("integer", listing.GetProperty("price").GetProperty("type").GetString());
            Assert.AreEqual("integer", listing.GetProperty("itemGuid").GetProperty("type").GetString());

            var limit = Operation(document, new MappedRoute("get", "/api/listings", false)).GetProperty("parameters").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "limit");
            Assert.AreEqual("integer", limit.GetProperty("schema").GetProperty("type").GetString(), "query parameters have their real types");
        }

        [TestMethod]
        public async Task RunningApi_StillReadsQuotedNumbers()
        {
            // the document narrows numbers to plain ones; the API itself stays lenient, as before, for game plugins that quote them
            await using var host = await MarketApiHost.StartAsync();
            var json = host.App.Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;

            Assert.AreEqual(JsonNumberHandling.AllowReadingFromString, json.NumberHandling);
        }

        [TestMethod]
        public async Task TicketResult_IsTheInventorySnapshotRecord()
        {
            using var document = await GenerateAsync();
            var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

            var result = schemas.GetProperty("TicketResponse").GetProperty("properties").GetProperty("result");
            var alternatives = result.GetProperty("oneOf").EnumerateArray().Select(alternative => JsonSerializer.Serialize(alternative)).ToList();
            CollectionAssert.AreEquivalent(new[] { "{\"type\":\"null\"}", "{\"$ref\":\"#/components/schemas/InventorySnapshotResponse\"}" }, alternatives);

            // each schema has exactly its record's properties, with their types and nullability
            var nullability = new NullabilityInfoContext();
            foreach (var type in new[] { typeof(TicketResponse), typeof(InventorySnapshotResponse), typeof(InventorySnapshotItemResponse), typeof(IconResponse), typeof(IconLayerResponse) })
            {
                var parameters = type.GetConstructors().Single().GetParameters();
                var properties = schemas.GetProperty(type.Name).GetProperty("properties");
                CollectionAssert.AreEqual(parameters.Select(parameter => JsonNamingPolicy.CamelCase.ConvertName(parameter.Name)).ToList(),
                    properties.EnumerateObject().Select(property => property.Name).ToList(), type.Name);

                foreach (var parameter in parameters)
                {
                    var name = $"{type.Name}.{parameter.Name}";
                    var (types, reference) = Describe(properties.GetProperty(JsonNamingPolicy.CamelCase.ConvertName(parameter.Name)));
                    var clr = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;

                    Assert.AreEqual(nullability.Create(parameter).ReadState == NullabilityState.Nullable, types.Contains("null"), $"{name} nullability");
                    if (JsonType(clr) is string expected)
                        Assert.IsTrue(types.Contains(expected), $"{name} is documented as {string.Join("|", types)}, not {expected}");
                    else
                        Assert.AreEqual("#/components/schemas/" + clr.Name, reference, name);
                }
            }
        }

        /// <summary>
        /// The JSON type a property of this CLR type is written as; null for a record, which the document refers to by name
        /// </summary>
        private static string JsonType(Type type)
        {
            if (type == typeof(string) || type == typeof(DateTime))
                return "string";
            if (type == typeof(bool))
                return "boolean";
            if (type == typeof(int) || type == typeof(uint) || type == typeof(long))
                return "integer";
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
                return "array";
            return null;
        }

        /// <summary>
        /// A property schema's types (including "null") and the schema it refers to, if any
        /// </summary>
        private static (List<string> Types, string Reference) Describe(JsonElement property)
        {
            var types = new List<string>();
            string reference = null;

            foreach (var schema in property.TryGetProperty("oneOf", out var alternatives) ? alternatives.EnumerateArray().ToList() : new List<JsonElement> { property })
            {
                if (schema.TryGetProperty("$ref", out var target))
                    reference = target.GetString();
                if (schema.TryGetProperty("type", out var type))
                    types.AddRange(type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(t => t.GetString()) : new[] { type.GetString() });
            }

            return (types, reference);
        }

        [TestMethod]
        public async Task Generation_StartsNoServer_WhateverTheEnvironmentSays()
        {
            // a port that's taken: if generation listened on it, it would fail
            using var taken = new TcpListener(IPAddress.Loopback, 0);
            taken.Start();
            var port = ((IPEndPoint)taken.LocalEndpoint).Port;

            var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
            var ports = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS");
            try
            {
                Environment.SetEnvironmentVariable("ASPNETCORE_URLS", $"http://127.0.0.1:{port}");
                Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", port.ToString());

                using var document = await GenerateAsync();

                Assert.IsTrue(document.RootElement.GetProperty("paths").EnumerateObject().Any());
                Assert.IsFalse(document.RootElement.TryGetProperty("servers", out _), "the document names no server");
            }
            finally
            {
                Environment.SetEnvironmentVariable("ASPNETCORE_URLS", urls);
                Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", ports);
            }
        }

        [TestMethod]
        public async Task CommittedOpenApi_IsWhatTheBuildGenerates()
        {
            var committed = ProjectFile("openapi.json");
            var built = ProjectFile(Path.Combine("obj", "openapi.json"));

            Assert.IsTrue(File.Exists(built), $"The build writes {built}.");
            Assert.IsTrue(File.Exists(committed), $"{committed} is missing: {RefreshCommand}");

            var builtBytes = await File.ReadAllBytesAsync(built);

            // the build's copy is current: the generator, run here, writes the same bytes
            CollectionAssert.AreEqual(await GenerateBytesAsync(), builtBytes, "obj/openapi.json is stale; build ACE.MarketApi.");

            CollectionAssert.AreEqual(await File.ReadAllBytesAsync(committed), builtBytes, $"The committed openapi.json is out of date: {RefreshCommand}");
        }
    }
}
