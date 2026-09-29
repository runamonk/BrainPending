# Vendored editor

Source: https://github.com/centwon/AvaloniaRichEditor
Version: 1.2.1, commit `837242c5769407dbabd3b6c201409fb586d05905`.
Licence: MIT; see LICENSE and THIRD-PARTY-NOTICES.md.

The library is built from pinned source because its released RTF formatter drops
hyperlink destinations, font families on read, and text highlighting. Brain Pending
requires those properties to survive reopening a note.

Local changes:
- Simplified project file for local builds; Avalonia pinned to 12.1.3.
- RTF formatter: standard hyperlink fields, font table reading, highlight colour
  read/write, scoped character state, paragraph alignment, indentation, headings,
  margins, and line spacing.
- Formatting toolbar: expose insert/edit hyperlink and clear-formatting actions.
- Opt-in `Compact` toolbar layout puts common character controls, lists, image insertion,
  and table insertion directly in the wrapping toolbar, with no overflow menu.
- Theme-aware toolbar icons, popup surfaces and status text; transparent toolbar
  and status backgrounds inherit the application's canvas.
- Opt-in `UseThemeColors` lightens dark document ink for a dark canvas at render
  time, without modifying RTF formatting. Explicit text highlights and paper mode
  retain their original ink colours.
- Configurable `ThemeForeground` lets the host match document text and caret to
  its theme without changing the saved document.

Regression coverage lives in tests/BrainPending.Tests/EditorTests.cs. Review these
patches before updating upstream; do not replace this project with the unpatched
NuGet package without passing the persistence tests.

- Toolbar dividers, control outlines, and popup borders use dynamic theme brushes instead of fixed gray colors.
- Link tooltips prefer above the hovered text, flip below at screen edges, and stay hidden
  while the editor context menu is open.
- Tooltip anchors use window coordinates so editor offsets do not displace them into the sidebar.
- Clipboard paragraph fragments retain all spacing properties; pasted boundary paragraphs use
  source formatting when appropriate, and single-paragraph pastes do not add a trailing line.
  RTF explicitly writes zero paragraph spacing so it survives clipboard and save/reopen round trips.
- HTML paste leaves default link ink unset so it follows LinkForeground; themed rendering also
  recognizes legacy blue link ink. Explicit custom link colours remain supported.
- Images show text-selection overlays and allow drag selection from/through the image.
  Mixed clipboard selections include HTML and RTF with embedded pictures, and trim boundary
  paragraphs instead of copying unselected text. Selections within a cell retain block images.
- `LinkHandler` lets the app handle notebook attachment links before the editor's
  normal HTTP/HTTPS-only launcher. Other local/custom schemes remain blocked.
