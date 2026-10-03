namespace BrainPending;

public sealed record BrowserItem(string Path, string Name, bool IsFolder, bool IsUp, string Detail, bool IsTrash = false, bool IsPinned = false)
{
    public string IconData => IsUp
        ? "M 10,17 L 10,3 M 4,9 L 10,3 L 16,9"
        : IsTrash ? "M 3,5 L 17,5 M 7,5 L 7,2 L 13,2 L 13,5 M 5,5 L 6,18 L 14,18 L 15,5 M 8,8 L 8,15 M 12,8 L 12,15"
        : IsFolder
            ? "M 8,8 L 5.5,5.5 M 12,8 L 14.5,5.5 M 8,12 L 5.5,14.5 M 12,12 L 14.5,14.5 M 13,10 A 3,3 0 1 0 7,10 A 3,3 0 1 0 13,10 M 6,4 A 2,2 0 1 0 2,4 A 2,2 0 1 0 6,4 M 18,4 A 2,2 0 1 0 14,4 A 2,2 0 1 0 18,4 M 6,16 A 2,2 0 1 0 2,16 A 2,2 0 1 0 6,16 M 18,16 A 2,2 0 1 0 14,16 A 2,2 0 1 0 18,16"
            : "M 5,13 C 2,13 1,11 1,9 C 1,7 2,5 5,5 C 5,1 11,1 12,4 C 16,2 19,5 19,8 C 19,11 17,13 14,13 Z M 8,15.5 A 1.5,1.5 0 1 0 5,15.5 A 1.5,1.5 0 1 0 8,15.5 M 3,18.5 A 0.5,0.5 0 1 0 2,18.5 A 0.5,0.5 0 1 0 3,18.5";
    public bool CanManage => !IsUp && !IsTrash;
}
