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

/// <summary>PDF transformation matrix [a b c d e f].</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FS_MATRIX
{
    public float A, B, C, D, E, F;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FPDF_IMAGEOBJ_METADATA
{
    public uint Width;
    public uint Height;
    public float HorizontalDpi;
    public float VerticalDpi;
    public uint BitsPerPixel;
    public int Colorspace;
    public int MarkedContentId;
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

    public const int FPDF_PAGEOBJ_TEXT = 1;
    public const int FPDF_PAGEOBJ_PATH = 2;
    public const int FPDF_PAGEOBJ_IMAGE = 3;
    public const int FPDF_PAGEOBJ_SHADING = 4;
    public const int FPDF_PAGEOBJ_FORM = 5;

    /// <summary>Text drawn with render mode 3 is invisible: the OCR layer under a scanned page.</summary>
    public const int FPDF_TEXTRENDERMODE_INVISIBLE = 3;

    // PDF font descriptor /Flags bits (PDF 32000-1 table 123).
    public const int FPDF_FONTFLAG_FIXEDPITCH = 1 << 0;
    public const int FPDF_FONTFLAG_SERIF = 1 << 1;
    public const int FPDF_FONTFLAG_ITALIC = 1 << 6;
    public const int FPDF_FONTFLAG_FORCEBOLD = 1 << 18;

    public const int FPDFBitmap_Gray = 1;
    public const int FPDFBitmap_BGR = 2;
    public const int FPDFBitmap_BGRx = 3;
    public const int FPDFBitmap_BGRA = 4;

    public const int FPDF_FILLMODE_NONE = 0;

    // Path segment types (fpdf_edit.h).
    public const int FPDF_SEGMENT_UNKNOWN = -1;
    public const int FPDF_SEGMENT_LINETO = 0;
    public const int FPDF_SEGMENT_BEZIERTO = 1;
    public const int FPDF_SEGMENT_MOVETO = 2;

    /// <summary>Value types a structure element attribute can hold (fpdf_structtree.h).</summary>
    public const int FPDF_OBJECT_NUMBER = 2;
    public const int FPDF_OBJECT_STRING = 3;
    public const int FPDF_OBJECT_NAME = 4;

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

    // Text appearance (fpdf_text.h, fpdf_edit.h). Used by the DOCX export to recover character styling.
    [LibraryImport(Lib)] public static partial double FPDFText_GetFontSize(nint textPage, int index);
    [LibraryImport(Lib)] public static partial uint FPDFText_GetFontInfo(nint textPage, int index, byte* buffer, uint bufLen, int* flags);
    [LibraryImport(Lib)] public static partial int FPDFText_GetFontWeight(nint textPage, int index);
    [LibraryImport(Lib)] public static partial int FPDFText_GetFillColor(nint textPage, int index, uint* r, uint* g, uint* b, uint* a);
    [LibraryImport(Lib)] public static partial float FPDFText_GetCharAngle(nint textPage, int index);
    [LibraryImport(Lib)] public static partial nint FPDFText_GetTextObject(nint textPage, int index);
    [LibraryImport(Lib)] public static partial int FPDFText_GetMatrix(nint textPage, int index, FS_MATRIX* matrix);
    [LibraryImport(Lib)] public static partial nint FPDFTextObj_GetFont(nint textObject);
    [LibraryImport(Lib)] public static partial int FPDFTextObj_GetTextRenderMode(nint textObject);
    [LibraryImport(Lib)] public static partial int FPDFFont_GetFlags(nint font);
    [LibraryImport(Lib)] public static partial int FPDFFont_GetWeight(nint font);
    [LibraryImport(Lib)] public static partial int FPDFFont_GetItalicAngle(nint font, int* angle);
    [LibraryImport(Lib)] public static partial uint FPDFFont_GetBaseFontName(nint font, byte* buffer, uint length);
    [LibraryImport(Lib)] public static partial uint FPDFFont_GetFamilyName(nint font, byte* buffer, uint length);

    // Page objects (fpdf_edit.h)
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetBounds(nint pageObject, float* left, float* bottom, float* right, float* top);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetMatrix(nint pageObject, FS_MATRIX* matrix);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_SetIsActive(nint pageObject, int active);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetStrokeWidth(nint pageObject, float* width);

    // Images (fpdf_edit.h)
    [LibraryImport(Lib)] public static partial int FPDFImageObj_GetImageFilterCount(nint imageObject);
    [LibraryImport(Lib)] public static partial uint FPDFImageObj_GetImageFilter(nint imageObject, int index, byte* buffer, uint bufLen);
    [LibraryImport(Lib)] public static partial uint FPDFImageObj_GetImageDataDecoded(nint imageObject, byte* buffer, uint bufLen);
    [LibraryImport(Lib)] public static partial int FPDFImageObj_GetImagePixelSize(nint imageObject, uint* width, uint* height);
    [LibraryImport(Lib)] public static partial int FPDFImageObj_GetImageMetadata(nint imageObject, nint page, FPDF_IMAGEOBJ_METADATA* metadata);
    [LibraryImport(Lib)] public static partial nint FPDFImageObj_GetRenderedBitmap(nint document, nint page, nint imageObject);

    // Paths (fpdf_edit.h)
    [LibraryImport(Lib)] public static partial int FPDFPath_CountSegments(nint path);
    [LibraryImport(Lib)] public static partial nint FPDFPath_GetPathSegment(nint path, int index);
    [LibraryImport(Lib)] public static partial int FPDFPathSegment_GetPoint(nint segment, float* x, float* y);
    [LibraryImport(Lib)] public static partial int FPDFPathSegment_GetType(nint segment);
    [LibraryImport(Lib)] public static partial int FPDFPath_GetDrawMode(nint path, int* fillMode, int* stroke);

    // Marked content, which is what ties a page object to an element of the structure tree.
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetMarkedContentID(nint pageObject);

    // Embedded font programs (fpdf_edit.h), for the optional font embedding.
    [LibraryImport(Lib)] public static partial int FPDFFont_GetIsEmbedded(nint font);
    [LibraryImport(Lib)] public static partial int FPDFFont_GetFontData(nint font, byte* buffer, nuint bufLen, nuint* outBufLen);

    // Structure tree (fpdf_structtree.h): the answer a tagged PDF already carries.
    [LibraryImport(Lib)] public static partial nint FPDF_StructTree_GetForPage(nint page);
    [LibraryImport(Lib)] public static partial void FPDF_StructTree_Close(nint structTree);
    [LibraryImport(Lib)] public static partial int FPDF_StructTree_CountChildren(nint structTree);
    [LibraryImport(Lib)] public static partial nint FPDF_StructTree_GetChildAtIndex(nint structTree, int index);
    [LibraryImport(Lib)] public static partial int FPDF_StructElement_CountChildren(nint element);
    [LibraryImport(Lib)] public static partial nint FPDF_StructElement_GetChildAtIndex(nint element, int index);
    [LibraryImport(Lib)] public static partial int FPDF_StructElement_GetChildMarkedContentID(nint element, int index);
    [LibraryImport(Lib)] public static partial uint FPDF_StructElement_GetType(nint element, char* buffer, uint bufLen);
    [LibraryImport(Lib)] public static partial uint FPDF_StructElement_GetAltText(nint element, char* buffer, uint bufLen);
    [LibraryImport(Lib)] public static partial uint FPDF_StructElement_GetTitle(nint element, char* buffer, uint bufLen);
    [LibraryImport(Lib)] public static partial int FPDF_StructElement_GetMarkedContentIdCount(nint element);
    [LibraryImport(Lib)] public static partial int FPDF_StructElement_GetMarkedContentIdAtIndex(nint element, int index);
    [LibraryImport(Lib)] public static partial int FPDF_StructElement_GetAttributeCount(nint element);
    [LibraryImport(Lib)] public static partial nint FPDF_StructElement_GetAttributeAtIndex(nint element, int index);
    [LibraryImport(Lib)] public static partial int FPDF_StructElement_Attr_GetCount(nint attr);
    [LibraryImport(Lib)] public static partial int FPDF_StructElement_Attr_GetName(nint attr, int index, byte* buffer, uint bufLen, uint* outBufLen);
    [LibraryImport(Lib)] public static partial nint FPDF_StructElement_Attr_GetValue(nint attr, byte* name);
    [LibraryImport(Lib)] public static partial int FPDF_StructElement_Attr_GetType(nint value);
    [LibraryImport(Lib)] public static partial int FPDF_StructElement_Attr_GetNumberValue(nint value, float* outValue);

    // Bitmaps produced by PDFium itself (FPDFImageObj_GetRenderedBitmap) carry their own size and format.
    [LibraryImport(Lib)] public static partial int FPDFBitmap_GetWidth(nint bitmap);
    [LibraryImport(Lib)] public static partial int FPDFBitmap_GetHeight(nint bitmap);
    [LibraryImport(Lib)] public static partial int FPDFBitmap_GetFormat(nint bitmap);
    [LibraryImport(Lib)] public static partial void FPDF_RenderPageBitmapWithMatrix(nint bitmap, nint page, FS_MATRIX* matrix, FS_RECTF* clipping, int flags);

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
