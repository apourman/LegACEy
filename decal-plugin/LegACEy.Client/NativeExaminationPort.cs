using System;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The examination panel's port over the installed client. Its root element is the panel itself (no frame). It is hidden directly, not
/// through gmPanelUI's panel switch: its layout (0x2100006B) carries no panel id to send that switch, so there is no switch route to use.
/// The shared parts are in <see cref="NativePanelPort"/>.
/// </summary>
internal sealed class NativeExaminationPort : NativePanelPort
{
    protected override IntPtr Element => NativeUi.GetElement(NativeUi.Examination);
}
