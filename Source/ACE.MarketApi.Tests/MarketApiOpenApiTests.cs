using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.MarketApi.Tests
{
    [TestClass]
    public class MarketApiOpenApiTests
    {
        [TestMethod]
        public async Task EveryEndpoint_HasASuccessResponseSchema()
        {
            var path = Path.Combine(Path.GetTempPath(), $"market-openapi-{Guid.NewGuid():N}.json");
            try
            {
                await Program.GenerateOpenApi(path);
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
                var paths = document.RootElement.GetProperty("paths");
                var operations = paths.EnumerateObject()
                    .SelectMany(item => item.Value.EnumerateObject().Select(operation => (Path: item.Name, Method: operation.Name, Operation: operation.Value)))
                    .Where(item => item.Method is "get" or "post" or "put" or "patch" or "delete")
                    .ToList();

                Assert.AreEqual(24, operations.Count, "Every mapped Market API route must be represented exactly once.");
                foreach (var (route, method, operation) in operations)
                {
                    var response = operation.GetProperty("responses").EnumerateObject()
                        .FirstOrDefault(candidate => candidate.Name.StartsWith("2", StringComparison.Ordinal));
                    Assert.IsFalse(response.Equals(default(JsonProperty)), $"{method.ToUpperInvariant()} {route} has no success response.");
                    Assert.IsTrue(response.Value.TryGetProperty("content", out var content), $"{method.ToUpperInvariant()} {route} has no response content.");
                    Assert.IsTrue(content.EnumerateObject().Any(media => media.Value.TryGetProperty("schema", out _)), $"{method.ToUpperInvariant()} {route} has no response schema.");
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public async Task CommittedOpenApi_MatchesTheBuiltInGenerator()
        {
            var generated = Path.Combine(Path.GetTempPath(), $"market-openapi-{Guid.NewGuid():N}.json");
            var committed = FindCommittedDocument();
            try
            {
                await Program.GenerateOpenApi(generated);
                Assert.IsTrue(File.Exists(committed), $"Expected committed OpenAPI document at {committed}.");
                var preBuildCommitted = Path.Combine(Path.GetDirectoryName(committed), "obj", "market-openapi-committed-before-build.json");
                Assert.IsTrue(File.Exists(preBuildCommitted), $"Expected the build to preserve the pre-generation document at {preBuildCommitted}.");
                AssertDocumentsMatch(await File.ReadAllBytesAsync(preBuildCommitted), await File.ReadAllBytesAsync(generated));
            }
            finally
            {
                File.Delete(generated);
            }
        }

        [TestMethod]
        public void OpenApiDriftCheck_RejectsStaleCommittedContent()
        {
            var committed = File.ReadAllBytes(FindCommittedDocument());
            var generated = Path.Combine(Path.GetTempPath(), $"market-openapi-{Guid.NewGuid():N}.json");
            try
            {
                Program.GenerateOpenApi(generated).GetAwaiter().GetResult();
                var stale = (byte[])committed.Clone();
                stale[0] ^= 1;

                Assert.ThrowsExactly<AssertFailedException>(() => AssertDocumentsMatch(stale, File.ReadAllBytes(generated)));
                AssertDocumentsMatch(committed, File.ReadAllBytes(generated));
            }
            finally
            {
                File.Delete(generated);
            }
        }

        private static void AssertDocumentsMatch(byte[] committed, byte[] generated)
        {
            CollectionAssert.AreEqual(committed, generated, "The committed document is out of date; build ACE.MarketApi to regenerate it.");
        }

        private static string FindCommittedDocument()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "Source", "ACE.MarketApi", "openapi.json");
                if (File.Exists(candidate))
                    return candidate;
            }

            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Source/ACE.MarketApi/openapi.json"));
        }
    }
}
