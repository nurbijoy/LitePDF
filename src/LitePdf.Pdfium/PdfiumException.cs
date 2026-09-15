namespace LitePdf.Pdfium;

/// <summary>PDFium FPDF_ERR_* codes.</summary>
public enum PdfiumError
{
    Unknown = 1,
    File = 2,
    Format = 3,
    Password = 4,
    Security = 5,
    Page = 6,
}

public sealed class PdfiumException : Exception
{
    public PdfiumException(PdfiumError error, string message, Exception? inner = null)
        : base(message, inner)
    {
        Error = error;
    }

    public PdfiumError Error { get; }

    internal static PdfiumException FromLastError(uint code) => (PdfiumError)code switch
    {
        PdfiumError.File => new(PdfiumError.File, "The file could not be opened."),
        PdfiumError.Format => new(PdfiumError.Format, "This file is not a valid PDF or is damaged."),
        PdfiumError.Password => new(PdfiumError.Password, "This document is protected by a password."),
        PdfiumError.Security => new(PdfiumError.Security, "This document uses an unsupported security handler."),
        PdfiumError.Page => new(PdfiumError.Page, "The page could not be loaded."),
        _ => new(PdfiumError.Unknown, "The document could not be opened."),
    };
}
