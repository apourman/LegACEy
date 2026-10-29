using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The Dereth map window in place of retail's map panel (Map, House), held as the social panel is: retail's key and toolbar button open
/// and close our window, and its close box closes retail's panel. Everything the window shows is retail's own: gmMapUI's update writes the
/// date, time, coordinates and markers into retail's map page, and gmHouseUI keeps its house page's lines current from the server's house
/// messages; this reads them back. Call everything on the game thread.
/// </summary>
internal sealed unsafe class MapFeature : IRetailMap, IDisposable
{
    private const long RefreshMs = 1000;
    private const string Source = "our disassembly of the installed end-of-retail acclient.exe; names from acclient.pdb (near build, +0x330 here)";
    // The map and house pages, placed in the map panel's LayoutDesc 0x21000027 (from 0x21000026/0x100001E8 and 0x21000025/0x100001E5).
    private const uint MapPage = 0x100001F6, HousePage = 0x100001F7;
    // Their UIElement vtables, which tell a gmMapUI and a gmHouseUI from any other element.
    private const uint MapVtable = 0x007ABFC0, HouseVtable = 0x007AC5A8;
    /// <summary>gmMapUI::Update (thiscall, returns bool): the date and time, the coordinates and both markers, as retail's timer runs it.</summary>
    private const uint MapUpdate = 0x004A21E0;
    // gmMapUI's elements, as its PostInit finds them: the date and time text, the coordinates text, the player and house markers, the map.
    private const int DateText = 0x5FC, CoordinatesText = 0x600, PlayerMarker = 0x604, HouseMarker = 0x608, MapImage = 0x60C;
    /// <summary>gmHouseUI's list of lines (UIElement_ListBox), its items' array and count.</summary>
    private const int HouseList = 0x600, ListItems = 0x608, ListCount = 0x610;
    /// <summary>UIElement::DynamicCast's vtable slot, and UIElement_Text's type, as AddHousePanelText casts each new line.</summary>
    private const int DynamicCastSlot = 0x94;
    private const uint TextType = 0x0C;
    /// <summary>UIElement_Text::GetText (thiscall, PStringBase&lt;ushort&gt;* out), and the string's destructor.</summary>
    private const uint GetText = 0x00466830, ReleaseString = 0x004011B0;
    /// <summary>UIElement's own visible flag: bit 1 of +0xA4. IsVisible also asks every ancestor, and retail's map page may be hidden.</summary>
    private const int ElementFlags = 0xA4;
    /// <summary>The places gmMapUI::PostInit names on the map: 53 records of x, y, width, height and a wide-string name.</summary>
    private const uint PlaceTable = 0x007AB490;
    private const int PlaceCount = 0x35, PlaceStride = 0x14;

    // Bodies only: Decal patches prologues in the running client.
    private static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        Entry("gmMapUI::GetUIElementType returns 0x10000026", 0x004A1DF0, "B8 26 00 00 10 C3"),
        Entry("gmMapUI's vtable: DynamicCast and GetUIElementType at +0x94", MapVtable + 0x94, "D0 1D 4A 00 F0 1D 4A 00"),
        Entry("gmHouseUI::GetUIElementType returns 0x10000025", 0x004A28F0, "B8 25 00 00 10 C3"),
        Entry("gmHouseUI's vtable: DynamicCast and GetUIElementType at +0x94", HouseVtable + 0x94, "D0 28 4A 00 F0 28 4A 00"),
        Entry("gmMapUI::PostInit: date and time text (0x100001EB) at +0x5FC, then the coordinates", 0x004A1FCC, "68 EF 01 00 10 8B CE 89 86 FC 05 00 00"),
        Entry("gmMapUI::PostInit: coordinates text at +0x600, then the player marker", 0x004A1FF2, "68 ED 01 00 10 8B CE 89 86 00 06 00 00"),
        Entry("gmMapUI::PostInit: player marker at +0x604, then the house marker", 0x004A2004, "68 EE 01 00 10 8B CE 89 86 04 06 00 00"),
        Entry("gmMapUI::PostInit: house marker at +0x608, then the map", 0x004A2016, "68 EC 01 00 10 8B CE 89 86 08 06 00 00"),
        Entry("gmMapUI::PostInit: the map at +0x60C", 0x004A202A, "89 86 0C 06 00 00"),
        Entry("gmMapUI::PostInit: 53 place records of 20 bytes at 0x7AB490", 0x004A20FD, "BF 90 B4 7A 00 BD 35 00 00 00"),
        Entry("gmMapUI::Update body: the next update time at +0x620, the date text at +0x5FC", 0x004A21EC,
            "DD 05 A8 79 83 00 53 DC 05 B0 A1 79 00 55 56 8B F1 8B 86 FC 05 00 00"),
        Entry("gmMapUI::Update: coordinates text and player marker", 0x004A2392, "8B 86 00 06 00 00 85 C0 0F 84 E8 01 00 00 8B 86 04 06 00 00"),
        Entry("gmHouseUI::AddHousePanelText: lines in the list at +0x600, each cast to text through +0x94", 0x004A2B40,
            "8B 89 00 06 00 00 85 C9 74 3F 6A 00 6A 00 E8 1D CB FC FF 85 C0 74 32 8B 10 56 6A 0C 8B C8 FF 92 94 00 00 00"),
        Entry("UIElement_ListBox::GetItem: items at +0x608, count at +0x610", 0x0046DC50,
            "8B 44 24 04 3B 81 10 06 00 00 73 0C 8B 89 08 06 00 00 8B 04 81 C2 04 00"),
        Entry("UIElement_Text::GetText body", GetText + 9, "8B 3D FC 31 79 00 89 44 24 0C 83 C0 F0 50 8B F1 FF D7 8D 44 24 0C 50 6A 00 8D 8E 0C 06 00 00"),
        Entry("PStringBase<wchar_t>::~PStringBase", ReleaseString, "56 8B 31 83 EE 14 8D 46 04 50 FF 15 F8 31 79 00"),
        Entry("UIElement::IsVisible reads the element's own flag, bit 1 of +0xA4", 0x004603CB, "8B 9E A4 00 00 00 D1 EB 80 E3 01")
    };

    private readonly ILegACEyPluginHost _host;
    private readonly RetailPanelTakeover? _panel;
    private readonly Action<string> _log;
    private MapWindow? _view;
    private Action? _hide;
    private bool _shown;
    private long _refreshedAt;
    private bool? _valid;
    private IReadOnlyList<MapPlace>? _places;

    public MapFeature(ILegACEyPluginHost host, IRetailPanelPort? port, Action<string> log)
    {
        _host = host;
        _log = log;
        if (port != null) _panel = new RetailPanelTakeover(port, OpenChanged, log);
    }

    /// <summary>Once a frame in game. With <paramref name="enabled"/> false retail's panel is given back, and our window closes.</summary>
    public void Tick(bool enabled, bool canShow)
    {
        _panel?.Tick(enabled, canShow);
        var now = Environment.TickCount64;
        if (_shown && _view != null && now - _refreshedAt >= RefreshMs)
        {
            _refreshedAt = now;
            _view.Refresh();
        }
    }

    /// <summary>Logoff, unload or the windows failing: gives retail's panel back and drops the window.</summary>
    public void EndSession()
    {
        _panel?.EndSession();
        _view = null;
        _hide = null;
        _shown = false;
    }

    public void Dispose() => _panel?.Dispose();

    private void OpenChanged(bool open)
    {
        if (open == _shown) return;
        if (!open)
        {
            _shown = false;
            _hide?.Invoke();
            return;
        }
        try { Open(); }
        catch (Exception exception)
        {
            _log($"The map window could not open: {exception.Message}");
            _panel?.Close();
        }
    }

    private void Open()
    {
        IGameArtSource? art = null;
        try { art = _host.Art; }
        catch (InvalidOperationException) { }
        // A window the client released is neither open nor hidden: the view is stale, so a new one is built.
        _view ??= new MapWindow(this, art);
        var view = _view;
        var place = _panel?.HeldPlace ?? new Point(1500, 150);
        var definition = new WindowDefinition(MapWindow.WindowId, "Map", MapWindow.Width, MapWindow.Height,
            DerethWindow.TitleBarHeight, new DerethClientTheme(), MapWindow.Sizing);
        var opened = _host.OpenWindow(definition, place, close =>
        {
            _hide = close;
            // The close box closes retail's panel, whose report hides the window; without the takeover it hides the window itself.
            view.Close = () =>
            {
                if (_panel?.Holds == true) _panel.Close();
                if (_shown) { _shown = false; _hide?.Invoke(); }
            };
            return view.Window;
        }, error =>
        {
            _log($"The map window failed: {error.Message}");
            if (ReferenceEquals(_view, view)) _view = null;
            _shown = false;
            _panel?.Close();
        }, ownChrome: true);
        if (!opened)
        {
            _panel?.Close();
            return;
        }
        _shown = true;
        _refreshedAt = Environment.TickCount64;
        view.Refresh();
    }

    // Retail's pages, as the window reads them.

    RetailMapView? IRetailMap.Read()
    {
        if (Page(MapPage, MapVtable) is not { } map) return null;
        // Retail runs the update on a timer only while its map page shows; ours may show while retail's House tab is up.
        ((delegate* unmanaged[Thiscall]<IntPtr, byte>)MapUpdate)(map);
        var image = Field(map, MapImage);
        if (image == IntPtr.Zero) return null;
        var origin = NativeUi.GetBounds(image).Location;
        return new RetailMapView(Text(Field(map, DateText)), Text(Field(map, CoordinatesText)),
            Centre(Field(map, PlayerMarker), origin), Centre(Field(map, HouseMarker), origin));
    }

    IReadOnlyList<string> IRetailMap.HouseLines()
    {
        var lines = new List<string>();
        if (Page(HousePage, HouseVtable) is not { } house) return lines;
        var list = Field(house, HouseList);
        if (list == IntPtr.Zero) return lines;
        var items = (IntPtr*)Field(list, ListItems);
        var count = *(uint*)(list + ListCount);
        for (var i = 0; i < count && items != null; i++)
        {
            var item = items[i];
            if (item == IntPtr.Zero) continue;
            var cast = (*(delegate* unmanaged[Thiscall]<IntPtr, uint, IntPtr>**)item)[DynamicCastSlot / IntPtr.Size];
            var text = cast(item, TextType);
            if (text != IntPtr.Zero) lines.Add(Text(text));
        }
        return lines;
    }

    IReadOnlyList<MapPlace> IRetailMap.Places
    {
        get
        {
            if (_places != null) return _places;
            var places = new List<MapPlace>();
            if (Valid())
            {
                for (var i = 0; i < PlaceCount; i++)
                {
                    var record = (int*)(PlaceTable + (uint)(i * PlaceStride));
                    var name = Marshal.PtrToStringUni(new IntPtr(record[4]));
                    if (!string.IsNullOrEmpty(name)) places.Add(new MapPlace(name, new Avalonia.Rect(record[0], record[1], record[2], record[3])));
                }
            }
            return _places = places;
        }
    }

    /// <summary>Retail's page with this id when the natives matched and the element is the page's own type; null otherwise.</summary>
    private IntPtr? Page(uint id, uint vtable)
    {
        if (!Valid()) return null;
        var page = NativeUi.GetElement(id);
        return page != IntPtr.Zero && *(uint*)page == vtable ? page : null;
    }

    private bool Valid()
    {
        if (_valid == null)
        {
            if (!NativeUi.Ready) return false;
            try { _valid = NativeUiCatalogue.Validate(NativeUi.ReadMemory, _log, Entries, "The map window is empty: the running client does not match retail's map natives."); }
            catch (Exception exception)
            {
                _log($"The map window is empty: its natives could not be checked: {exception.Message}");
                _valid = false;
            }
        }
        return _valid == true;
    }

    private static IntPtr Field(IntPtr element, int offset) => *(IntPtr*)(element + offset);

    /// <summary>A marker's centre on the map, or null while retail hides it.</summary>
    private static Avalonia.Point? Centre(IntPtr marker, Point origin)
    {
        if (marker == IntPtr.Zero || (*(uint*)(marker + ElementFlags) & 2) == 0) return null;
        var box = NativeUi.GetBounds(marker);
        return new Avalonia.Point(box.X - origin.X + box.Width / 2.0, box.Y - origin.Y + box.Height / 2.0);
    }

    /// <summary>A text element's text, through retail's own GetText.</summary>
    private static string Text(IntPtr element)
    {
        if (element == IntPtr.Zero) return string.Empty;
        IntPtr text = IntPtr.Zero;
        ((delegate* unmanaged[Thiscall]<IntPtr, IntPtr*, IntPtr*>)GetText)(element, &text);
        try { return text == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(text) ?? string.Empty; }
        finally { if (text != IntPtr.Zero) ((delegate* unmanaged[Thiscall]<IntPtr*, void>)ReleaseString)(&text); }
    }

    private static NativeUiEntry Entry(string name, uint address, string bytes) =>
        new(name, address, NativeUiCatalogue.Bytes(bytes), Source, "ThisCall");
}
