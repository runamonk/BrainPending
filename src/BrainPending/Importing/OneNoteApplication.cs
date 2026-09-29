using System.Runtime.InteropServices;

namespace BrainPending.Importing;

// First eight slots of OneNote 2013's IApplicationCOM, verified against Microsoft's
// installed primary interop assembly. A typed dual interface avoids dynamic's
// GetTypeInfo call, which fails on some current desktop OneNote installations.
// Unused slots must retain their position; no source-writing methods are called.
[ComImport, Guid("452AC71A-B655-4967-A208-A4CC39DD7949"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IOneNoteApplication
{
    void GetHierarchy([MarshalAs(UnmanagedType.BStr)] string root, int scope,
        [MarshalAs(UnmanagedType.BStr)] out string xml, int schema);
    void UpdateHierarchy([MarshalAs(UnmanagedType.BStr)] string xml, int schema);
    void OpenHierarchy([MarshalAs(UnmanagedType.BStr)] string path, [MarshalAs(UnmanagedType.BStr)] string parent,
        [MarshalAs(UnmanagedType.BStr)] out string id, int createFileType);
    void DeleteHierarchy([MarshalAs(UnmanagedType.BStr)] string id, DateTime expectedModified, bool permanently);
    void CreateNewPage([MarshalAs(UnmanagedType.BStr)] string section,
        [MarshalAs(UnmanagedType.BStr)] out string id, int style);
    void CloseNotebook([MarshalAs(UnmanagedType.BStr)] string id, bool force);
    void GetHierarchyParent([MarshalAs(UnmanagedType.BStr)] string id, [MarshalAs(UnmanagedType.BStr)] out string parent);
    void GetPageContent([MarshalAs(UnmanagedType.BStr)] string id,
        [MarshalAs(UnmanagedType.BStr)] out string xml, int pageInfo, int schema);
}
