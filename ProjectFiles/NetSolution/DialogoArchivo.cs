using System;
using System.Runtime.InteropServices;
using System.Threading;

/// <summary>
/// Diálogo "Abrir" de Windows (comdlg32). Solo sirve en el runtime nativo de Windows:
/// en la vista web aparecería en la PC del servidor, no en el navegador.
/// </summary>
internal static class DialogoArchivo
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public string lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public string lpstrFileTitle;
        public int nMaxFileTitle;
        public string lpstrInitialDir;
        public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetOpenFileNameW")]
    private static extern bool GetOpenFileName(ref OpenFileName ofn);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private const int OFN_NOCHANGEDIR = 0x8, OFN_HIDEREADONLY = 0x4, OFN_PATHMUSTEXIST = 0x800, OFN_FILEMUSTEXIST = 0x1000, OFN_EXPLORER = 0x80000;
    private const int Largo = 1024;

    /// <summary>Ruta elegida, o null si se canceló.</summary>
    public static string ElegirExcel(string titulo, string carpetaInicial)
    {
        // La ventana de Optix queda como dueña: el diálogo se abre encima y la bloquea mientras está abierto
        IntPtr dueno = GetForegroundWindow();
        string ruta = null;

        // El diálogo necesita un hilo STA propio
        var hilo = new Thread(() =>
        {
            IntPtr buffer = Marshal.AllocHGlobal(Largo * 2);
            try
            {
                Marshal.Copy(new byte[Largo * 2], 0, buffer, Largo * 2);
                var ofn = new OpenFileName
                {
                    lStructSize = Marshal.SizeOf<OpenFileName>(),
                    hwndOwner = dueno,
                    lpstrFilter = "Recetas de Excel (*.xlsx;*.csv)\0*.xlsx;*.csv\0Libro de Excel (*.xlsx)\0*.xlsx\0CSV (*.csv)\0*.csv\0\0",
                    nFilterIndex = 1,
                    lpstrFile = buffer,
                    nMaxFile = Largo,
                    lpstrInitialDir = carpetaInicial,
                    lpstrTitle = titulo,
                    Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR | OFN_HIDEREADONLY,
                    lpstrDefExt = "xlsx",
                };
                if (GetOpenFileName(ref ofn))
                    ruta = Marshal.PtrToStringUni(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        });
        hilo.SetApartmentState(ApartmentState.STA);
        hilo.Start();
        hilo.Join();
        return string.IsNullOrEmpty(ruta) ? null : ruta;
    }
}
