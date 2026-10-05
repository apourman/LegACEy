using Avalonia.Styling;

namespace LegACEy.Client.Themes;

/// <summary>A set of Avalonia styles that can be replaced while a panel remains open.</summary>
public interface IClientTheme
{
    string Name { get; }
    bool UsesAcChrome { get; }
    IStyle CreateStyles();
}
