#region Using directives
using System;
using System.Runtime.InteropServices;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.SQLiteStore;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.NativeUI;
using FTOptix.Recipe;
using FTOptix.Store;
using FTOptix.Retentivity;
using FTOptix.CoreBase;
using FTOptix.NetLogic;
using FTOptix.Core;
using FTOptix.WebUI;
#endregion

public class RuntimeNetLogic1 : BaseNetLogic
{
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    public override void Start()
    {
        // Busca la ventana que contiene este NetLogic
        var ventana = Owner as Window;
        if (ventana == null)
        {
            Log.Warning("RuntimeNetLogic1", "El NetLogic debe estar dentro de MainWindow.");
            return;
        }

        float anchoPantalla = GetSystemMetrics(SM_CXSCREEN);
        float altoPantalla = GetSystemMetrics(SM_CYSCREEN);

        ventana.X = (anchoPantalla - ventana.Width) / 2;
        ventana.Y = (altoPantalla - ventana.Height) / 2;

        Log.Info("RuntimeNetLogic1", $"Ventana centrada en X={ventana.X} Y={ventana.Y}");
    }

    public override void Stop()
    {
    }
}
