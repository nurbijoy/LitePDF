using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LitePdf.Pdfium.Interop;

/// <summary>
/// Streams a file to PDFium through FPDF_FILEACCESS. PDFium keeps the struct pointer for the document's lifetime,
/// so the struct lives in native memory and is freed only after the document is closed.
/// Only used on the PDFium worker thread.
/// </summary>
internal sealed unsafe class FileAccessBridge : IDisposable
{
    private readonly FileStream _stream;
    private GCHandle _handle;
    private bool _disposed;

    private FileAccessBridge(FileStream stream)
    {
        _stream = stream;
        _handle = GCHandle.Alloc(this);
        Access = (FPDF_FILEACCESS*)NativeMemory.AllocZeroed((nuint)sizeof(FPDF_FILEACCESS));
        Access->FileLen = (uint)stream.Length;
        Access->GetBlock = &GetBlock;
        Access->Param = GCHandle.ToIntPtr(_handle);
    }

    public FPDF_FILEACCESS* Access { get; }

    public long Length => _stream.Length;

    public static FileAccessBridge Open(string path)
    {
        // FileShare.Delete lets other apps rename/delete the file while it is open; writers are blocked.
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, bufferSize: 0, FileOptions.RandomAccess);
        if (stream.Length > uint.MaxValue)
        {
            stream.Dispose();
            throw new PdfiumException(PdfiumError.File, "Files larger than 4 GB are not supported.");
        }
        return new FileAccessBridge(stream);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetBlock(nint param, uint position, byte* buffer, uint size)
    {
        try
        {
            if (GCHandle.FromIntPtr(param).Target is not FileAccessBridge bridge || bridge._disposed) return 0;
            var span = new Span<byte>(buffer, checked((int)size));
            bridge._stream.Position = position;
            bridge._stream.ReadExactly(span);
            return 1;
        }
        catch
        {
            return 0; // PDFium treats 0 as a read error; exceptions must not cross the native boundary.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NativeMemory.Free(Access);
        _handle.Free();
        _stream.Dispose();
    }
}

/// <summary>Receives FPDF_SaveAsCopy output and writes it to a stream.</summary>
internal sealed unsafe class FileWriteBridge : IDisposable
{
    private readonly Stream _stream;
    private GCHandle _handle;

    public FileWriteBridge(Stream stream)
    {
        _stream = stream;
        _handle = GCHandle.Alloc(this);
        Context = (FileWriteContext*)NativeMemory.AllocZeroed((nuint)sizeof(FileWriteContext));
        Context->Version = 1;
        Context->WriteBlock = &WriteBlock;
        Context->Handle = GCHandle.ToIntPtr(_handle);
    }

    public FileWriteContext* Context { get; }

    public Exception? Error { get; private set; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WriteBlock(FileWriteContext* context, byte* data, uint size)
    {
        FileWriteBridge? bridge = null;
        try
        {
            bridge = GCHandle.FromIntPtr(context->Handle).Target as FileWriteBridge;
            if (bridge is null) return 0;
            bridge._stream.Write(new ReadOnlySpan<byte>(data, checked((int)size)));
            return 1;
        }
        catch (Exception ex)
        {
            if (bridge is not null) bridge.Error = ex;
            return 0;
        }
    }

    public void Dispose()
    {
        NativeMemory.Free(Context);
        _handle.Free();
    }
}
