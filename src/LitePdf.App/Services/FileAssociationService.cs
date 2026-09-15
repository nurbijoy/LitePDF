using Microsoft.Win32;

namespace LitePdf.App.Services;

public static class FileAssociationService
{
    public static bool IsAssociated()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.pdf", false);
            if (key == null) return false;
            var val = key.GetValue("") as string;
            return val == "LitePDF.pdf";
        }
        catch { return false; }
    }

    public static void Register()
    {
        try
        {
            string exePath = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(exePath)) return;

            using (var pdfKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.pdf"))
            {
                pdfKey.SetValue("", "LitePDF.pdf");
            }
            using (var appKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\LitePDF.pdf"))
            {
                appKey.SetValue("", "PDF Document");
                using var icon = appKey.CreateSubKey("DefaultIcon");
                icon.SetValue("", $"\"{exePath}\",0");
                using var shell = appKey.CreateSubKey(@"shell\open\command");
                shell.SetValue("", $"\"{exePath}\" \"%1\"");
            }

            // Notify shell
            NativeMethods.SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    public static void Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\LitePDF.pdf", false);
            using var pdfKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.pdf", true);
            if (pdfKey != null)
            {
                var val = pdfKey.GetValue("") as string;
                if (val == "LitePDF.pdf")
                    pdfKey.DeleteValue("");
            }
            NativeMethods.SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        public static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);
    }
}
