using System;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Realms;

namespace ACE.Server.Tests.Realms
{
    /// <summary>
    /// Content/realms/*.json: a good file loads, and a bad file is skipped with a diagnostic while the other files still load
    /// </summary>
    [TestClass]
    public class RealmLoaderTests
    {
        private string directory;

        [TestInitialize]
        public void CreateDirectory()
        {
            directory = Path.Combine(Path.GetTempPath(), "legacey-realms-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        [TestCleanup]
        public void DeleteDirectory()
        {
            Directory.Delete(directory, true);
        }

        [TestMethod]
        public void GoodFiles_Load_WithTheirRules_AndEphemeralTemplateKeepsItsExtraFields()
        {
            Write("pvp-test.json", Realm());
            Write("dungeon.json", "{ \"version\": 1, \"id\": 2, \"name\": \"dungeon\", \"kind\": \"ephemeral-template\", \"rules\": {}, " +
                "\"landblocks\": [\"0x0150\"], \"ttlMinutes\": 60, \"entry\": " + EntryJson + ", \"exit\": " + ExitJson + " }");

            var result = RealmLoader.LoadDirectory(directory);

            CollectionAssert.AreEqual(Array.Empty<string>(), result.Diagnostics.ToArray());
            var pvp = result.Realms.Single(realm => realm.Id == 1);
            Assert.AreEqual("test-pvp", pvp.Name);
            Assert.AreEqual(RealmKind.World, pvp.Kind);
            Assert.IsTrue(pvp.Rules["pvp"]);

            var dungeon = result.Realms.Single(realm => realm.Id == 2);
            Assert.AreEqual(RealmKind.EphemeralTemplate, dungeon.Kind);
            CollectionAssert.AreEqual(new ushort[] { 0x0150 }, dungeon.Landblocks.ToArray());
            Assert.AreEqual(60, dungeon.TtlMinutes);
            Assert.AreEqual(0x01500001u, dungeon.Entry.Cell, "the cell is read as hex");
            Assert.AreEqual(0xA9B40019u, dungeon.Exit.Cell, "the exit is a base-world position");
            Assert.AreEqual(84f, dungeon.Exit.PositionX, 0.0001f);
            Assert.AreEqual(-0.5f, dungeon.Exit.RotationW, 0.0001f);
        }

        [TestMethod]
        [DataRow("case-a.json", "{ \"version\": 2, \"id\": 1, \"name\": \"bad\", \"kind\": \"world\", \"rules\": {} }", "version")]
        [DataRow("case-b.json", "{ \"version\": 1, \"id\": 0, \"name\": \"bad\", \"kind\": \"world\", \"rules\": {} }", "id")]
        [DataRow("case-c.json", "{ \"version\": 1, \"id\": 32768, \"name\": \"bad\", \"kind\": \"world\", \"rules\": {} }", "id")]
        [DataRow("case-d.json", "{ \"version\": 1, \"id\": 1, \"name\": \"bad\", \"kind\": \"world\", \"rules\": { \"pvp\": true, \"nope\": true } }", "rules.nope")]
        [DataRow("case-e.json", "{ \"version\": 1, \"id\": 1, \"name\": \"bad\", \"kind\": \"world\", \"rules\": { \"pvp\": \"yes\" } }", "rules.pvp")]
        [DataRow("case-f.json", "{ \"version\": 1, ", "json")]
        [DataRow("case-g.json", "{ \"version\": 1, \"id\": 3, \"name\": \"bad\", \"kind\": \"ephemeral-template\", \"rules\": {}, \"landblocks\": [\"0x10000\"], \"ttlMinutes\": 5, \"entry\": { \"cell\": \"0x00010001\", \"x\": 0, \"y\": 0, \"z\": 0, \"qw\": 1, \"qx\": 0, \"qy\": 0, \"qz\": 0 }, \"exit\": { \"cell\": \"0xA9B40019\", \"x\": 0, \"y\": 0, \"z\": 0, \"qw\": 1, \"qx\": 0, \"qy\": 0, \"qz\": 0 } }", "landblocks")]
        [DataRow("case-h.json", "{ \"version\": 1, \"id\": 3, \"name\": \"bad\", \"kind\": \"ephemeral-template\", \"rules\": {}, \"landblocks\": [\"0x0150\"], \"ttlMinutes\": 0, \"entry\": { \"cell\": \"0x01500001\", \"x\": 0, \"y\": 0, \"z\": 0, \"qw\": 1, \"qx\": 0, \"qy\": 0, \"qz\": 0 }, \"exit\": { \"cell\": \"0xA9B40019\", \"x\": 0, \"y\": 0, \"z\": 0, \"qw\": 1, \"qx\": 0, \"qy\": 0, \"qz\": 0 } }", "ttlMinutes")]
        [DataRow("case-i.json", "{ \"version\": 1, \"id\": 3, \"name\": \"bad\", \"kind\": \"ephemeral-template\", \"rules\": {}, \"landblocks\": [\"0x0150\"], \"ttlMinutes\": 5, \"entry\": { \"cell\": \"0x01500001\", \"x\": 0, \"y\": 0, \"z\": 0, \"qw\": 1, \"qx\": 0, \"qy\": 0, \"qz\": 0 }, \"exit\": { \"cell\": \"0xA9B40019\", \"x\": 0, \"y\": 0, \"qw\": 1, \"qx\": 0, \"qy\": 0, \"qz\": 0 } }", "exit.z")]
        public void BadFile_IsSkipped_WithADiagnosticNamingFileAndField_AndTheOthersStillLoad(string fileName, string json, string field)
        {
            Write(fileName, json);
            Write("good.json", Realm(id: "7", name: "\"good\""));

            var result = RealmLoader.LoadDirectory(directory);

            Assert.AreEqual(7, result.Realms.Single().Id);
            var diagnostic = result.Diagnostics.Single();
            StringAssert.Contains(diagnostic, fileName);
            StringAssert.Contains(diagnostic, $": {field}: ");
        }

        [TestMethod]
        [DataRow("second.json", "{ \"version\": 1, \"id\": 1, \"name\": \"second\", \"kind\": \"world\", \"rules\": {} }", "id")]
        [DataRow("second.json", "{ \"version\": 1, \"id\": 2, \"name\": \"first\", \"kind\": \"world\", \"rules\": {} }", "name")]
        public void DuplicateIdOrName_SkipsTheLaterFile_WithADiagnostic(string laterFile, string laterJson, string field)
        {
            Write("first.json", Realm(id: "1", name: "\"first\""));
            Write(laterFile, laterJson);

            var result = RealmLoader.LoadDirectory(directory);

            Assert.AreEqual("first", result.Realms.Single().Name);
            var diagnostic = result.Diagnostics.Single();
            StringAssert.Contains(diagnostic, laterFile);
            StringAssert.Contains(diagnostic, $": {field}: ");
        }

        private const string EntryJson = "{ \"cell\": \"0x01500001\", \"x\": 0, \"y\": 0, \"z\": 0, \"qw\": 1, \"qx\": 0, \"qy\": 0, \"qz\": 0 }";
        // an outdoor cell's index follows its x and y (0x19 is cell 3,0 at x 84), so the exit's coordinates are the real ones
        private const string ExitJson = "{ \"cell\": \"0xA9B40019\", \"x\": 84, \"y\": 7.1, \"z\": 94, \"qw\": -0.5, \"qx\": 0, \"qy\": 0, \"qz\": 0.8660254 }";

        private void Write(string fileName, string json) => File.WriteAllText(Path.Combine(directory, fileName), json);

        private static string Realm(string version = "1", string id = "1", string name = "\"test-pvp\"", string rules = "{ \"pvp\": true }") =>
            $"{{ \"version\": {version}, \"id\": {id}, \"name\": {name}, \"kind\": \"world\", \"rules\": {rules} }}";
    }
}
