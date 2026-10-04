using System;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace LegACEy.Client.Demo;

/// <summary>A small themed panel bound to the current game-state port.</summary>
public sealed class LiveGameDataPanel : UserControl, IDisposable
{
    private readonly IGameStatePort _state;
    private readonly TextBlock _identity;
    private readonly TextBlock[] _values = new TextBlock[3];
    private readonly ProgressBar[] _bars = new ProgressBar[3];
    private bool _disposed;

    public LiveGameDataPanel(IGameStatePort state)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        var stack = new StackPanel { Spacing = 10, Margin = new Avalonia.Thickness(12) };
        stack.Children.Add(new TextBlock { Text = "Live character data", FontSize = 18, FontWeight = FontWeight.Bold });
        _identity = new TextBlock();
        stack.Children.Add(_identity);
        AddVital(stack, 0, "Health");
        AddVital(stack, 1, "Stamina");
        AddVital(stack, 2, "Mana");
        Content = stack;
        _state.Changed += OnChanged;
        Update(_state.Current);
    }

    private void AddVital(Panel parent, int index, string name)
    {
        var row = new StackPanel { Spacing = 3 };
        _values[index] = new TextBlock { Text = name, FontWeight = FontWeight.SemiBold };
        _bars[index] = new ProgressBar { Minimum = 0, Maximum = 100, Height = 16 };
        row.Children.Add(_values[index]);
        row.Children.Add(_bars[index]);
        parent.Children.Add(row);
    }

    private void OnChanged(object? sender, EventArgs e) => Update(_state.Current);

    private void Update(GameStateSnapshot value)
    {
        _identity.Text = $"{value.CharacterName} · {value.ServerName}";
        SetVital(0, "Health", value.Health, value.MaxHealth);
        SetVital(1, "Stamina", value.Stamina, value.MaxStamina);
        SetVital(2, "Mana", value.Mana, value.MaxMana);
    }

    private void SetVital(int index, string name, int current, int maximum)
    {
        maximum = Math.Max(0, maximum);
        current = Math.Max(0, Math.Min(current, maximum));
        _values[index].Text = $"{name}: {current} / {maximum}";
        _bars[index].Maximum = Math.Max(1, maximum);
        _bars[index].Value = current;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _state.Changed -= OnChanged;
    }
}
