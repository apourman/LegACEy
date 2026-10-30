using System;
using System.Collections.Generic;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The examination panel's port over the installed client. Its root element is the panel itself (no frame). It is hidden directly, not
/// through gmPanelUI's panel switch: its layout (0x2100006B) carries no panel id to send that switch, so there is no switch route to use.
/// The shared parts are in <see cref="NativePanelPort"/>.
/// </summary>
internal sealed class NativeExaminationPort : NativePanelPort
{
    // An ancestor walk bound: the examination layout is a few levels deep, so a longer chain is not the panel.
    private const int MaxDepth = 32;
    private const string Source = "our disassembly of the installed end-of-retail acclient.exe; names from acclient.pdb (near build)";
    /// <summary>SpellExamineUI's vtable, which its constructor writes with its gmExaminationUI at + 0x70.</summary>
    private const uint SpellPageVtable = 0x007B03B8;
    /// <summary>gmExaminationUI: the active page (SetActiveExamineUI) and the spell page; SpellExamineUI: its panel and the spell it shows.</summary>
    private const int ActivePage = 0x5FC, SpellPage = 0x60C, PagePanel = 0x70, PageSpell = 0x94;

    // The prologues are left out: Decal patches prologues in the running client.
    private static readonly IReadOnlyList<NativeUiEntry> SpellEntries = new[]
    {
        new NativeUiEntry("SpellExamineUI's constructor keeps its gmExaminationUI at +0x70 and writes its vtable", 0x004ABFF5,
            NativeUiCatalogue.Bytes("89 4E 70 C7 06 B8 03 7B 00"), Source, "instruction reference"),
        new NativeUiEntry("gmExaminationUI::ExamineSpell hands the spell to its spell page (+0x60C) and makes it the active page (+0x5FC)", 0x004B7565,
            NativeUiCatalogue.Bytes("8B 44 24 08 8B 8E 0C 06 00 00 50 E8 BB F8 FF FF 8B 86 0C 06 00 00 39 86 FC 05 00 00"), Source,
            "instruction reference"),
        new NativeUiEntry("SpellExamineUI::ExamineSpell keeps the spell at +0x94", 0x004B6EDC, NativeUiCatalogue.Bytes("89 B3 94 00 00 00"), Source,
            "instruction reference"),
    };

    // Null until checked.
    private bool? _spellOk;
    private bool _panelMissing;

    protected override IntPtr Element => NativeUi.GetElement(NativeUi.Examination);

    /// <summary>
    /// Checks, at runtime, that the root element is the examination panel: the inscription field resolves and sits under the root. Read
    /// before the panel is parked. A read that throws propagates, so the caller logs its reason and leaves retail's panel alone.
    /// </summary>
    public bool VerifyRoot()
    {
        var root = Element;
        var field = NativeUi.GetElement(NativeUi.ExaminationInscription);
        if (root == IntPtr.Zero || field == IntPtr.Zero) return false;
        var current = field;
        for (var depth = 0; depth < MaxDepth && current != IntPtr.Zero; depth++)
        {
            if (current == root) return true;
            current = NativeUi.Virtual<NativeUi.GetParentFn>(current, NativeUi.GetParentSlot)(current);
        }
        return false;
    }

    /// <summary>
    /// The spell retail's panel is showing (a click on the spell bar examines one), or zero when it shows an object or the client does not
    /// match. Retail sets the active page on every examination, so it is current whenever the panel opens. The gmExaminationUI is found
    /// from the inscription field up, as the element whose spell page (+ 0x60C) is a SpellExamineUI pointing back at it: its vtable is
    /// not relied on, since the element placed with the panel's id did not carry it in game. Every read is checked.
    /// </summary>
    public uint ExaminedSpell(Action<string> log)
    {
        _spellOk ??= NativeUiCatalogue.Validate(NativeUi.ReadMemory, log, SpellEntries,
            "A spell examined from retail's spell bar shows the selection instead: the running client does not match.");
        if (_spellOk != true) return 0;
        var current = NativeUi.GetElement(NativeUi.ExaminationInscription);
        for (var depth = 0; depth < MaxDepth && current != IntPtr.Zero; depth++)
        {
            var panel = unchecked((uint)current.ToInt32());
            var page = Read(panel + SpellPage);
            if (page != 0 && Read(page) == SpellPageVtable && Read(page + PagePanel) == panel)
                return Read(panel + ActivePage) == page ? Read(page + PageSpell) : 0;
            current = NativeUi.Virtual<NativeUi.GetParentFn>(current, NativeUi.GetParentSlot)(current);
        }
        if (!_panelMissing)
            log("A spell examined from retail's spell bar shows the selection instead: retail's examination panel was not found.");
        _panelMissing = true;
        return 0;
    }

    /// <summary>A word of the client's memory, or zero when it cannot be read.</summary>
    private static uint Read(uint address)
    {
        try
        {
            var bytes = NativeUi.ReadMemory(address, 4);
            return bytes.Length == 4 ? BitConverter.ToUInt32(bytes, 0) : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
