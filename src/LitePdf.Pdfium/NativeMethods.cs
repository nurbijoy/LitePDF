using System.Runtime.InteropServices;

namespace LitePdf.Pdfium;

internal static partial class NativeMethods
{
    private const string DllName = "pdfium.dll";

    // Library
    [LibraryImport(DllName, EntryPoint = "FPDF_InitLibrary")]
    internal static partial void FPDF_InitLibrary();

    [LibraryImport(DllName, EntryPoint = "FPDF_DestroyLibrary")]
    internal static partial void FPDF_DestroyLibrary();

    [LibraryImport(DllName, EntryPoint = "FPDF_GetLastError")]
    internal static partial uint FPDF_GetLastError();

    // Document
    [LibraryImport(DllName, EntryPoint = "FPDF_LoadCustomDocument", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr FPDF_LoadCustomDocument(ref FPDF_FILEACCESS access, string? password);

    [LibraryImport(DllName, EntryPoint = "FPDF_CloseDocument")]
    internal static partial void FPDF_CloseDocument(IntPtr doc);

    [LibraryImport(DllName, EntryPoint = "FPDF_GetPageCount")]
    internal static partial int FPDF_GetPageCount(IntPtr doc);

    [LibraryImport(DllName, EntryPoint = "FPDF_GetPageSizeByIndex")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDF_GetPageSizeByIndex(IntPtr doc, int pageIndex, out double width, out double height);

    [LibraryImport(DllName, EntryPoint = "FPDF_GetFileIdentifier")]
    internal static partial uint FPDF_GetFileIdentifier(IntPtr doc, int fileIdType, IntPtr buffer, uint buflen);

    // Page
    [LibraryImport(DllName, EntryPoint = "FPDF_LoadPage")]
    internal static partial IntPtr FPDF_LoadPage(IntPtr doc, int pageIndex);

    [LibraryImport(DllName, EntryPoint = "FPDF_ClosePage")]
    internal static partial void FPDF_ClosePage(IntPtr page);

    // Bitmap
    [LibraryImport(DllName, EntryPoint = "FPDFBitmap_Create")]
    internal static partial IntPtr FPDFBitmap_Create(int width, int height, int alpha);

    [LibraryImport(DllName, EntryPoint = "FPDFBitmap_GetBuffer")]
    internal static partial IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);

    [LibraryImport(DllName, EntryPoint = "FPDFBitmap_GetStride")]
    internal static partial int FPDFBitmap_GetStride(IntPtr bitmap);

    [LibraryImport(DllName, EntryPoint = "FPDFBitmap_FillRect")]
    internal static partial void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);

    [LibraryImport(DllName, EntryPoint = "FPDFBitmap_Destroy")]
    internal static partial void FPDFBitmap_Destroy(IntPtr bitmap);

    // Render
    [LibraryImport(DllName, EntryPoint = "FPDF_RenderPageBitmap")]
    internal static partial void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    // Text
    [LibraryImport(DllName, EntryPoint = "FPDFText_LoadPage")]
    internal static partial IntPtr FPDFText_LoadPage(IntPtr page);

    [LibraryImport(DllName, EntryPoint = "FPDFText_ClosePage")]
    internal static partial void FPDFText_ClosePage(IntPtr textPage);

    [LibraryImport(DllName, EntryPoint = "FPDFText_CountChars")]
    internal static partial int FPDFText_CountChars(IntPtr textPage);

    [LibraryImport(DllName, EntryPoint = "FPDFText_GetUnicode")]
    internal static partial uint FPDFText_GetUnicode(IntPtr textPage, int index);

    [LibraryImport(DllName, EntryPoint = "FPDFText_GetText")]
    internal static partial int FPDFText_GetText(IntPtr textPage, int start, int count, IntPtr result);

    [LibraryImport(DllName, EntryPoint = "FPDFText_GetCharBox")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFText_GetCharBox(IntPtr textPage, int index, out double left, out double right, out double bottom, out double top);

    [LibraryImport(DllName, EntryPoint = "FPDFText_GetCharIndexAtPos")]
    internal static partial int FPDFText_GetCharIndexAtPos(IntPtr textPage, double x, double y, double xTolerance, double yTolerance);

    [LibraryImport(DllName, EntryPoint = "FPDFText_CountRects")]
    internal static partial int FPDFText_CountRects(IntPtr textPage, int start, int count);

    [LibraryImport(DllName, EntryPoint = "FPDFText_GetRect")]
    internal static partial int FPDFText_GetRect(IntPtr textPage, int rectIndex, out double left, out double top, out double right, out double bottom);

    // Search
    [LibraryImport(DllName, EntryPoint = "FPDFText_FindStart")]
    internal static partial IntPtr FPDFText_FindStart(IntPtr textPage, IntPtr findWhat, int flags, int startIndex);

    [LibraryImport(DllName, EntryPoint = "FPDFText_FindNext")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFText_FindNext(IntPtr handle);

    [LibraryImport(DllName, EntryPoint = "FPDFText_FindPrev")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFText_FindPrev(IntPtr handle);

    [LibraryImport(DllName, EntryPoint = "FPDFText_GetSchResultIndex")]
    internal static partial int FPDFText_GetSchResultIndex(IntPtr handle);

    [LibraryImport(DllName, EntryPoint = "FPDFText_GetSchCount")]
    internal static partial int FPDFText_GetSchCount(IntPtr handle);

    [LibraryImport(DllName, EntryPoint = "FPDFText_FindClose")]
    internal static partial void FPDFText_FindClose(IntPtr handle);

    // Outline / Bookmark
    [LibraryImport(DllName, EntryPoint = "FPDFBookmark_GetFirstChild")]
    internal static partial IntPtr FPDFBookmark_GetFirstChild(IntPtr document, IntPtr bookmark);

    [LibraryImport(DllName, EntryPoint = "FPDFBookmark_GetNextSibling")]
    internal static partial IntPtr FPDFBookmark_GetNextSibling(IntPtr document, IntPtr bookmark);

    [LibraryImport(DllName, EntryPoint = "FPDFBookmark_GetTitle")]
    internal static partial uint FPDFBookmark_GetTitle(IntPtr bookmark, IntPtr buffer, uint buflen);

    [LibraryImport(DllName, EntryPoint = "FPDFBookmark_GetDest")]
    internal static partial IntPtr FPDFBookmark_GetDest(IntPtr document, IntPtr bookmark);

    [LibraryImport(DllName, EntryPoint = "FPDFBookmark_GetAction")]
    internal static partial IntPtr FPDFBookmark_GetAction(IntPtr bookmark);

    [LibraryImport(DllName, EntryPoint = "FPDFAction_GetDest")]
    internal static partial IntPtr FPDFAction_GetDest(IntPtr document, IntPtr action);

    [LibraryImport(DllName, EntryPoint = "FPDFDest_GetDestPageIndex")]
    internal static partial int FPDFDest_GetDestPageIndex(IntPtr document, IntPtr dest);

    // Links
    [LibraryImport(DllName, EntryPoint = "FPDFLink_Enumerate")]
    internal static partial IntPtr FPDFLink_Enumerate(IntPtr page, ref int startPos);

    [LibraryImport(DllName, EntryPoint = "FPDFLink_GetDest")]
    internal static partial IntPtr FPDFLink_GetDest(IntPtr document, IntPtr link);

    [LibraryImport(DllName, EntryPoint = "FPDFLink_GetAction")]
    internal static partial IntPtr FPDFLink_GetAction(IntPtr link);

    [LibraryImport(DllName, EntryPoint = "FPDFAction_GetType")]
    internal static partial uint FPDFAction_GetType(IntPtr action);

    [LibraryImport(DllName, EntryPoint = "FPDFAction_GetURIPath")]
    internal static partial uint FPDFAction_GetURIPath(IntPtr document, IntPtr action, IntPtr buffer, uint buflen);

    [LibraryImport(DllName, EntryPoint = "FPDFLink_GetAnnotRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFLink_GetAnnotRect(IntPtr link, out FS_RECTF rect);

    [LibraryImport(DllName, EntryPoint = "FPDFLink_GetLinkZOrderAtPoint")]
    internal static partial int FPDFLink_GetLinkAtPoint(IntPtr page, double x, double y);

    // Annotations
    [LibraryImport(DllName, EntryPoint = "FPDFPage_CreateAnnot")]
    internal static partial IntPtr FPDFPage_CreateAnnot(IntPtr page, int subtype);

    [LibraryImport(DllName, EntryPoint = "FPDFPage_GetAnnotCount")]
    internal static partial int FPDFPage_GetAnnotCount(IntPtr page);

    [LibraryImport(DllName, EntryPoint = "FPDFPage_GetAnnot")]
    internal static partial IntPtr FPDFPage_GetAnnot(IntPtr page, int index);

    [LibraryImport(DllName, EntryPoint = "FPDFPage_RemoveAnnot")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFPage_RemoveAnnot(IntPtr page, int index);

    [LibraryImport(DllName, EntryPoint = "FPDFPage_CloseAnnot")]
    internal static partial void FPDFPage_CloseAnnot(IntPtr annot);

    [LibraryImport(DllName, EntryPoint = "FPDFAnnot_AppendAttachmentPoints")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFAnnot_AppendAttachmentPoints(IntPtr annot, ref FS_QUADPOINTSF quad);

    [LibraryImport(DllName, EntryPoint = "FPDFAnnot_SetColor")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFAnnot_SetColor(IntPtr annot, int colorType, uint R, uint G, uint B, uint A);

    [LibraryImport(DllName, EntryPoint = "FPDFAnnot_SetRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFAnnot_SetRect(IntPtr annot, ref FS_RECTF rect);

    [LibraryImport(DllName, EntryPoint = "FPDFAnnot_GetColor")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFAnnot_GetColor(IntPtr annot, int colorType, out uint R, out uint G, out uint B, out uint A);

    [LibraryImport(DllName, EntryPoint = "FPDFAnnot_GetRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFAnnot_GetRect(IntPtr annot, out FS_RECTF rect);

    [LibraryImport(DllName, EntryPoint = "FPDFAnnot_GetAttachmentPoints")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFAnnot_GetAttachmentPoints(IntPtr annot, uint quadIndex, out FS_QUADPOINTSF quad);

    [LibraryImport(DllName, EntryPoint = "FPDFAnnot_CountAttachmentPoints")]
    internal static partial uint FPDFAnnot_CountAttachmentPoints(IntPtr annot);

    [LibraryImport(DllName, EntryPoint = "FPDFAnnot_GetSubtype")]
    internal static partial int FPDFAnnot_GetSubtype(IntPtr annot);

    [LibraryImport(DllName, EntryPoint = "FPDFAnnot_SetStringValue")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFAnnot_SetStringValue(IntPtr annot, [MarshalAs(UnmanagedType.LPWStr)] string key, IntPtr value);

    [LibraryImport(DllName, EntryPoint = "FPDFAnnot_GetStringValue")]
    internal static partial uint FPDFAnnot_GetStringValue(IntPtr annot, [MarshalAs(UnmanagedType.LPWStr)] string key, IntPtr buffer, uint buflen);

    [LibraryImport(DllName, EntryPoint = "FPDFAnnot_GetFormFieldName")]
    internal static partial uint FPDFAnnot_GetFormFieldName(IntPtr formHandle, IntPtr annot, IntPtr buffer, uint buflen);

    // Save
    [LibraryImport(DllName, EntryPoint = "FPDF_SaveAsCopy")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDF_SaveAsCopy(IntPtr document, ref FPDF_FILEWRITE fileWrite, uint flags);

    // Device / Page conversion
    [LibraryImport(DllName, EntryPoint = "FPDF_PageToDevice")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDF_PageToDevice(IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, double pageX, double pageY, out int deviceX, out int deviceY);

    [LibraryImport(DllName, EntryPoint = "FPDF_DeviceToPage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDF_DeviceToPage(IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int deviceX, int deviceY, out double pageX, out double pageY);

    // For searchable PDF (optional)
    [LibraryImport(DllName, EntryPoint = "FPDFPageObj_CreateTextObj")]
    internal static partial IntPtr FPDFPageObj_CreateTextObj(IntPtr document, IntPtr font, float fontSize);

    [LibraryImport(DllName, EntryPoint = "FPDFText_LoadStandardFont")]
    internal static partial IntPtr FPDFText_LoadStandardFont(IntPtr document, [MarshalAs(UnmanagedType.LPStr)] string fontName);

    [LibraryImport(DllName, EntryPoint = "FPDFText_SetText")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFText_SetText(IntPtr textObject, IntPtr text);

    [LibraryImport(DllName, EntryPoint = "FPDFTextObj_SetTextRenderMode")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFTextObj_SetTextRenderMode(IntPtr textObject, int renderMode);

    [LibraryImport(DllName, EntryPoint = "FPDFPageObj_Transform")]
    internal static partial void FPDFPageObj_Transform(IntPtr pageObject, double a, double b, double c, double d, double e, double f);

    [LibraryImport(DllName, EntryPoint = "FPDFPage_InsertObject")]
    internal static partial void FPDFPage_InsertObject(IntPtr page, IntPtr pageObject);

    [LibraryImport(DllName, EntryPoint = "FPDFPage_GenerateContent")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FPDFPage_GenerateContent(IntPtr page);

    // Constants
    internal const int FPDF_ANNOT_TEXT = 1;
    internal const int FPDF_ANNOT_HIGHLIGHT = 9;
    internal const int FPDF_ANNOT_UNDERLINE = 10;
    internal const int FPDF_ANNOT_STRIKEOUT = 12;

    internal const int FPDF_ANNOT_AP = 0;
    internal const int FPDF_ANNOT_NORMAL = 0;

    internal const int FPDF_ANNOT_COLORTYPE_Color = 0;

    internal const int FPDF_INCREMENTAL = 1;
    internal const int FPDF_NO_INCREMENTAL = 2;

    internal const uint FPDF_ACTION_TYPE_GOTO = 1;
    internal const uint FPDF_ACTION_TYPE_URI = 2;

    // Structs
    [StructLayout(LayoutKind.Sequential)]
    internal struct FPDF_FILEACCESS
    {
        public uint m_FileLen;
        public IntPtr m_GetBlock;
        public IntPtr m_Param;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FPDF_FILEWRITE
    {
        public int version;
        public IntPtr WriteBlock;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FS_RECTF
    {
        public float left;
        public float top;
        public float right;
        public float bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FS_QUADPOINTSF
    {
        public float x1;
        public float y1;
        public float x2;
        public float y2;
        public float x3;
        public float y3;
        public float x4;
        public float y4;
    }

    // Delegates for callbacks
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int GetBlockDelegate(IntPtr param, uint position, IntPtr pBuf, uint size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int WriteBlockDelegate(ref FPDF_FILEWRITE pThis, IntPtr pData, uint size);
}
