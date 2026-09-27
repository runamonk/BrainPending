# Vendored editor

Source: https://github.com/centwon/AvaloniaRichEditor
Version: 1.2.1, commit `837242c5769407dbabd3b6c201409fb586d05905`.
Licence: MIT; see LICENSE and THIRD-PARTY-NOTICES.md.

The library is built from pinned source because its released RTF formatter drops
hyperlink destinations, font families on read, and text highlighting. MyNotes
requires those properties to survive reopening a note.

Local changes:
- Simplified project file for local builds; Avalonia pinned to 12.1.3.
- RTF formatter: standard hyperlink fields, font table reading, highlight colour
  read/write, scoped character state, paragraph alignment, indentation, headings,
  margins, and line spacing.
- Formatting toolbar: expose insert/edit hyperlink and clear-formatting actions.
- Theme-aware toolbar icons, popup surfaces and status text; transparent toolbar
  and status backgrounds inherit the application's canvas.
- Opt-in `UseThemeColors` lightens dark document ink for a dark canvas at render
  time, without modifying RTF formatting. Explicit text highlights and paper mode
  retain their original ink colours.

Regression coverage lives in tests/MyNotes.Tests/EditorTests.cs. Review these
patches before updating upstream; do not replace this project with the unpatched
NuGet package without passing the persistence tests.
