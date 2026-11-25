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
using FTOptix.ODBCStore;
#endregion

/// <summary>
/// Centra la ventana en el área útil de la pantalla principal (sin la barra de tareas).
/// </summary>
public class RuntimeNetLogic1 : BaseNetLogic
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangulo
    {
        public int Izquierda, Arriba, Derecha, Abajo;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static extern bool SystemParametersInfo(uint accion, uint parametro, out Rectangulo area, uint opciones);

    private const uint SPI_GETWORKAREA = 0x0030;
    private const int SM_CYCAPTION = 4;
    private const int SM_CYSIZEFRAME = 33;
    private const int SM_CXPADDEDBORDER = 92;
    private const float MargenRecortable = 16f;

    public override void Start()
    {
        // Busca la ventana que contiene este NetLogic
        var ventana = Owner as Window;
        if (ventana == null)
        {
            Log.Warning("RuntimeNetLogic1", "El NetLogic debe estar dentro de MainWindow.");
            return;
        }

        if (!OperatingSystem.IsWindows() || !SystemParametersInfo(SPI_GETWORKAREA, 0, out Rectangulo area, 0))
            return;

        // Windows entrega píxeles reales; X, Y, Width y Height usan píxeles sin escala (125 %, 150 %...)
        float escala = GetDpiForSystem() / 96f;
        float izquierda = area.Izquierda / escala;
        float arriba = area.Arriba / escala;
        float ancho = (area.Derecha - area.Izquierda) / escala;
        float alto = (area.Abajo - area.Arriba) / escala;

        // Y ubica el borde superior de la barra de título; el contenido queda debajo
        float barraTitulo = (GetSystemMetrics(SM_CYCAPTION) + GetSystemMetrics(SM_CYSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER)) / escala;

        // Si solo falta un poco de alto, se recorta el margen vacío inferior para que quepa completa
        float exceso = ventana.Height + barraTitulo - alto;
        if (exceso > 0 && exceso <= MargenRecortable)
            ventana.Height -= exceso;

        float altoTotal = ventana.Height + barraTitulo;

        // Si la ventana no cabe, se alinea arriba para que la barra de título siempre sea visible
        ventana.X = izquierda + Math.Max(0, (ancho - ventana.Width) / 2);
        ventana.Y = arriba + Math.Max(0, (alto - altoTotal) / 2);

        Log.Info("RuntimeNetLogic1", $"Ventana centrada en X={ventana.X} Y={ventana.Y} (escala {escala:P0})");
    }

    public override void Stop()
    {
    }
}
