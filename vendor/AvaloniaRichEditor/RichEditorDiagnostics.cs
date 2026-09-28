using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace AvaloniaRichEditor;

public sealed class RichEditorFaultEventArgs : EventArgs
{
    internal RichEditorFaultEventArgs(Exception exception, string member, string file, int line)
    {
        Exception = exception;
        Member = member;
        File = file;
        Line = line;
    }

    public Exception Exception { get; }

    public string Member { get; }

    /// <summary>Source file name (no directory).</summary>
    public string File { get; }

    /// <summary>Line number of the reporting <c>catch</c>.</summary>
    public int Line { get; }

    /// <inheritdoc/>
    public override string ToString()
        => $"{File}:{Line} {Member} — {Exception.GetType().Name}: {Exception.Message}";
}

/// <summary>Reports handled faults once per catch site and exception type; Reset re-arms reporting.
/// Handlers cannot change the fallback and their exceptions are ignored. Events may run off the UI
/// thread. Unsubscribe from this static event when the subscriber is no longer needed.</summary>
public static class RichEditorDiagnostics
{
    private static readonly HashSet<(string File, int Line, Type Type)> Seen = new();
    private static readonly object Gate = new();

    /// <summary>Raised the first time each distinct internal fault occurs. See
    /// <see cref="RichEditorDiagnostics"/> for threading and lifetime.</summary>
    public static event EventHandler<RichEditorFaultEventArgs>? Fault;

    /// <summary>Forgets which faults have already been reported, so they raise
    /// <see cref="Fault"/> again. Use it to scope reporting to one operation.</summary>
    public static void Reset()
    {
        lock (Gate) Seen.Clear();
    }

    // Compiler-supplied locations keep unsubscribed reporting cheap enough for render paths.
    internal static void Report(
        Exception ex,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        var handler = Fault; // snapshot: unsubscribe may race with the raise below
        if (handler == null) return;

        string name = FileName(file);
        lock (Gate)
        {
            if (!Seen.Add((name, line, ex.GetType()))) return;
        }

        // A diagnostic handler must not undo the recovery by throwing its own exception.
        try { handler(null, new RichEditorFaultEventArgs(ex, member, name, line)); }
        catch { }
    }

    private static string FileName(string path)
    {
        int i = path.LastIndexOfAny(new[] { '\\', '/' });
        return i < 0 ? path : path[(i + 1)..];
    }
}
