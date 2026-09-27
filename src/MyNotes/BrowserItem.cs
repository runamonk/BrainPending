namespace MyNotes;

public sealed record BrowserItem(string Path, string Name, bool IsFolder, bool IsUp, string Detail, bool IsTrash = false, bool IsPinned = false)
{
    public string IconData => IsUp
        ? "M 10,17 L 10,3 M 4,9 L 10,3 L 16,9"
        : IsTrash ? "M 3,5 L 17,5 M 7,5 L 7,2 L 13,2 L 13,5 M 5,5 L 6,18 L 14,18 L 15,5 M 8,8 L 8,15 M 12,8 L 12,15"
        : IsFolder
            ? "M 2,17 L 2,4 L 8,4 L 10,7 L 18,7 L 18,17 Z"
            : "M 4,2 L 12,2 L 16,6 L 16,18 L 4,18 Z M 12,2 L 12,6 L 16,6 M 7,10 L 13,10 M 7,14 L 13,14";
    public bool CanManage => !IsUp && !IsTrash;
}
