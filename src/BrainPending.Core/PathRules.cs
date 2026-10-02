namespace BrainPending.Core;

public static class PathRules
{
    private static readonly StringComparison Comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer Comparer { get; } = StringComparer.FromComparison(Comparison);

    public static bool AreEqual(string? left, string? right) => Comparer.Equals(left, right);

    // Callers normalize paths first. Notebook-relative paths work here too.
    public static bool IsSameOrDescendant(string? path, string directory)
    {
        if (path == null) return false;
        var prefix = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
        return AreEqual(path, directory) || path.StartsWith(prefix, Comparison);
    }
}
