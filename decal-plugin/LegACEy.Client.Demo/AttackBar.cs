using System;

namespace LegACEy.Client.Demo;

/// <summary>The attack height retail swings at, as the plaques read from the top: High, Medium, Low.</summary>
public enum AttackHeight { High, Medium, Low }

/// <summary>
/// What the attack bar reads from the game and does in it: retail's combat stance, set point, attack height, charge and character options. The
/// client implements it over retail and Decal; tests fake it. <see cref="Changed"/> is raised on the UI thread when a value changes from outside
/// the window, such as a retail key or a swing ending. The window reads the port back after its own clicks, so it need not wait for Changed.
/// </summary>
public interface IAttackBarPort
{
    /// <summary>The player's combat stance. The bar shows in melee and missile, and hides in peace and magic.</summary>
    RetailStance Stance { get; }
    /// <summary>The set point for the stance, from 0 to 1: the power in melee, the accuracy with a missile weapon. Setting it is a click on the bar.</summary>
    double SetPoint { get; set; }
    /// <summary>The attack height.</summary>
    AttackHeight Height { get; set; }
    /// <summary>The attack building toward the set point, from 0 to 1; 0 when no attack is building, so the fill is empty.</summary>
    double Charge { get; }
    /// <summary>The character option AutoRepeatAttack: Repeat Attacks.</summary>
    bool AutoRepeatAttack { get; set; }
    /// <summary>The character option AutoTarget: Auto Target.</summary>
    bool AutoTarget { get; set; }
    /// <summary>The character option ViewCombatTarget: Keep in View.</summary>
    bool ViewCombatTarget { get; set; }
    /// <summary>Whether the character has Recklessness trained or specialized, so the bar marks its range.</summary>
    bool Reckless { get; }
    /// <summary>Whether retail's UI is locked, in which case no window moves.</summary>
    bool UiLocked { get; }
    event EventHandler? Changed;
}
