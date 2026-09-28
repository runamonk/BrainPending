using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace MyNotes.Importing;

internal sealed record ImportFolder(string Id, string Name);
internal sealed record ImportPage(string Id, string Title, ImportFolder[] Folders)
{
    public string Location => string.Join(" / ", Folders.Select(f => f.Name));
    public override string ToString() => Title + " — " + Location;
}

internal interface INoteImportSource
{
    Task<IReadOnlyList<ImportPage>> GetPagesAsync(string? sourceFile, CancellationToken cancellation);
    Task<string> GetPageAsync(string id, CancellationToken cancellation);
}

internal sealed class OneNoteSource : INoteImportSource
{
    public Task<IReadOnlyList<ImportPage>> GetPagesAsync(string? sourceFile, CancellationToken cancellation) =>
        ReadAsync<IReadOnlyList<ImportPage>>(app =>
        {
            string root = "";
            if (sourceFile != null) app.OpenHierarchy(GetHierarchyPath(sourceFile), "", out root, 0);
            string xml;
            app.GetHierarchy(root, 4, out xml, 2); // hsPages, xs2013
            return ParseHierarchy(xml);
        }, cancellation);

    internal static string GetHierarchyPath(string sourceFile)
    {
        var path = Path.GetFullPath(sourceFile);
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".one", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".onetoc2", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a OneNote section (.one) or notebook (.onetoc2).");
        if (!File.Exists(path)) throw new FileNotFoundException("The selected OneNote file could not be found.", path);
        // OpenHierarchy opens notebooks by folder, not by their table-of-contents file.
        return extension.Equals(".onetoc2", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(path)! : path;
    }

    public Task<string> GetPageAsync(string id, CancellationToken cancellation) => ReadAsync<string>(app =>
    {
        string xml;
        app.GetPageContent(id, out xml, 5, 2); // Binary data and file types, xs2013
        return xml;
    }, cancellation);

    internal static IReadOnlyList<ImportPage> ParseHierarchy(string xml)
    {
        var document = XDocument.Parse(xml);
        return document.Descendants().Where(e => e.Name.LocalName == "Page"
            && !e.AncestorsAndSelf().Any(a => (string?)a.Attribute("isInRecycleBin") == "true"))
            .Select(e => new ImportPage((string?)e.Attribute("ID") ?? throw new InvalidDataException("A OneNote page has no ID."),
                (string?)e.Attribute("name") ?? "Untitled page",
                e.Ancestors().Reverse().Where(a => a.Name.LocalName is "Notebook" or "SectionGroup" or "Section")
                    .Select(a => new ImportFolder((string?)a.Attribute("ID") ?? throw new InvalidDataException("A OneNote section has no ID."),
                        (string?)a.Attribute("name") ?? "Untitled section")).ToArray())).ToArray();
    }

    private static Task<T> ReadAsync<T>(Func<IOneNoteApplication, T> read, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("OneNote import requires the Windows desktop version of OneNote.");
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            object? instance = null;
            try
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                cancellation.ThrowIfCancellationRequested();
                var type = Type.GetTypeFromProgID("OneNote.Application")
                    ?? throw new InvalidOperationException("Install Microsoft OneNote for Windows, open your notebooks in it, then try again.");
                instance = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Could not start OneNote.");
                var result = read((IOneNoteApplication)instance);
                cancellation.ThrowIfCancellationRequested();
                completion.TrySetResult(result);
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(cancellation); }
            catch (COMException error) { completion.TrySetException(new IOException("OneNote could not read this content. Open it in OneNote, let it sync, and unlock any protected sections. " + error.Message, error)); }
            catch (Exception error) { completion.TrySetException(error); }
            finally
            {
                if (OperatingSystem.IsWindows() && instance != null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
            }
        }) { IsBackground = true, Name = "MyNotes OneNote reader" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // A running COM call cannot be interrupted; its result is discarded on cancellation.
        return completion.Task.WaitAsync(cancellation);
    }
}
