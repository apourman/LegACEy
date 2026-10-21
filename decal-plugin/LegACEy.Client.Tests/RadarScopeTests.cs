using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using Xunit;

namespace LegACEy.Client.Tests;

/// <summary>The radar's geometry and rules, with retail's coordinates: what the window draws, hovers and selects, with no game and no window.</summary>
public sealed class RadarScopeTests
{
    // Arwic's landblock 0xC6A9, outdoors (the cell's low bits below 0x100), 60 m east and 84 m north of its corner.
    private const uint Outdoors = 0xC6A90014;
    private const double Radius = 59;
    private static readonly RadarOptions Default = new();
    private static readonly RadarObject[] NoObjects = Array.Empty<RadarObject>();

    [Fact]
    public void Heading_up_turns_the_world_so_the_way_the_player_faces_is_up()
    {
        // Facing east: what lies east is ahead (up on the scope), what lies north is to the left.
        var player = Player(heading: 90);
        var objects = new[] { Object(1, east: 20, north: 0), Object(2, east: 0, north: 20) };

        var frame = RadarScope.Build(player, objects, Default, Radius);

        var ahead = Dot(frame, 1);
        Assert.Equal(Radius, ahead.At.X, 2);
        Assert.True(ahead.At.Y < Radius - 10);
        var left = Dot(frame, 2);
        Assert.True(left.At.X < Radius - 10);
        Assert.Equal(Radius, left.At.Y, 2);
    }

    [Fact]
    public void North_up_keeps_north_on_top_whatever_the_heading()
    {
        var player = Player(heading: 90);
        var objects = new[] { Object(1, east: 20, north: 0), Object(2, east: 0, north: 20) };

        var frame = RadarScope.Build(player, objects, Default with { NorthUp = true }, Radius);

        var east = Dot(frame, 1);
        Assert.True(east.At.X > Radius + 10);
        var north = Dot(frame, 2);
        Assert.True(north.At.Y < Radius - 10);
    }

    [Fact]
    public void A_dot_sits_at_its_distance_scaled_to_the_range()
    {
        // 35 m of a 70 m range is half the scope's radius from the centre.
        var frame = RadarScope.Build(Player(0), new[] { Object(1, east: 35, north: 0) }, Default, Radius);

        var dot = Dot(frame, 1);
        var from = Math.Sqrt(Math.Pow(dot.At.X - Radius, 2) + Math.Pow(dot.At.Y - Radius, 2));
        Assert.Equal(Radius / 2, from, 2);
    }

    [Fact]
    public void Objects_retail_never_shows_stay_hidden_and_others_draw_in_retail_colours()
    {
        var objects = new[]
        {
            Object(1, east: 5, north: 0, RadarBehavior.ShowNever),
            Object(2, east: 6, north: 0, RadarBehavior.ShowMovement, RadarColor.Red),
            Object(3, east: 7, north: 0, RadarBehavior.ShowAlways, RadarColor.Gold)
        };

        var frame = RadarScope.Build(Player(0), objects, Default, Radius);

        Assert.DoesNotContain(frame.Dots, dot => dot.Id == 1);
        Assert.Equal(RadarColor.Red, Dot(frame, 2).Colour);
        Assert.Equal(RadarColor.Gold, Dot(frame, 3).Colour);
    }

    [Fact]
    public void Dots_beyond_the_range_are_not_drawn_and_the_range_is_the_zoom()
    {
        var far = Object(1, east: 90, north: 0);

        Assert.Empty(RadarScope.Build(Player(0), new[] { far }, Default with { Zoom = 70 }, Radius).Dots);
        Assert.Single(RadarScope.Build(Player(0), new[] { far }, Default with { Zoom = 150 }, Radius).Dots);
    }

    [Fact]
    public void A_dot_more_than_5_metres_off_the_player_height_is_drawn_faded()
    {
        var level = Object(1, east: 10, north: 0, z: 0);
        var above = Object(2, east: 11, north: 0, z: 6);

        var frame = RadarScope.Build(Player(0), new[] { level, above }, Default, Radius);

        Assert.Equal(1.0, Dot(frame, 1).Alpha);
        Assert.Equal(0.65, Dot(frame, 2).Alpha, 3);
    }

    [Fact]
    public void Hover_reaches_6_pixels_and_reports_name_distance_and_compass_direction()
    {
        // North-east of the player: 20 m east and 20 m north, which is 28 m away.
        var frame = RadarScope.Build(Player(heading: 30), new[] { Object(1, east: 20, north: 20, name: "Mirra") }, Default, Radius);
        var dot = Dot(frame, 1);

        Assert.Same(dot, RadarScope.Hit(frame, new Point(dot.At.X + 5, dot.At.Y)));
        Assert.Null(RadarScope.Hit(frame, new Point(dot.At.X + 7, dot.At.Y)));
        Assert.Equal("Mirra · 28 m NE", RadarScope.Label(dot));
    }

    [Fact]
    public void Ground_is_the_nine_landblocks_around_the_player_outdoors_only()
    {
        var outdoors = RadarScope.Build(Player(0), Array.Empty<RadarObject>(), Default, Radius);
        var indoors = RadarScope.Build(Player(0, cell: 0xC6A90100), Array.Empty<RadarObject>(), Default, Radius);
        var off = RadarScope.Build(Player(0), Array.Empty<RadarObject>(), Default with { Map = false }, Radius);

        Assert.Equal(9, outdoors.Tiles.Count);
        Assert.Empty(indoors.Tiles);
        Assert.Empty(off.Tiles);
        // The player's own landblock: its corner is 60 m west and 84 m south of the player.
        Assert.Equal(new Rect(-60, -108, 192, 192), outdoors.Tiles.Single(tile => tile.Landblock == 0xC6A9).Metres);
    }

    [Fact]
    public void Coordinates_are_retails_form_for_the_player_and_none_without_a_player()
    {
        Assert.Equal("33.6N, 56.7E", RadarScope.Build(Player(0), Array.Empty<RadarObject>(), Default, Radius).Coordinates);
        Assert.Null(RadarScope.Build(null, Array.Empty<RadarObject>(), Default, Radius).Coordinates);
    }

    [Fact]
    public void The_plaque_is_the_coordinates_outdoors_and_until_the_plan_is_built()
    {
        Assert.Equal("33.6N, 56.7E", RadarScope.Build(Player(0, RadarPlanFixtures.DungeonCell), NoObjects, Default, Radius).Plaque);
        // With the map off no plan is drawn, so the plaque keeps the coordinates even with the plan in hand.
        var plan = new RadarPlan(RadarPlanFixtures.Dungeon(), 0x0101);
        Assert.Equal("33.6N, 56.7E", RadarScope.Build(Player(0, RadarPlanFixtures.DungeonCell), NoObjects, Default with { Map = false }, Radius, plan).Plaque);
    }

    [Fact]
    public void A_dungeons_plaque_names_the_place_and_counts_its_levels_from_the_top()
    {
        // 0x01EF is Arwic Mines on the shared list; the middle room is the second level of three.
        var plan = new RadarPlan(RadarPlanFixtures.Dungeon(0x01, 0xEF), 0x0101);

        Assert.Equal("Arwic Mines · Level 2 of 3", RadarScope.Build(Player(0, 0x01EF0101), NoObjects, Default, Radius, plan).Plaque);
    }

    [Fact]
    public void A_name_too_long_for_the_plaque_is_cut_with_an_ellipsis_and_the_level_still_shows()
    {
        // Black Totem Temple does not fit beside "Level 2 of 3" in the plaque's 27 characters, so it is cut to "Black Totem…".
        var plan = new RadarPlan(RadarPlanFixtures.Dungeon(0x00, 0x99), 0x0101);

        Assert.Equal("Black Totem… · Level 2 of 3", RadarScope.Build(Player(0, 0x00990101), NoObjects, Default, Radius, plan).Plaque);
    }

    [Fact]
    public void A_shared_or_unlisted_landblock_shows_the_level_only()
    {
        // 0x00C9 holds four Chambers, so none is named; Arwic's 0xC6A9 is not on the list.
        var shared = new RadarPlan(RadarPlanFixtures.Dungeon(0x00, 0xC9), 0x0101);
        var unlisted = new RadarPlan(RadarPlanFixtures.Dungeon(), 0x0101);

        Assert.Equal("Level 2 of 3", RadarScope.Build(Player(0, 0x00C90101), NoObjects, Default, Radius, shared).Plaque);
        Assert.Equal("Level 2 of 3", RadarScope.Build(Player(0, RadarPlanFixtures.DungeonCell), NoObjects, Default, Radius, unlisted).Plaque);
    }

    [Fact]
    public void A_server_name_wins_over_the_list_even_for_a_shared_landblock_and_an_empty_one_leaves_the_list()
    {
        var shared = new RadarPlan(RadarPlanFixtures.Dungeon(0x00, 0xC9), 0x0101);
        var named = RadarScope.Build(Player(0, 0x00C90101), NoObjects, Default, Radius, shared, serverName: cell => cell == 0x00C90101 ? "Chamber 8" : null);
        var empty = RadarScope.Build(Player(0, 0x00990101), NoObjects, Default, Radius, new RadarPlan(RadarPlanFixtures.Dungeon(0x00, 0x99), 0x0101),
            serverName: _ => string.Empty);

        Assert.Equal("Chamber 8 · Level 2 of 3", named.Plaque);
        Assert.Equal("Black Totem… · Level 2 of 3", empty.Plaque);
    }

    [Fact]
    public void In_a_building_the_plaque_is_the_coordinates_and_the_floor()
    {
        var plan = new RadarPlan(RadarPlanFixtures.Town(), 0x0200);

        // The town's house has an upstairs above its ground floor; the far building is not this place.
        Assert.Equal("33.6N, 56.7E · Ground", RadarScope.Build(Player(0, RadarPlanFixtures.HouseCell), NoObjects, Default, Radius, plan).Plaque);
        Assert.Equal("33.6N, 56.7E · Upstairs", RadarScope.Build(Player(0, 0xC6A90201), NoObjects, Default, Radius, plan).Plaque);
    }

    [Fact]
    public void The_real_DAT_plaques_name_the_arwic_houses_floors_and_0x0099_by_its_level()
    {
        using var dat = OpenDats();
        if (dat == null) return;

        // Arwic's house 0x0168 has an upstairs, its ground floor and a cellar, top first: one cell of each level stands in for it.
        var house = dat.ReadPlan(0xC6, 0xA9);
        var plan = new RadarPlan(house, 0x0168);
        var floors = house.Place(0x0168).Levels.Select(level => 0xC6A90000u | level.Cells[0].Id);
        Assert.Equal(new[] { "33.6N, 56.7E · Upstairs", "33.6N, 56.7E · Ground", "33.6N, 56.7E · Cellar" },
            floors.Select(cell => RadarScope.Build(Player(0, cell), NoObjects, Default, Radius, plan).Plaque));

        // The start of 0x0099 is in its lowest room, the sixth level of six.
        var temple = new RadarPlan(dat.ReadPlan(0x00, 0x99), 0x0100);
        Assert.Equal("Black Totem… · Level 6 of 6", RadarScope.Build(Player(0, 0x00990100), NoObjects, Default, Radius, temple).Plaque);
    }

    [Theory]
    [InlineData(70, 10, 25)]
    [InlineData(70, -10, 150)]
    [InlineData(25, 1, 25)]
    [InlineData(150, -1, 150)]
    public void Wheel_up_zooms_in_and_down_zooms_out_within_25_and_150_metres(int range, int notches, int expected) =>
        Assert.Equal(expected, RadarZoom.Wheel(range, notches));

    [Fact]
    public void The_zoom_options_step_through_the_presets_from_the_current_range()
    {
        Assert.Equal(40, RadarZoom.Next(25));
        Assert.Equal(110, RadarZoom.Next(70));
        Assert.Equal(25, RadarZoom.Next(150));
    }

    [Fact]
    public void Options_round_trip_through_the_two_numbers_kept_per_character()
    {
        var options = new RadarOptions(Map: false, NorthUp: true, Rings: true, Zoom: 110, IndoorZoom: 20);

        var (flags, zoom) = options.Encode();

        Assert.Equal(options, RadarOptions.Decode(flags, zoom));
    }

    [Fact]
    public void Options_saved_before_the_indoor_zoom_existed_keep_the_default_indoor_zoom()
    {
        // The flags as they were saved: north up only, the map off.
        var decoded = RadarOptions.Decode(2, 110);

        Assert.Equal(new RadarOptions(Map: false, NorthUp: true, Zoom: 110), decoded);
        Assert.Equal(40, decoded.IndoorZoom);
    }

    [Fact]
    public void Indoors_the_radar_draws_the_plan_and_outdoors_the_ground()
    {
        var plan = new RadarPlan(RadarPlanFixtures.Dungeon(), 0x0101);

        var indoors = RadarScope.Build(Player(0, RadarPlanFixtures.DungeonCell), NoObjects, Default, Radius, plan);
        var outdoors = RadarScope.Build(Player(0), NoObjects, Default, Radius, plan);

        Assert.Equal(3, indoors.PlanLevels.Count);
        Assert.Empty(indoors.Tiles);
        Assert.Empty(outdoors.PlanLevels);
        Assert.Equal(9, outdoors.Tiles.Count);
    }

    [Fact]
    public void The_players_level_is_the_solid_one_and_the_other_levels_are_faint()
    {
        var plan = new RadarPlan(RadarPlanFixtures.Dungeon(), 0x0101);

        var frame = RadarScope.Build(Player(0, RadarPlanFixtures.DungeonCell), NoObjects, Default, Radius, plan);

        // Levels run top first, so the middle room's level is the second.
        Assert.Same(plan.Levels[1], Assert.Single(frame.PlanLevels, level => level.Solid).Level);
        Assert.Equal(2, frame.PlanLevels.Count(level => !level.Solid));
    }

    [Fact]
    public void In_a_building_only_its_cells_are_drawn_and_the_ground_stays_under_them()
    {
        var plan = new RadarPlan(RadarPlanFixtures.Town(), 0x0200);

        var frame = RadarScope.Build(Player(0, RadarPlanFixtures.HouseCell), NoObjects, Default, Radius, plan);

        // The landblock has three levels, the far building's among them; the place draws its own two, the ground floor solid.
        Assert.Equal(2, frame.PlanLevels.Count);
        Assert.Same(plan.Levels[1], frame.PlanLevels.Single(level => level.Solid).Level);
        Assert.Equal(9, frame.Tiles.Count);
    }

    [Theory]
    [InlineData(40, 10, 12)]
    [InlineData(40, -10, 150)]
    [InlineData(12, 1, 12)]
    public void Indoor_wheel_stays_within_12_and_150_metres(int range, int notches, int expected) =>
        Assert.Equal(expected, RadarZoom.Wheel(range, notches, indoors: true));

    [Fact]
    public void The_indoor_zoom_presets_stay_within_12_and_150_metres()
    {
        Assert.All(RadarZoom.IndoorPresets, preset => Assert.InRange(preset, RadarZoom.IndoorMin, RadarZoom.Max));
        Assert.Equal(40, RadarZoom.Next(25, indoors: true));
        Assert.Equal(12, RadarZoom.Next(150, indoors: true));
    }

    [Fact]
    public void An_indoor_zoom_out_of_range_is_clamped_when_it_is_saved()
    {
        var (flags, zoom) = new RadarOptions(IndoorZoom: 500).Encode();

        Assert.Equal(150, RadarOptions.Decode(flags, zoom).IndoorZoom);
    }

    [Fact]
    public void Indoors_the_zoom_is_the_indoor_one_and_the_outdoor_zoom_is_untouched()
    {
        var options = new RadarOptions(Zoom: 70, IndoorZoom: 20);
        var plan = new RadarPlan(RadarPlanFixtures.Dungeon(), 0x0101);

        var indoors = RadarScope.Build(Player(0, RadarPlanFixtures.DungeonCell), NoObjects, options, Radius, plan);
        var outdoors = RadarScope.Build(Player(0), NoObjects, options, Radius, plan);

        Assert.Equal(Radius / 20, indoors.PixelsPerMetre, 4);
        Assert.Equal(Radius / 70, outdoors.PixelsPerMetre, 4);
    }

    [Fact]
    public void With_the_map_off_indoors_the_dots_are_drawn_on_dark()
    {
        var plan = new RadarPlan(RadarPlanFixtures.Dungeon(), 0x0101);

        var objects = new[] { Object(1, east: 10, north: 0) };

        var frame = RadarScope.Build(Player(0, RadarPlanFixtures.DungeonCell), objects, Default with { Map = false }, Radius, plan);

        Assert.Empty(frame.PlanLevels);
        Assert.Empty(frame.Tiles);
        Assert.Single(frame.Dots);
    }

    [Fact]
    public void Indoors_before_the_plan_is_built_the_radar_draws_dots_on_dark()
    {
        var frame = RadarScope.Build(Player(0, RadarPlanFixtures.DungeonCell), new[] { Object(1, east: 10, north: 0) }, Default, Radius, null);

        Assert.Empty(frame.PlanLevels);
        Assert.Single(frame.Dots);
    }

    [Fact]
    public void The_ladder_has_a_pip_for_each_level_and_the_players_is_the_one_it_is_on()
    {
        var plan = new RadarPlan(RadarPlanFixtures.Dungeon(), 0x0101);

        var frame = RadarScope.Build(Player(0, RadarPlanFixtures.DungeonCell), NoObjects, Default, Radius, plan);

        Assert.Equal(new RadarLadder(Levels: 3, Current: 1, Peek: -1, Occupied: 0), frame.Ladder);
    }

    [Fact]
    public void A_pip_is_ringed_when_someone_within_range_is_on_its_level_and_not_for_the_players_own()
    {
        var plan = new RadarPlan(RadarPlanFixtures.Dungeon(), 0x0101);
        var objects = new[]
        {
            Object(1, east: 5, north: 0, z: 8),
            Object(2, east: 6, north: 0, z: 4),
            Object(3, east: 90, north: 0, z: 0),
        };

        var frame = RadarScope.Build(Player(0, RadarPlanFixtures.DungeonCell), objects, Default, Radius, plan);

        // The top level has someone in range; the player's own has someone too, but its pip is gold already; the bottom one is out of range.
        Assert.Equal(1UL << 0, frame.Ladder!.Occupied);
    }

    [Fact]
    public void Peeking_a_pip_draws_that_level_solid_in_teal_and_the_players_level_stays_as_it_was()
    {
        var plan = new RadarPlan(RadarPlanFixtures.Dungeon(), 0x0101);

        var frame = RadarScope.Build(Player(0, RadarPlanFixtures.DungeonCell), NoObjects, Default, Radius, plan, peek: 0);

        var peeked = Assert.Single(frame.PlanLevels, level => level.Solid);
        Assert.Same(plan.Levels[0], peeked.Level);
        Assert.True(peeked.Peek);
        // The player's own level is left out while a level is peeked; the one below it is faint.
        Assert.Equal(2, frame.PlanLevels.Count);
        Assert.Contains(frame.PlanLevels, draw => !draw.Solid && ReferenceEquals(draw.Level, plan.Levels[2]));
        Assert.DoesNotContain(frame.PlanLevels, draw => ReferenceEquals(draw.Level, plan.Levels[1]));
        Assert.Equal(new RadarLadder(Levels: 3, Current: 1, Peek: 0, Occupied: 0), frame.Ladder);
    }

    [Fact]
    public void A_dot_on_another_level_is_an_arrow_and_its_label_says_above_or_below()
    {
        var plan = new RadarPlan(RadarPlanFixtures.Dungeon(), 0x0101);
        var objects = new[]
        {
            Object(1, east: 5, north: 0, z: 8),
            Object(2, east: 6, north: 0, z: 0),
            Object(3, east: 7, north: 0, z: 4),
        };

        var frame = RadarScope.Build(Player(0, RadarPlanFixtures.DungeonCell), objects, Default, Radius, plan);

        // A dot is an arrow by its level, not by how far above it is: the one 8 m up is on the level above, the one 4 m up on the player's own.
        Assert.Equal(RadarFloor.Above, Dot(frame, 1).Floor);
        Assert.Equal(RadarFloor.Below, Dot(frame, 2).Floor);
        Assert.Equal(RadarFloor.Same, Dot(frame, 3).Floor);
        Assert.Equal(1.0, Dot(frame, 1).Alpha);
        Assert.EndsWith(" · above", RadarScope.Label(Dot(frame, 1)));
        Assert.EndsWith(" · below", RadarScope.Label(Dot(frame, 2)));
        Assert.DoesNotContain("above", RadarScope.Label(Dot(frame, 3)));
    }

    [Fact]
    public void Stair_marks_sit_at_the_doors_to_another_level_and_point_up_when_the_door_leads_up()
    {
        var plan = new RadarPlan(RadarPlanFixtures.Dungeon(), 0x0101);

        var frame = RadarScope.Build(Player(0, RadarPlanFixtures.DungeonCell), NoObjects, Default, Radius, plan);

        // The doorways are in the room's east wall, 2 m east of the player: the one up is 0.5 m south of the player's line, the one down 1.5 m.
        var metres = Radius / 40;
        Assert.Equal(new Point(Radius + 2 * metres, Radius + 0.5 * metres), Assert.Single(frame.StairMarks, mark => mark.Up).At);
        Assert.Equal(new Point(Radius + 2 * metres, Radius + 1.5 * metres), Assert.Single(frame.StairMarks, mark => !mark.Up).At);
    }

    [Fact]
    public void Stair_marks_turn_with_the_world_and_keep_their_direction()
    {
        var plan = new RadarPlan(RadarPlanFixtures.Dungeon(), 0x0101);

        var ahead = RadarScope.Build(Player(0, RadarPlanFixtures.DungeonCell), NoObjects, Default, Radius, plan).StairMarks;
        var turned = RadarScope.Build(Player(90, RadarPlanFixtures.DungeonCell), NoObjects, Default, Radius, plan).StairMarks;

        Assert.Equal(ahead.Select(mark => mark.Up), turned.Select(mark => mark.Up));
        Assert.All(Enumerable.Range(0, ahead.Count), i => Assert.Equal(Distance(ahead[i].At), Distance(turned[i].At), 6));
        Assert.NotEqual(ahead[0].At, turned[0].At);
    }

    [Fact]
    public void A_door_out_of_the_building_gets_no_stair_mark()
    {
        var plan = new RadarPlan(RadarPlanFixtures.Town(), 0x0200);

        var frame = RadarScope.Build(Player(0, RadarPlanFixtures.HouseCell), NoObjects, Default, Radius, plan);

        // The ground floor's door out to the town has no level, so only the stair up to the upstairs is marked.
        Assert.Equal(new[] { true }, frame.StairMarks.Select(mark => mark.Up));
    }

    private static double Distance(Point point) => Math.Sqrt(Math.Pow(point.X - Radius, 2) + Math.Pow(point.Y - Radius, 2));

    private static RadarPlayer Player(float heading, uint cell = Outdoors) =>
        new(new RadarPosition(cell, 60, 84, 0), heading);

    // East and north of the player, in the same landblock.
    private static RadarObject Object(uint id, float east, float north, RadarBehavior behaviour = RadarBehavior.ShowAlways,
        RadarColor colour = RadarColor.Yellow, float z = 0, string name = "Object") =>
        new(id, name, colour, behaviour, new RadarPosition(Outdoors, 60 + east, 84 + north, z));

    private static RadarScopeDot Dot(RadarScopeFrame frame, uint id) => frame.Dots.Single(dot => dot.Id == id);

    // The client's own DATs, when LEGACEY_CELL_DAT and LEGACEY_PORTAL_DAT are set; null otherwise, and the test passes without them.
    private static IndoorDat? OpenDats()
    {
        var cellPath = Environment.GetEnvironmentVariable("LEGACEY_CELL_DAT");
        var portalPath = Environment.GetEnvironmentVariable("LEGACEY_PORTAL_DAT");
        return string.IsNullOrWhiteSpace(cellPath) || string.IsNullOrWhiteSpace(portalPath) ? null : new IndoorDat(portalPath, cellPath);
    }
}
