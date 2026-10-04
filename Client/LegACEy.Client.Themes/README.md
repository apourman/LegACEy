# Client themes

The AC theme reads its artwork from the supplied `IGameArtSource`; game images
are not bundled. Missing sprites fall back to plain brushes and the close/check
controls retain their text or glyph fallback. The Simple theme removes the art
styles and uses the stock Avalonia templates again.

Control faces use native-size corner slices and cropped final edge tiles.
Fields, lists, scroll content and vital meters tile native textures. PanelHost
and the desktop preview set nearest-neighbour bitmap drawing on their root so
nested controls inherit it. Standard text entry, selection, checkbox and
scrollbar behavior remains Avalonia's behavior. Tooltip themes are carried to
their native popup roots and updated when the owning surface switches theme;
author-supplied tooltip themes take precedence.

## Sprite sources

- Frame placement follows OpenAC's `RetailChromeSprites` map: `060074C3`/`C4`/
  `C5`/`C6` are corners; `060074BF`/`C1`/`C0`/`C2` are top/bottom/left/right
  edges. Corners extend farther along the frame than its five-pixel edges.
- The scrollbar map follows OpenAC's `RetailScrollbarChrome`: vertical track
  `06004C5F`, thumb states `06004C63`–`65`, down states `06004C69`–`6B`, up
  states `06004C6C`–`6E`; horizontal counterparts start at `06004C7F`.
- Control faces `06004C4C`–`4E`, rows `060012B3`/`B4`, checkbox lamps
  `06004D15`–`17`, the vital fill `06004C3E`, and header texture `06001399`
  were inspected in the installed portal.dat.
- Close faces `06004D0C`/`0D` were confirmed from inventory close element
  `100001D2`'s `Normal` and `Normal_pressed` states in the installed UI layouts.

OpenAC is MIT licensed: https://github.com/apourman/OpenAC. Its maps are prior
art; the game images themselves continue to come from the user's data file.

The theme tests check rendered art through the panel host, native frame geometry,
partial-tile cropping, live button states, switching, and themed input behavior.
In-game visual matching and coexistence still require the Windows Decal play-test.
