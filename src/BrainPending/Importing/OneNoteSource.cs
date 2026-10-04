using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Xml.Linq;

namespace BrainPending.Importing;

internal sealed record ImportCluster(string Id, string Name);
internal sealed record ImportPage(string Id, string Title, ImportCluster[] Clusters)
{
    public string Location => string.Join(" / ", Clusters.Select(f => f.Name));
    public override string ToString() => Title + " — " + Location;
}

internal interface IThoughtImportSource : IDisposable
{
    Task<IReadOnlyList<ImportPage>> GetPagesAsync(string? sourceFile, CancellationToken cancellation);
    Task<string> GetPageAsync(string id, CancellationToken cancellation);
    void IDisposable.Dispose() { }
}

internal sealed class OneNoteSource : IThoughtImportSource
{
    private readonly BlockingCollection<Action> _requests = new();
    private readonly object _lifetime = new();
    private Thread? _thread;
    private object? _instance;
    private bool _disposed;
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
                    .Select(a => new ImportCluster((string?)a.Attribute("ID") ?? throw new InvalidDataException("A OneNote section has no ID."),
                        (string?)a.Attribute("name") ?? "Untitled section")).ToArray())).ToArray();
    }

    private Task<T> ReadAsync<T>(Func<IOneNoteApplication, T> read, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("OneNote import requires the Windows desktop version of OneNote.");
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread == null)
            {
                _thread = new Thread(() =>
                {
                    try { foreach (var request in _requests.GetConsumingEnumerable()) request(); }
                    finally
                    {
                        if (OperatingSystem.IsWindows() && _instance != null && Marshal.IsComObject(_instance))
                            Marshal.FinalReleaseComObject(_instance);
                        _requests.Dispose();
                    }
                }) { IsBackground = true, Name = "Brain Pending OneNote reader" };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
            _requests.Add(() =>
            {
                try
                {
                    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                    cancellation.ThrowIfCancellationRequested();
                    var type = Type.GetTypeFromProgID("OneNote.Application")
                        ?? throw new InvalidOperationException("Install Microsoft OneNote for Windows, open your notebooks in it, then try again.");
                    _instance ??= Activator.CreateInstance(type) ?? throw new InvalidOperationException("Could not start OneNote.");
                    var result = read((IOneNoteApplication)_instance);
                    cancellation.ThrowIfCancellationRequested();
                    completion.TrySetResult(result);
                }
                catch (OperationCanceledException) { completion.TrySetCanceled(cancellation); }
                catch (COMException error) { completion.TrySetException(new IOException("OneNote could not read this content. Open it in OneNote, let it sync, and unlock any protected sections. " + error.Message, error)); }
                catch (Exception error) { completion.TrySetException(error); }
            });
        }
        // A running COM call cannot be interrupted; its result is discarded on cancellation.
        return completion.Task.WaitAsync(cancellation);
    }

    public void Dispose()
    {
        lock (_lifetime)
        {
            if (_disposed) return;
            _disposed = true;
            _requests.CompleteAdding();
            if (_thread == null) _requests.Dispose();
        }
    }
}
