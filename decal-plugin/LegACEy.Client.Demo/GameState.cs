using System;

namespace LegACEy.Client.Demo;

/// <summary>A render-thread snapshot of the character identity and current vitals.</summary>
public sealed class GameStateSnapshot : IEquatable<GameStateSnapshot>
{
    public GameStateSnapshot(string characterName, string serverName, int health, int maxHealth, int stamina, int maxStamina, int mana, int maxMana)
    {
        CharacterName = characterName ?? string.Empty;
        ServerName = serverName ?? string.Empty;
        Health = health; MaxHealth = maxHealth;
        Stamina = stamina; MaxStamina = maxStamina;
        Mana = mana; MaxMana = maxMana;
    }

    public string CharacterName { get; }
    public string ServerName { get; }
    public int Health { get; }
    public int MaxHealth { get; }
    public int Stamina { get; }
    public int MaxStamina { get; }
    public int Mana { get; }
    public int MaxMana { get; }

    public bool Equals(GameStateSnapshot? other) => other != null &&
        CharacterName == other.CharacterName && ServerName == other.ServerName &&
        Health == other.Health && MaxHealth == other.MaxHealth && Stamina == other.Stamina &&
        MaxStamina == other.MaxStamina && Mana == other.Mana && MaxMana == other.MaxMana;
    public override bool Equals(object? obj) => Equals(obj as GameStateSnapshot);
    public override int GetHashCode() => CharacterName.GetHashCode() ^ ServerName.GetHashCode() ^ Health ^ MaxHealth ^ Stamina ^ MaxStamina ^ Mana ^ MaxMana;
}

/// <summary>Read-only game data published by the host on its UI/render thread.</summary>
public interface IGameStatePort
{
    GameStateSnapshot Current { get; }
    event EventHandler? Changed;
}

/// <summary>Mutable port used by the Decal adapter and the test fake.</summary>
public sealed class GameStatePort : IGameStatePort
{
    public GameStatePort(GameStateSnapshot initial) => Current = initial ?? throw new ArgumentNullException(nameof(initial));
    public GameStateSnapshot Current { get; private set; }
    public event EventHandler? Changed;

    public void Publish(GameStateSnapshot value)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        if (Current.Equals(value)) return;
        Current = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Fixed game-state fake for framework tests.</summary>
public sealed class FakeGameState : IGameStatePort
{
    private readonly GameStatePort _port;

    public FakeGameState(string characterName = "Preview Character", string serverName = "Thistledown")
    {
        _port = new GameStatePort(new GameStateSnapshot(characterName, serverName, 86, 100, 64, 100, 37, 100));
    }

    public GameStateSnapshot Current => _port.Current;
    public event EventHandler? Changed { add => _port.Changed += value; remove => _port.Changed -= value; }
}
