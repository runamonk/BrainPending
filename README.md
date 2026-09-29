# Brain Pending

![Brain Pending icon](src/BrainPending/Assets/BrainPending.png)

**A place for thoughts still loading.**

A portable note-taking app built with C# / .NET 10 and Avalonia. No WPF, WinForms,
browser shell, or paid editor components.

## Run in VS Code

Install the .NET 10 SDK and Microsoft's C# extension (C# Dev Kit is recommended).
Open this folder, select **Brain Pending** in Run and Debug, and press **F5**.
The workspace recommends the optional Avalonia extension for XAML editing.

Or run from a terminal:

```powershell
dotnet run --project src/BrainPending -- --notes ./Notes
```

## What works

- Two primary panels, no tabs. Clusters and thoughts share the left panel. Clusters are folders on disk.
- Use the pin at the right end of the left panel's toolbar to auto-hide the panel;
  click the mini sidebar’s toggle to reveal it over the editor, or use Ctrl+Shift+F.
  Pin it again to keep it visible. The pin preference is remembered across launches.
- Nested folders, sliding folder navigation, an Up entry, and notebook-home button.
- Individual `.rtf` files in real folders.
- Unpin the sidebar to keep a narrow vertical toolbar. Click its top button
  to toggle the note list. Home and Pin stay below search in the expanded pane;
  its other actions appear there only while pinned.
- Formatting toolbar: fonts, size, bold/italic/underline/strike, colours,
  highlighting, links, bulleted and numbered lists, image insertion, and table insertion.
  Controls wrap onto another row when needed; there is no overflow menu.
- Image insertion, clipboard images, and resize handles. Click an image to select
  it; drag its corner to resize while retaining its proportions.
- **Attach file…** copies files into notebook storage and inserts attachment links.
  Follow a link to see its filename and size, **Open copy**, or **Save As…**.
  Opening a copy never changes the stored original.
- Insert/edit links from the toolbar; right-click an existing link to edit/remove
  it. Select text before inserting a link to use that text as the label.
- Autosave after a short typing pause; save on note switch and close.
- Search note/folder titles recursively in the current folder (Ctrl+Shift+F).
- Ctrl+F opens a slide-down find panel above the editor and focuses its input.
  Matches are highlighted as you type, with a match counter. F3 / Shift+F3
  (or Enter / Shift+Enter in the input) move between matches with wrap-around;
  Escape closes the panel and clears the highlights.
- Right-click entries to rename, move to parent, move to another notebook folder,
  or delete to recoverable trash. Shift+F10 opens the menu for a selected entry.
- Pin notes from their right-click menu to keep them above other entries. Pins
  are saved in the notebook and follow notes when renamed or moved in the app.
- Move to cluster uses an in-app cluster browser with Home, Up, and Move here;
  destinations stay inside the notebook.
- Trash is a protected notebook folder. Deleted notes and folders stay browsable
  and editable there; move them out to recover them. Deleting from Trash sends
  the item to the Windows Recycle Bin. Trash itself cannot be renamed, moved or deleted.
- Light/dark themes.
- Bottom status bar includes the current notebook as a split button: click the
  path to browse, or the arrow for the ten most recently opened notebooks,
  remembered between sessions. The color theme button sits alongside save status.
- Each recent notebook has a trashcan button to remove it from the menu without
  deleting its files or changing the currently open notebook.
- Dark mode uses the Dracula palette, including the editor, toolbar, dialogs,
  cyan folder icons, purple accents and muted selection backgrounds.
  Dark document ink is lightened for display without changing saved RTF colours.
- Main-window size, position and maximized state are remembered on close and
  restored within an available monitor.
- Each notebook remembers its last open note and restores its folder and selection.
  Missing or unreadable notes are skipped without preventing the notebook from opening.
- Double-click the open note's title to rename it inline. Enter saves the name;
  Escape or clicking away cancels the title edit.
- File watching plus a three-second fallback scan for external changes.

## Portable Windows build

```powershell
dotnet publish src/BrainPending -c Release -r win-x64 --self-contained true -o artifacts/BrainPending
```

Copy the **whole output folder** to your synced location. Run `BrainPending.exe`.
No .NET SDK or IDE is needed on the destination computer.

Notebook selection order: `--notes <folder>`, the last successfully opened notebook
on this computer, then an adjacent `Notes` folder (created on first use).
If automatic reopening fails or is interrupted, startup leaves the app ready for
you to choose a notebook instead of retrying on every launch. Opening a notebook
successfully enables automatic reopening again. A missing remembered folder is
never silently recreated. **Open notebook** can select any other writable folder.

Preferences are per computer, under `%LOCALAPPDATA%/MyNotes`. They are not shared
with the notes. Use `--notes` to open multiple independent notebooks/instances.

## Shared-folder behaviour

Your existing sync service transfers the files; Brain Pending reacts when they arrive.
“Saved locally” does not mean the sync service has uploaded a change.

- Clean open notes reload on an external change.
- If an edit is detected on disk before saving local work, the app creates a
  separate conflict copy and preserves the other version.
- Same-filesystem instances serialize their writes with a short exclusive lock.
  This lock is **not** a distributed lock between cloud replicas.
- Immutable RTF revisions are stored in `Notes/.mynotes/history`, with JSON files
  identifying their original paths. Deleted items live in `.mynotes/trash/items`
  and appear in the notebook's Trash folder. Earlier trash entries migrate into this view.
  Recover deleted items with Move to cluster or Move to parent; history recovery
  still requires copying an RTF from the history directory.

This is eventual file synchronization, not collaborative live editing. Offline
edits reaching the sync provider simultaneously can still produce provider-level
conflicts. History preserves local save revisions, but automated reconciliation
of those conflicts is not implemented. Do not exclude `.mynotes/history` from
sync if you want those revisions on another computer. History has no retention
limit yet; image-heavy notes can consume substantial space.

## Validation and current scope

```powershell
dotnet test BrainPending.slnx
```

Tests cover persistence, competing instances, external changes in an open window,
save-on-close and RTF round-trips for links, images, Unicode, fonts,
highlighting, lists and alignment. A headless Skia-rendered screenshot is written
to `artifacts/screenshots/editor.png` by the visual-review test.

This is the first working version. Full-notebook content search,
history restore screens, and automatic handling
of sync-provider conflict files are still to come. Moving files with Explorer
is supported through refresh; avoid moving a note while actively editing it.

The free editor implements a subset of RTF, not full Word compatibility. Complex
external RTF objects and advanced table/page formatting need further compatibility
testing. Original bytes are retained until the user edits, and previous revisions
are kept. Windows is the validated target; macOS/Linux have not been validated.

## App icon

The pink brain has a blank stare and three loading dots. Thinking is pending;
your notes are saved. The window and Windows executable use the same icon.
Regenerate the PNG and multi-size ICO (16–256 px) on Windows with:

```powershell
pwsh -File scripts/Generate-AppIcon.ps1
```

## Source layout

- `src/BrainPending`: Avalonia application and UI.
- `src/BrainPending.Core`: storage and conflict preservation.
- `tests/BrainPending.Tests`: persistence, editor, and window integration tests.
- `vendor/AvaloniaRichEditor`: pinned MIT editor source with documented RTF fixes.

See `THIRD-PARTY-NOTICES.md` and `vendor/AvaloniaRichEditor/BRAINPENDING-PATCHES.md`.

### Custom themes

Open the color-theme menu and choose **Edit themes…** for the built-in two-pane
editor: JSON on the left and a live theme preview on the right. Choose a theme
in the preview list, then edit its colors or copy an entry with a unique `Name`.
**Save themes** (Ctrl+S) validates, backs up, and applies the file; **Cancel** or
Escape discards edits. Invalid JSON keeps the last valid preview and disables
Save. Previewing does not change the app's active theme.

The file lives at `%LOCALAPPDATA%\MyNotes\themes.json` and is created on first
launch. External edits can still be applied with **Reload themes**; themes also
load on startup. `Dark` selects light/dark control styling; `Surface`, `Text`, `Accent`,
`Selection`, `Icon`, `Line`, and `Link` define the palette colors (for example,
`#FF79C6`). JSON comments and trailing commas are supported.

Invalid files show an error without replacing the current palette; startup uses
bundled defaults if loading fails. **Reset built-in themes** restores all supplied
themes, preserves custom entries, and saves a uniquely named `.bak` beside the
file. Correct invalid JSON before resetting so custom entries can be preserved.
The bundled defaults are maintained in `src/BrainPending/DefaultThemes.json`.

## Import from Microsoft OneNote

Choose **Import thoughts** in the sidebar, then **Load notebooks**. This first
import source requires the Windows desktop version of OneNote, with first-run
setup completed and the notebooks open and synced. **Open .one / .onetoc2…** asks
OneNote to open an existing section (`.one`) or notebook (`.onetoc2`). Selecting
`.onetoc2` loads pages from the notebook’s sections and section groups; keep the
accompanying `.one` files and subfolders in place. Brain Pending reads through OneNote,
rather than parsing these binary files directly.

Filter by notebook, section, or page name. Use **Select all shown**, or Ctrl/Shift
selection, and review a page preview. Choose a destination inside the current
Brain Pending notebook and click **Import selected**. Each run creates a separate
folder, preserving notebook/section-group/section structure and giving duplicate
titles unique filenames. Existing notes and source OneNote pages are not changed.

Text, basic formatting, links, lists, tables, and embedded supported images are
converted to RTF. Free-positioned content is arranged top-to-bottom, then
left-to-right. Attached files are copied from OneNote's cache into notebook storage
and linked from the imported note. Missing cached files are reported individually;
the rest of the page is still imported. Ink, media, and tags are not fully converted; the
preview and import report flag these limitations. Internal OneNote links still
open their original OneNote pages. Password-protected sections must be unlocked
in OneNote. Re-importing creates another copy rather than synchronizing notes.

Escape or **Stop import** cancels remaining pages and keeps completed notes.
The progress bar counts processed pages, including failures, and shows imported
and failed counts. Notebook discovery uses an indeterminate bar. Stopping leaves
the bar at the number of pages processed, rather than marking it complete.
A report note summarizes skipped pages and conversion warnings. A JSON record in
`.mynotes/imports` retains source page IDs and imported paths. OneNote calls run
off the UI thread; an in-flight OneNote call may finish after cancellation, but
its result is discarded. No remote or local-file image references are fetched
by the converter.

Attachment files live in `.mynotes/attachments` and use notebook-relative links,
so renaming/moving notes or moving the whole notebook preserves them. Duplicate
filenames are stored separately. Copy/back up the **whole notebook including
`.mynotes`** to preserve attachments; an individual RTF export contains links,
not embedded attachment bytes. Files are retained for undo, history, and conflict
copies even after their links are removed. Attachment previews and automatic
cleanup of unreferenced files are not implemented.

To run the optional live import test against the generated dummy notebook, set
`BRAINPENDING_TEST_ONENOTE` to its `.onetoc2` path before running `dotnet test`.
