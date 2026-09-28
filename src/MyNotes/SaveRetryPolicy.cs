namespace MyNotes;

internal static class SaveRetryPolicy
{
    public static readonly TimeSpan[] Delays =
    [TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(700), TimeSpan.FromMilliseconds(1500)];

    public static bool IsTemporary(Exception error) => error is IOException
        && (error.HResult & 0xffff) is 32 or 33; // Sharing or lock violation.
}
