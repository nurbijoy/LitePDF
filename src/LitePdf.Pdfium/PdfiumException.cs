namespace LitePdf.Pdfium;

public sealed class PdfiumException : Exception
{
    public int ErrorCode { get; }

    public PdfiumException(int errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }

    public PdfiumException(int errorCode, string message, Exception inner) : base(message, inner)
    {
        ErrorCode = errorCode;
    }

    public static string MessageForError(int code) => code switch
    {
        1 => "Unknown error.",
        2 => "File not found or could not be opened.",
        3 => "This file is not a valid PDF or is damaged.",
        4 => "Password required or incorrect password.",
        5 => "Unsupported security handler.",
        6 => "Page not found or content error.",
        _ => $"PDFium error {code}."
    };
}
