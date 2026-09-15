using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LitePdf.Pdfium.Interop;

// Signatures follow PDFium's public headers (fpdfview.h, fpdf_text.h, fpdf_doc.h, fpdf_annot.h, fpdf_edit.h, fpdf_save.h).
// Windows ABI notes: `unsigned long` is 32-bit (uint), FPDF_BOOL is int, FPDF_WIDESTRING is UTF-16LE (char*),
// FPDF_BYTESTRING is a NUL-terminated byte string (byte*), size_t is nuint.

[StructLayout(LayoutKind.Sequential)]
internal struct FS_RECTF
{
    public float Left;
    public float Top;
    public float Right;
    public float Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FS_QUADPOINTSF
{
    public float X1, Y1, X2, Y2, X3, Y3, X4, Y4;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FPDF_FILEACCESS
{
    public uint FileLen;
    public delegate* unmanaged[Cdecl]<nint, uint, byte*, uint, int> GetBlock;
    public nint Param;
}

/// <summary>FPDF_FILEWRITE followed by our own context field; PDFium passes the struct pointer back to WriteBlock.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FileWriteContext
{
    public int Version;
    public delegate* unmanaged[Cdecl]<FileWriteContext*, byte*, uint, int> WriteBlock;
    public nint Handle;
}

internal static unsafe partial class NativeMethods
{
    private const string Lib = "pdfium";

    public const int FPDF_ANNOT = 0x01;
    public const int FPDF_INCREMENTAL = 1;
    public const int FPDF_NO_INCREMENTAL = 2;

    public const uint PDFACTION_GOTO = 1;
    public const uint PDFACTION_URI = 3;

    public const int FPDF_PAGEOBJ_PATH = 2;
    public const int FPDF_PAGEOBJ_IMAGE = 3;
    public const int FPDF_PAGEOBJ_FORM = 5;

    public const int FPDF_ANNOT_TEXT = 1;
    public const int FPDF_ANNOT_LINK = 2;
    public const int FPDF_ANNOT_HIGHLIGHT = 9;
    public const int FPDF_ANNOT_UNDERLINE = 10;
    public const int FPDF_ANNOT_SQUIGGLY = 11;
    public const int FPDF_ANNOT_STRIKEOUT = 12;
    public const int FPDF_ANNOT_WIDGET = 20;

    public const int FPDFANNOT_COLORTYPE_Color = 0;
    public const int FPDF_ANNOT_APPEARANCEMODE_NORMAL = 0;
    public const int FPDF_ANNOT_FLAG_PRINT = 1 << 2;

    [LibraryImport(Lib)] public static partial void FPDF_InitLibrary();
    [LibraryImport(Lib)] public static partial uint FPDF_GetLastError();

    // Document
    [LibraryImport(Lib)] public static partial nint FPDF_LoadCustomDocument(FPDF_FILEACCESS* fileAccess, byte* password);
    [LibraryImport(Lib)] public static partial void FPDF_CloseDocument(nint document);
    [LibraryImport(Lib)] public static partial int FPDF_GetPageCount(nint document);
    [LibraryImport(Lib)] public static partial int FPDF_GetPageSizeByIndex(nint document, int pageIndex, double* width, double* height);
    [LibraryImport(Lib)] public static partial int FPDF_GetFileVersion(nint document, int* fileVersion);
    [LibraryImport(Lib)] public static partial uint FPDF_GetMetaText(nint document, byte* tag, char* buffer, uint bufLen);
    [LibraryImport(Lib)] public static partial int FPDF_GetSecurityHandlerRevision(nint document);
    [LibraryImport(Lib)] public static partial uint FPDF_GetFileIdentifier(nint document, int idType, byte* buffer, uint bufLen);
    [LibraryImport(Lib)] public static partial int FPDF_SaveAsCopy(nint document, FileWriteContext* fileWrite, uint flags);

    // Page
    [LibraryImport(Lib)] public static partial nint FPDF_LoadPage(nint document, int pageIndex);
    [LibraryImport(Lib)] public static partial void FPDF_ClosePage(nint page);
    [LibraryImport(Lib)] public static partial int FPDFPage_GetRotation(nint page);
    [LibraryImport(Lib)] public static partial int FPDF_GetPageBoundingBox(nint page, FS_RECTF* rect);
    [LibraryImport(Lib)] public static partial int FPDFPage_CountObjects(nint page);
    [LibraryImport(Lib)] public static partial nint FPDFPage_GetObject(nint page, int index);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetType(nint pageObject);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetFillColor(nint pageObject, uint* r, uint* g, uint* b, uint* a);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetStrokeColor(nint pageObject, uint* r, uint* g, uint* b, uint* a);
    [LibraryImport(Lib)] public static partial int FPDFFormObj_CountObjects(nint formObject);
    [LibraryImport(Lib)] public static partial nint FPDFFormObj_GetObject(nint formObject, uint index);
    [LibraryImport(Lib)] public static partial int FPDF_PageToDevice(nint page, int startX, int startY, int sizeX, int sizeY, int rotate, double pageX, double pageY, int* deviceX, int* deviceY);

    // Bitmap / render
    [LibraryImport(Lib)] public static partial nint FPDFBitmap_Create(int width, int height, int alpha);
    [LibraryImport(Lib)] public static partial int FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);
    [LibraryImport(Lib)] public static partial nint FPDFBitmap_GetBuffer(nint bitmap);
    [LibraryImport(Lib)] public static partial int FPDFBitmap_GetStride(nint bitmap);
    [LibraryImport(Lib)] public static partial void FPDFBitmap_Destroy(nint bitmap);
    [LibraryImport(Lib)] public static partial void FPDF_RenderPageBitmap(nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    // Text
    [LibraryImport(Lib)] public static partial nint FPDFText_LoadPage(nint page);
    [LibraryImport(Lib)] public static partial void FPDFText_ClosePage(nint textPage);
    [LibraryImport(Lib)] public static partial int FPDFText_CountChars(nint textPage);
    [LibraryImport(Lib)] public static partial uint FPDFText_GetUnicode(nint textPage, int index);
    [LibraryImport(Lib)] public static partial int FPDFText_IsGenerated(nint textPage, int index);
    [LibraryImport(Lib)] public static partial int FPDFText_GetLooseCharBox(nint textPage, int index, FS_RECTF* rect);
    [LibraryImport(Lib)] public static partial int FPDFText_GetCharBox(nint textPage, int index, double* left, double* right, double* bottom, double* top);

    // Outline, destinations, actions
    [LibraryImport(Lib)] public static partial nint FPDFBookmark_GetFirstChild(nint document, nint bookmark);
    [LibraryImport(Lib)] public static partial nint FPDFBookmark_GetNextSibling(nint document, nint bookmark);
    [LibraryImport(Lib)] public static partial uint FPDFBookmark_GetTitle(nint bookmark, char* buffer, uint bufLen);
    [LibraryImport(Lib)] public static partial int FPDFBookmark_GetCount(nint bookmark);
    [LibraryImport(Lib)] public static partial nint FPDFBookmark_GetDest(nint document, nint bookmark);
    [LibraryImport(Lib)] public static partial nint FPDFBookmark_GetAction(nint bookmark);
    [LibraryImport(Lib)] public static partial uint FPDFAction_GetType(nint action);
    [LibraryImport(Lib)] public static partial nint FPDFAction_GetDest(nint document, nint action);
    [LibraryImport(Lib)] public static partial uint FPDFAction_GetURIPath(nint document, nint action, byte* buffer, uint bufLen);
    [LibraryImport(Lib)] public static partial int FPDFDest_GetDestPageIndex(nint document, nint dest);
    [LibraryImport(Lib)] public static partial int FPDFDest_GetLocationInPage(nint dest, int* hasX, int* hasY, int* hasZoom, float* x, float* y, float* zoom);

    // Links
    [LibraryImport(Lib)] public static partial int FPDFLink_Enumerate(nint page, int* startPos, nint* linkAnnot);
    [LibraryImport(Lib)] public static partial nint FPDFLink_GetDest(nint document, nint link);
    [LibraryImport(Lib)] public static partial nint FPDFLink_GetAction(nint link);
    [LibraryImport(Lib)] public static partial int FPDFLink_GetAnnotRect(nint link, FS_RECTF* rect);

    // Annotations
    [LibraryImport(Lib)] public static partial int FPDFPage_GetAnnotCount(nint page);
    [LibraryImport(Lib)] public static partial nint FPDFPage_GetAnnot(nint page, int index);
    [LibraryImport(Lib)] public static partial nint FPDFPage_CreateAnnot(nint page, int subtype);
    [LibraryImport(Lib)] public static partial int FPDFPage_RemoveAnnot(nint page, int index);
    [LibraryImport(Lib)] public static partial void FPDFPage_CloseAnnot(nint annot);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetSubtype(nint annot);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetRect(nint annot, FS_RECTF* rect);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetRect(nint annot, FS_RECTF* rect);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetColor(nint annot, int type, uint* r, uint* g, uint* b, uint* a);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetColor(nint annot, int type, uint r, uint g, uint b, uint a);
    [LibraryImport(Lib)] public static partial nuint FPDFAnnot_CountAttachmentPoints(nint annot);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetAttachmentPoints(nint annot, nuint quadIndex, FS_QUADPOINTSF* quad);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_AppendAttachmentPoints(nint annot, FS_QUADPOINTSF* quad);
    [LibraryImport(Lib)] public static partial uint FPDFAnnot_GetStringValue(nint annot, byte* key, char* buffer, uint bufLen);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetStringValue(nint annot, byte* key, char* value);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetAP(nint annot, int appearanceMode, char* value);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetFlags(nint annot, int flags);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetObjectCount(nint annot);
    [LibraryImport(Lib)] public static partial nint FPDFAnnot_GetObject(nint annot, int index);
}
