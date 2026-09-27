# MyNotes

A portable note-taking app built with C# / .NET 10 and Avalonia. No WPF, WinForms,
browser shell, or paid editor components.

## Run in VS Code

Install the .NET 10 SDK and Microsoft's C# extension (C# Dev Kit is recommended).
Open this folder, select **MyNotes** in Run and Debug, and press **F5**.
The workspace recommends the optional Avalonia extension for XAML editing.

Or run from a terminal:

```powershell
dotnet run --project src/MyNotes -- --notes ./Notes
```

## What works

- Two primary panels, no tabs. Folders and notes share the left panel.
- Nested folders, sliding folder navigation, an Up entry, and notebook-home button.
- Individual `.rtf` files in real folders.
- Formatting toolbar: fonts, size, bold/italic/underline/strike, colours,
  highlighting, headings, alignment, lists, indentation, and line spacing.
- Image insertion, clipboard images, and resize handles. Click an image to select
  it; drag its corner to resize while retaining its proportions.
- Insert/edit links from the toolbar; right-click an existing link to edit/remove
  it. Select text before inserting a link to use that text as the label.
- Autosave after a short typing pause; save on note switch and close.
- Search note/folder titles recursively in the current folder. Ctrl+F / Ctrl+H
  search or replace text within the open document.
- Rename, recoverable trash, light/dark themes, and ZuulSnips import.
- Dark mode uses a consistent charcoal surface, including the editor and toolbar.
  Dark document ink is lightened for display without changing saved RTF colours.
- Main-window position is remembered on close and restored within an available monitor.
- File watching plus a three-second fallback scan for external changes.

## Portable Windows build

```powershell
dotnet publish src/MyNotes -c Release -r win-x64 --self-contained true -o artifacts/MyNotes
```

Copy the **whole output folder** to your synced location. Run `MyNotes.exe`.
No .NET SDK or IDE is needed on the destination computer.

Notebook selection order: `--notes <folder>`, an existing `Notes` folder beside
the executable, the last folder selected on this computer, then a new adjacent
`Notes` folder. Keep an adjacent `Notes` folder for a self-contained portable
notebook. **Open notebook** can select any other writable folder.

Preferences are per computer, under `%LOCALAPPDATA%/MyNotes`. They are not shared
with the notes. Use `--notes` to open multiple independent notebooks/instances.

## Shared-folder behaviour

Your existing sync service transfers the files; MyNotes reacts when they arrive.
“Saved locally” does not mean the sync service has uploaded a change.

- Clean open notes reload on an external change.
- If an edit is detected on disk before saving local work, the app creates a
  separate conflict copy and preserves the other version.
- Same-filesystem instances serialize their writes with a short exclusive lock.
  This lock is **not** a distributed lock between cloud replicas.
- Immutable RTF revisions are stored in `Notes/.mynotes/history`, with JSON files
  identifying their original paths. Deleted items live in `.mynotes/trash`.
  Recovery currently means copying an RTF/folder back from these directories.

This is eventual file synchronization, not collaborative live editing. Offline
edits reaching the sync provider simultaneously can still produce provider-level
conflicts. History preserves local save revisions, but automated reconciliation
of those conflicts is not implemented. Do not exclude `.mynotes/history` from
sync if you want those revisions on another computer. History has no retention
limit yet; image-heavy notes can consume substantial space.

## Import ZuulSnips

Click **Import ZuulSnips** and select the data folder containing `.snips.json`
and optionally `.folders.json`. The importer creates a new dated folder and
copies notes into RTF files. It never writes to the original data. Duplicate
titles receive numbered suffixes; text-only notes are converted to RTF.

## Validation and current scope

```powershell
dotnet test MyNotes.slnx
```

Tests cover persistence, competing instances, external changes in an open window,
save-on-close, import, and RTF round-trips for links, images, Unicode, fonts,
highlighting, lists and alignment. A headless Skia-rendered screenshot is written
to `artifacts/screenshots/editor.png` by the visual-review test.

This is the first working version. Full-notebook content search, moving notes
between folders in the UI, history/trash restore screens, and automatic handling
of sync-provider conflict files are still to come. Moving files with Explorer
is supported through refresh; avoid moving a note while actively editing it.

The free editor implements a subset of RTF, not full Word compatibility. Complex
external RTF objects and advanced table/page formatting need further compatibility
testing. Original bytes are retained until the user edits, and previous revisions
are kept. Windows is the validated target; macOS/Linux have not been validated.

## Source layout

- `src/MyNotes`: Avalonia application and UI.
- `src/MyNotes.Core`: storage, conflict preservation, and ZuulSnips import.
- `tests/MyNotes.Tests`: persistence, editor, and window integration tests.
- `vendor/AvaloniaRichEditor`: pinned MIT editor source with documented RTF fixes.

See `THIRD-PARTY-NOTICES.md` and `vendor/AvaloniaRichEditor/MYNOTES-PATCHES.md`.
