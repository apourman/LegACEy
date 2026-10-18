using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

using ACE.Entity;

namespace ACE.Server.Realms
{
    public sealed class RealmLoadResult
    {
        public IReadOnlyList<RealmDefinition> Realms { get; init; }

        /// <summary>One line per skipped file, naming the file and the field</summary>
        public IReadOnlyList<string> Diagnostics { get; init; }
    }

    /// <summary>
    /// Reads realm files. A bad file is skipped with a diagnostic; the others still load.
    /// </summary>
    public static class RealmLoader
    {
        public const int KnownVersion = 1;
        public const int MaxRealmId = 0x7FFF;

        public static RealmLoadResult LoadDirectory(string directory)
        {
            var realms = new List<RealmDefinition>();
            var diagnostics = new List<string>();
            var owners = new Dictionary<ushort, string>();
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [RealmDefinition.BaseWorld.Name] = "the base world" };

            if (Directory.Exists(directory))
            {
                foreach (var path in Directory.GetFiles(directory, "*.json").OrderBy(path => path, StringComparer.Ordinal))
                {
                    var fileName = Path.GetFileName(path);
                    try
                    {
                        var realm = ParseFile(path);

                        if (owners.TryGetValue(realm.Id, out var idOwner))
                            throw new RealmFileException("id", $"{realm.Id} is already used by {idOwner}");
                        if (names.TryGetValue(realm.Name, out var nameOwner))
                            throw new RealmFileException("name", $"'{realm.Name}' is already used by {nameOwner}");

                        owners[realm.Id] = fileName;
                        names[realm.Name] = fileName;
                        realms.Add(realm);
                    }
                    catch (RealmFileException ex)
                    {
                        diagnostics.Add($"{fileName}: {ex.Field}: {ex.Message}; realm skipped");
                    }
                }
            }

            return new RealmLoadResult { Realms = realms, Diagnostics = diagnostics };
        }

        private static RealmDefinition ParseFile(string path)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(File.ReadAllText(path));
            }
            catch (JsonException ex)
            {
                throw new RealmFileException("json", $"not valid JSON ({ex.Message})");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new RealmFileException("file", $"could not be read ({ex.Message})");
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw new RealmFileException("json", "the file must hold one JSON object");

                var version = RequiredInt(root, "version");
                if (version != KnownVersion)
                    throw new RealmFileException("version", $"unknown version {version} (known: {KnownVersion})");

                var id = RequiredInt(root, "id");
                if (id < 1 || id > MaxRealmId)
                    throw new RealmFileException("id", $"{id} is out of range 1..{MaxRealmId}");

                var name = RequiredString(root, "name");
                if (name.Trim().Length == 0)
                    throw new RealmFileException("name", "must not be blank");

                var kind = RequiredString(root, "kind") switch
                {
                    "world" => RealmKind.World,
                    "ephemeral-template" => RealmKind.EphemeralTemplate,
                    var other => throw new RealmFileException("kind", $"'{other}' is not 'world' or 'ephemeral-template'"),
                };

                var rules = ParseRules(Required(root, "rules"));

                if (kind == RealmKind.World)
                    return new RealmDefinition { Id = (ushort)id, Name = name, Kind = kind, Rules = rules };

                var landblocks = RequiredLandblocks(root);
                var entry = RequiredPosition(root, "entry");
                if (!landblocks.Contains((ushort)(entry.Cell >> 16)))
                    throw new RealmFileException("entry", "must be in one of the landblocks");

                return new RealmDefinition
                {
                    Id = (ushort)id,
                    Name = name,
                    Kind = kind,
                    Rules = rules,
                    Landblocks = landblocks,
                    TtlMinutes = RequiredPositiveInt(root, "ttlMinutes"),
                    Entry = entry,
                    Exit = RequiredPosition(root, "exit"),
                };
            }
        }

        private static IReadOnlyDictionary<string, bool> ParseRules(JsonElement rules)
        {
            if (rules.ValueKind != JsonValueKind.Object)
                throw new RealmFileException("rules", "must be an object");

            var parsed = new Dictionary<string, bool>();
            foreach (var property in rules.EnumerateObject())
            {
                var field = "rules." + property.Name;
                var rule = RealmRules.Find(property.Name);
                if (rule == null)
                    throw new RealmFileException(field, "unknown rule");

                if (property.Value.ValueKind != JsonValueKind.True && property.Value.ValueKind != JsonValueKind.False)
                    throw new RealmFileException(field, "must be true or false");

                parsed[rule.Key] = property.Value.GetBoolean();
            }

            return parsed;
        }

        private static JsonElement Required(JsonElement obj, string field)
        {
            if (!obj.TryGetProperty(field, out var value))
                throw new RealmFileException(field, "is missing");
            return value;
        }

        private static int RequiredInt(JsonElement obj, string field)
        {
            var value = Required(obj, field);
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
                throw new RealmFileException(field, "must be a whole number");
            return number;
        }

        private static string RequiredString(JsonElement obj, string field)
        {
            var value = Required(obj, field);
            if (value.ValueKind != JsonValueKind.String)
                throw new RealmFileException(field, "must be a string");
            return value.GetString();
        }

        private static int RequiredPositiveInt(JsonElement obj, string field)
        {
            var number = RequiredInt(obj, field);
            if (number < 1)
                throw new RealmFileException(field, $"{number} must be a positive whole number");
            return number;
        }

        /// <summary>Hex landblock ids such as "0x0150": one or more, each 0x0000..0xFFFF</summary>
        private static IReadOnlyList<ushort> RequiredLandblocks(JsonElement obj)
        {
            const string field = "landblocks";
            var value = Required(obj, field);
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
                throw new RealmFileException(field, "must be a non-empty array of hex landblock ids such as \"0x0150\"");

            return value.EnumerateArray().Select(item =>
            {
                if (item.ValueKind != JsonValueKind.String || !TryParseHex(item.GetString(), out var landblock) || landblock > 0xFFFF)
                    throw new RealmFileException(field, $"'{item}' is not a hex landblock id such as \"0x0150\"");
                return (ushort)landblock;
            }).ToList();
        }

        /// <summary>
        /// A position: { "cell": "0x7F0401AD", "x", "y", "z", "qw", "qx", "qy", "qz" }. The cell is hex; the numbers are the @loc values.
        /// Names in a diagnostic are the parent field and the member, e.g. "exit.z".
        /// </summary>
        private static Position RequiredPosition(JsonElement obj, string field)
        {
            var value = Required(obj, field);
            if (value.ValueKind != JsonValueKind.Object)
                throw new RealmFileException(field, "must be an object with cell, x, y, z, qw, qx, qy and qz");

            if (!value.TryGetProperty("cell", out var cellValue) || cellValue.ValueKind != JsonValueKind.String)
                throw new RealmFileException($"{field}.cell", "must be a hex string such as \"0x7F0401AD\"");
            if (!TryParseHex(cellValue.GetString(), out var cell))
                throw new RealmFileException($"{field}.cell", $"'{cellValue.GetString()}' is not a hex cell id such as \"0x7F0401AD\"");

            double Number(string member) => RequiredNumber(value, $"{field}.{member}", member);

            return new Position(cell, (float)Number("x"), (float)Number("y"), (float)Number("z"),
                (float)Number("qx"), (float)Number("qy"), (float)Number("qz"), (float)Number("qw"));
        }

        private static bool TryParseHex(string text, out uint value)
        {
            value = 0;
            return text != null && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
        }

        private static double RequiredNumber(JsonElement obj, string field, string member)
        {
            if (!obj.TryGetProperty(member, out var value) || value.ValueKind != JsonValueKind.Number)
                throw new RealmFileException(field, "must be a number");
            return value.GetDouble();
        }

        private sealed class RealmFileException : Exception
        {
            public RealmFileException(string field, string message) : base(message)
            {
                Field = field;
            }

            public string Field { get; }
        }
    }
}
