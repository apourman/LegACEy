using System;
using System.Collections.Generic;

namespace LegACEy.Client.Demo;

/// <summary>
/// What the spell bar reads from the game and does in it: retail's eight spell tabs, the spells on the open one and the spell selected there.
/// The client implements it over retail's own spell bar, so retail's keys and the bar stay in step; tests fake it. <see cref="Changed"/> is
/// raised when a value changes from outside the window, such as a retail key. The window reads the port back after its own clicks. The bar
/// is open in magic only, so the port has no stance.
/// </summary>
public interface ISpellBarPort
{
    /// <summary>The open tab, 0 to 7 (I to VIII). Setting it opens that tab, as a click on retail's tab does.</summary>
    int Tab { get; set; }
    /// <summary>The open tab's spells, in their order on the bar.</summary>
    IReadOnlyList<uint> Spells { get; }
    /// <summary>The spell selected on the open tab, which Cast casts; zero for none.</summary>
    uint Selected { get; }
    /// <summary>Selects a spell on the open tab, as a click on it does.</summary>
    void Select(uint spell);
    /// <summary>Casts the selected spell, as retail's Cast button does; with none selected retail says to select one.</summary>
    void Cast();
    /// <summary>Takes a spell off the open tab, as retail does when a drag of it starts.</summary>
    void Remove(uint spell);
    /// <summary>Puts a spell on the open tab before the spell at <paramref name="index"/>, or at its end for -1, and selects it.</summary>
    void Insert(uint spell, int index);
    /// <summary>Shows the spell in the appraisal window, as a click on a spell on retail's bar does.</summary>
    void Examine(uint spell);
    /// <summary>
    /// The wielded item whose on-use spell retail's bar offers at its left, such as an orb; null with none. Its name is retail's, the item and
    /// its spell: "White Sapphire Orb (Might of the 5 Mules)".
    /// </summary>
    SpellBarEndowment? Endowment { get; }
    /// <summary>Selects the item at the bar's left on the open tab, so Cast uses it, as a click on it does in retail.</summary>
    void SelectEndowment();
    /// <summary>Whether retail's UI is locked, in which case no window moves.</summary>
    bool UiLocked { get; }
    event EventHandler? Changed;
}

/// <summary>The item at the spell bar's left: its icon, retail's name for it with its spell, and whether it is the open tab's selection.</summary>
public sealed record SpellBarEndowment(ItemVisual Visual, string Name, bool Selected);
