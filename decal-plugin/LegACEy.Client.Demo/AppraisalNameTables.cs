namespace LegACEy.Client.Demo;

/// <summary>
/// The names the appraisal shows for a material and a creature type, indexed by the value the server sends. Generated from ACE.Entity's
/// MaterialType and CreatureType enums, with each name split into words and two spellings corrected. Retail reads the same names from the portal DAT, so a name can
/// differ from retail's in spelling. Update it when those enums change.
/// </summary>
internal static class AppraisalNameTables
{
    /// <summary>MaterialType value to name; empty for Unknown.</summary>
    internal static readonly string[] Materials =
    {
        "", "Ceramic", "Porcelain", "Cloth", "Linen", "Satin",
        "Silk", "Velvet", "Wool", "Gem", "Agate", "Amber",
        "Amethyst", "Aquamarine", "Azurite", "Black Garnet", "Black Opal", "Bloodstone",
        "Carnelian", "Citrine", "Diamond", "Emerald", "Fire Opal", "Green Garnet",
        "Green Jade", "Hematite", "Imperial Topaz", "Jet", "Lapis Lazuli", "Lavender Jade",
        "Malachite", "Moonstone", "Onyx", "Opal", "Peridot", "Red Garnet",
        "Red Jade", "Rose Quartz", "Ruby", "Sapphire", "Smoky Quartz", "Sunstone",
        "Tiger Eye", "Tourmaline", "Turquoise", "White Jade", "White Quartz", "White Sapphire",
        "Yellow Garnet", "Yellow Topaz", "Zircon", "Ivory", "Leather", "Armadillo Hide",
        "Gromnie Hide", "Reed Shark Hide", "Metal", "Brass", "Bronze", "Copper",
        "Gold", "Iron", "Pyreal", "Silver", "Steel", "Stone",
        "Alabaster", "Granite", "Marble", "Obsidian", "Sandstone", "Serpentine",
        "Wood", "Ebony", "Mahogany", "Oak", "Pine", "Teak",
    };

    /// <summary>CreatureType value to name.</summary>
    internal static readonly string[] Creatures =
    {
        "Invalid", "Olthoi", "Banderling", "Drudge", "Mosswart", "Lugian",
        "Tumerok", "Mite", "Tusker", "Phyntos Wasp", "Rat", "Auroch",
        "Cow", "Golem", "Undead", "Gromnie", "Reedshark", "Armoredillo",
        "Fae", "Virindi", "Wisp", "Knathtead", "Shadow", "Mattekar",
        "Mumiyah", "Rabbit", "Sclavus", "Shallows Shark", "Monouga", "Zefir",
        "Skeleton", "Human", "Shreth", "Chittick", "Moarsman", "Olthoi Larvae",
        "Slithis", "Deru", "Fire Elemental", "Snowman", "Unknown", "Bunny",
        "Lightning Elemental", "Rockslide", "Grievver", "Niffis", "Ursuin", "Crystal",
        "Hollow Minion", "Scarecrow", "Idol", "Empyrean", "Hopeslayer", "Doll",
        "Marionette", "Carenzi", "Siraluun", "Aun Tumerok", "Hea Tumerok", "Simulacrum",
        "Acid Elemental", "Frost Elemental", "Elemental", "Statue", "Wall", "Altered Human",
        "Device", "Harbinger", "Dark Sarcophagus", "Chicken", "Gotrok Lugian", "Margul",
        "Bleached Rabbit", "Nasty Rabbit", "Grimacing Rabbit", "Burun", "Target", "Ghost",
        "Fiun", "Eater", "Penguin", "Ruschk", "Thrungus", "Viamontian Knight",
        "Remoran", "Swarm", "Moar", "Enchanted Arms", "Sleech", "Mukkir",
        "Merwart", "Food", "Paradox Olthoi", "Harvest", "Energy", "Apparition",
        "Aerbax", "Touched", "Blighted Moarsman", "Gear Knight", "Gurog", "Anekshay",
    };
}
