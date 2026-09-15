using System.Runtime.InteropServices;

namespace LitePdf.Pdfium;

/// <summary>
/// Bridges a FileStream to PDFium's FPDF_FILEACCESS callback.
/// The instance must stay alive while the document is open.
/// </summary>
internal sealed class FileAccessBridge : IDisposable
{
    private readonly FileStream _stream;
    private readonly NativeMethods.GetBlockDelegate _callback;
    private readonly GCHandle _selfHandle;
    private bool _disposed;

    public NativeMethods.FPDF_FILEACCESS Access;

    public FileAccessBridge(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _callback = GetBlock;
        _selfHandle = GCHandle.Alloc(this);

        Access = new NativeMethods.FPDF_FILEACCESS
        {
            m_FileLen = (uint)_stream.Length,
            m_GetBlock = Marshal.GetFunctionPointerForDelegate(_callback),
            m_Param = GCHandle.ToIntPtr(_selfHandle)
        };
    }

    public FileAccessBridge(FileStream stream)
    {
        _stream = stream;
        _callback = GetBlock;
        _selfHandle = GCHandle.Alloc(this);

        Access = new NativeMethods.FPDF_FILEACCESS
        {
            m_FileLen = (uint)_stream.Length,
            m_GetBlock = Marshal.GetFunctionPointerForDelegate(_callback),
            m_Param = GCHandle.ToIntPtr(_selfHandle)
        };
    }

    private static int GetBlock(IntPtr param, uint position, IntPtr pBuf, uint size)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(param);
            if (handle.Target is not FileAccessBridge bridge)
                return 0;
            if (bridge._disposed)
                return 0;

            var stream = bridge._stream;
            lock (stream)
            {
                stream.Seek(position, SeekOrigin.Begin);
                byte[] buffer = new byte[size];
                int read = 0;
                while (read < size)
                {
                    int n = stream.Read(buffer, read, (int)(size - (uint)read));
                    if (n == 0) break;
                    read += n;
                }
                if (read != size)
                    return 0;
                Marshal.Copy(buffer, 0, pBuf, (int)size);
                return 1;
            }
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _selfHandle.Free();
        _stream.Dispose();
    }
}
