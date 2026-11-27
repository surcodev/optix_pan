#region Using directives
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Store;
using UAManagedCore;
#endregion

/// <summary>
/// Vigila la conexión con la base de datos (DataStores/DB_SQLSERVER).
/// Cada 5 s hace una consulta liviana en otro hilo y publica el resultado en sus variables:
/// Desconectada (bloquea la interfaz con el panel MainWindow/SinConexion), Mensaje y UltimaConexion.
/// </summary>
public class MonitorBaseDatos : BaseNetLogic
{
    private const int PeriodoMs = 5000;
    private const string Consulta = "SELECT COUNT(*) FROM \"SchemaBatidora\"";

    private Store store;
    private PeriodicTask tarea;
    private int probando;
    private bool? conectada;
    private DateTime ultimaConexion;

    public override void Start()
    {
        store = Project.Current.Get<Store>("DataStores/DB_SQLSERVER");
        Escribir("Desconectada", false);
        Escribir("Mensaje", "");
        Escribir("UltimaConexion", "");

        tarea = new PeriodicTask(Probar, PeriodoMs, LogicObject);
        tarea.Start();
        Task.Run(Probar);
    }

    public override void Stop()
    {
        tarea?.Dispose();
    }

    /// <summary>Prueba la conexión de inmediato (botón "Reintentar ahora").</summary>
    [ExportMethod]
    public void Reintentar()
    {
        Escribir("Mensaje", "Reintentando...");
        Task.Run(Probar);
    }

    private void Probar()
    {
        // Sin servidor la consulta tarda lo que el timeout de ODBC: no se solapan pruebas
        if (Interlocked.Exchange(ref probando, 1) == 1)
            return;

        try
        {
            string error = null;
            if (store == null)
                error = "No existe DataStores/DB_SQLSERVER en el proyecto.";
            else
            {
                try
                {
                    store.Query(Consulta, out _, out _);
                    if (store.Status == StoreStatus.Error)
                        error = "El DataStore informa un error.";
                }
                catch (Exception e)
                {
                    error = e.Message;
                }
            }

            if (error == null)
                Conectada();
            else
                Desconectada(error);
        }
        finally
        {
            Interlocked.Exchange(ref probando, 0);
        }
    }

    private void Conectada()
    {
        ultimaConexion = DateTime.Now;
        if (conectada == true)
            return;

        if (conectada == false)
            Log.Info("MonitorBaseDatos", "Conexión con la base de datos recuperada.");
        conectada = true;
        Escribir("Desconectada", false);
        Escribir("Mensaje", "");
    }

    private void Desconectada(string error)
    {
        string dsn = Convert.ToString(store?.GetVariable("DSN")?.Value?.Value ?? "");
        Escribir("Mensaje", "No responde el servidor del origen de datos ODBC \"" + dsn + "\". Revise la red y que SQL Server esté en marcha.");
        Escribir("UltimaConexion", ultimaConexion == default
            ? "Sin conexión desde que se inició la aplicación."
            : "Última conexión correcta: " + ultimaConexion.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture));

        if (conectada == false)
            return;
        conectada = false;
        Escribir("Desconectada", true);
        Log.Error("MonitorBaseDatos", "Sin conexión con la base de datos: " + error);
    }

    private void Escribir(string nombre, object valor)
    {
        var variable = LogicObject.GetVariable(nombre);
        if (variable != null)
            variable.Value = new UAValue(valor);
    }
}
