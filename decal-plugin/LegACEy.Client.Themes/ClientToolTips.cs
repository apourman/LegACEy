using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace LegACEy.Client.Themes;

/// <summary>Carry a surface's tooltip theme across the separate native popup root.</summary>
internal static class ClientToolTips
{
    internal static readonly AttachedProperty<ControlTheme?> ThemeProperty =
        AvaloniaProperty.RegisterAttached<Control, ControlTheme?>("ClientToolTipTheme", typeof(ClientToolTips), inherits: true);
    private static readonly AttachedProperty<ControlTheme?> AppliedThemeProperty =
        AvaloniaProperty.RegisterAttached<ToolTip, ControlTheme?>("AppliedClientTheme", typeof(ClientToolTips));

    static ClientToolTips()
    {
        ToolTip.ToolTipOpeningEvent.AddClassHandler<Control>((owner, _) =>
        {
            var theme = owner.GetValue(ThemeProperty);
            var content = ToolTip.GetTip(owner);
            if (content == null || (theme == null && content is not ToolTip)) return;
            var tip = content as ToolTip;
            if (tip == null)
            {
                tip = new ToolTip { Content = content };
                ToolTip.SetTip(owner, tip);
            }
            ApplyTheme(tip, theme);
        });
        ThemeProperty.Changed.AddClassHandler<Control>((owner, change) =>
        {
            if (ToolTip.GetTip(owner) is ToolTip tip)
                ApplyTheme(tip, change.NewValue as ControlTheme);
        });
    }

    private static void ApplyTheme(ToolTip tip, ControlTheme? theme)
    {
        // An explicit author-supplied tooltip theme takes precedence over the surface theme.
        if (tip.Theme != null && !ReferenceEquals(tip.Theme, tip.GetValue(AppliedThemeProperty))) return;
        tip.SetCurrentValue(StyledElement.ThemeProperty, theme);
        tip.SetValue(AppliedThemeProperty, theme);
    }
}
