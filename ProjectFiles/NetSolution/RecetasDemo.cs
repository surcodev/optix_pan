#region Using directives
using System;
using System.Collections.Generic;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Recipe;
using FTOptix.Store;
using UAManagedCore;
using FTOptix.ODBCStore;
#endregion

/// <summary>
/// Comandos de la página Recetas, historial de acciones y carga inicial de las 5 recetas del proyecto.
/// </summary>
public class RecetasDemo : BaseNetLogic
{
    private const string TablaHistorial = "HistorialRecetas";
    private const string RecetaInicial = "BPE-M-18-C-4";
    private static readonly string[] Tiempos = { "NoTam_MinInicio", "NoTam_MinConfirmacion", "NoTam_MinFinCronometro" };

    private readonly List<IUAVariable> observadas = new List<IUAVariable>();
    private DelayedTask arranque, limpiarMensaje;
    private int intentos;
    private RecipeSchema schema;
    private IUANode editModel, proceso;
    private Store store;

    public override void Start()
    {
        schema = Project.Current.Get<RecipeSchema>("Recipes/SchemaBatidora");
        editModel = schema.Get("EditModel");
        proceso = Project.Current.Get("Model/Proceso");
        store = Project.Current.Get<Store>("DataStores/DB");

        foreach (var nombre in Tiempos)
        {
            var variable = editModel.GetVariable(nombre);
            variable.VariableChange += AlCambiarTiempo;
            observadas.Add(variable);
        }

        Programar(500);
    }

    public override void Stop()
    {
        foreach (var variable in observadas)
            variable.VariableChange -= AlCambiarTiempo;
        arranque?.Dispose();
        limpiarMensaje?.Dispose();
    }

    #region Comandos
    [ExportMethod]
    public void Guardar(string codigo)
    {
        Ejecutar(codigo, "Guardar", nombre =>
        {
            if (!Existe(nombre))
                schema.CreateStoreRecipe(nombre);
            schema.CopyToStoreRecipe(editModel.NodeId, nombre, CopyErrorPolicy.BestEffortCopy);
            return "Receta " + nombre + " guardada.";
        });
    }

    [ExportMethod]
    public void Eliminar(string codigo)
    {
        Ejecutar(codigo, "Eliminar", nombre =>
        {
            if (!Existe(nombre))
                throw new InvalidOperationException("La receta " + nombre + " no existe.");
            schema.DeleteStoreRecipe(nombre);
            return "Receta " + nombre + " eliminada.";
        });
    }

    /// <summary>Copia al editor los valores de la receta activa en el proceso.</summary>
    [ExportMethod]
    public void Cargar()
    {
        Ejecutar((string)proceso.GetVariable("RecetaCodigo").Value, "Cargar", nombre =>
        {
            schema.Copy(schema.TargetNode, editModel.NodeId, CopyErrorPolicy.BestEffortCopy);
            return "Valores activos cargados en el editor.";
        });
    }

    /// <summary>Envía los valores del editor al proceso.</summary>
    [ExportMethod]
    public void Aplicar(string codigo)
    {
        Ejecutar(codigo, "Aplicar", nombre =>
        {
            if ((bool)proceso.GetVariable("CicloActivo").Value)
                throw new InvalidOperationException("No se puede cambiar la receta con un ciclo en marcha.");
            schema.CopyFromEditModel(editModel.NodeId, schema.TargetNode, CopyErrorPolicy.BestEffortCopy);
            proceso.GetVariable("RecetaCodigo").Value = nombre;
            return "Receta " + nombre + " aplicada al proceso.";
        });
    }
    #endregion

    private void Ejecutar(string codigo, string accion, Func<string, string> operacion)
    {
        codigo = codigo?.Trim();
        if (string.IsNullOrEmpty(codigo))
        {
            Mensaje("Escriba o elija un código de receta.");
            return;
        }

        try
        {
            string texto = operacion(codigo);
            store.Insert(TablaHistorial, new[] { "Timestamp", "Receta", "Accion" },
                new object[,] { { DateTime.Now, codigo, accion } });
            Mensaje(texto);
        }
        catch (Exception e)
        {
            Mensaje(e.Message);
        }
    }

    private bool Existe(string codigo)
    {
        store.Query("SELECT Name FROM \"SchemaBatidora\" WHERE Name = '" + codigo.Replace("'", "''") + "'", out _, out object[,] filas);
        return filas != null && filas.GetLength(0) > 0;
    }

    private void Mensaje(string texto)
    {
        proceso.GetVariable("MensajeRecetas").Value = texto;
        limpiarMensaje?.Dispose();
        limpiarMensaje = new DelayedTask(() => proceso.GetVariable("MensajeRecetas").Value = "", 6000, LogicObject);
        limpiarMensaje.Start();
    }

    private void AlCambiarTiempo(object sender, VariableChangeEventArgs e) => ActualizarTextos();

    private void ActualizarTextos()
    {
        foreach (var nombre in Tiempos)
        {
            int segundos = (int)Math.Round(Convert.ToDouble(editModel.GetVariable(nombre).Value.Value) * 60);
            proceso.GetVariable("Txt_" + nombre).Value = $"{segundos / 60:00}:{segundos % 60:00}";
        }
    }

    private void Programar(int milisegundos)
    {
        arranque?.Dispose();
        arranque = new DelayedTask(CargaInicial, milisegundos, LogicObject);
        arranque.Start();
    }

    private void CargaInicial()
    {
        try
        {
            CrearRecetasBase();
            if (Existe(RecetaInicial))
            {
                schema.CopyFromStoreRecipe(RecetaInicial, schema.TargetNode, CopyErrorPolicy.BestEffortCopy);
                schema.CopyFromStoreRecipe(RecetaInicial, editModel.NodeId, CopyErrorPolicy.BestEffortCopy);
                proceso.GetVariable("RecetaCodigo").Value = RecetaInicial;
            }
            ActualizarTextos();
        }
        catch (Exception e)
        {
            if (++intentos < 10)
                Programar(1000);
            else
                Log.Error("RecetasDemo", "No se cargaron las recetas base: " + e.Message);
        }
    }

    private void CrearRecetasBase()
    {
        // codigo, nombre, mezcla, categoria, inicioNoTam, confirmacionNoTam, finCronometro, chopperDesde, chopperUso, descarga
        // En la hoja de recetas, 7.55 y 13.05 son min:seg
        object[][] recetas =
        {
            new object[] { "BPE-0-13-0-0", "Mezcla Estándar 13 min", 13, 0, 0f, 0f, 0f, 0f, 0f, 2f },
            new object[] { "BPE-0-13-C-4", "Mezcla Estándar 13 min con Chopper 4 min", 13, 0, 0f, 0f, 0f, 4f, 4f, 2f },
            new object[] { "BPE-M-18-C-4", "Mezcla con Manteca 18 min con Chopper 4 min", 18, 1, 4f, 7f + 55f / 60f, 0f, 8f, 4f, 2f },
            new object[] { "BPE-A-18-C-6", "Mezcla con Aceite 18 min con Chopper 6 min", 18, 3, 6f, 7f + 55f / 60f, 0f, 6f, 2f, 2f },
            new object[] { "BPE-G-26-C-20", "Mezcla con Galletas 26 min con Chopper 20 min", 26, 4, 13f, 13f + 5f / 60f, 13f, 0f, 20f, 2f },
        };

        foreach (var r in recetas)
        {
            if (Existe((string)r[0]))
                return;
        }

        foreach (var r in recetas)
        {
            string codigo = (string)r[0];
            Poner("NombreReceta", r[1]);
            Poner("Carga_HabilitarM2", true);
            Poner("Carga_TiempoSeg", 15);
            Poner("Carga_ModoOperador", false);
            Poner("Mezcla_TiempoTotalMin", r[2]);
            Poner("NoTam_Categoria", r[3]);
            Poner("NoTam_MinInicio", r[4]);
            Poner("NoTam_MinConfirmacion", r[5]);
            Poner("NoTam_MinFinCronometro", r[6]);
            Poner("Chopper_MinEncendido", r[7]);
            Poner("Chopper_MinutosUso", r[8]);
            Poner("Descarga_TiempoMinMin", r[9]);
            schema.CreateStoreRecipe(codigo);
            schema.CopyToStoreRecipe(editModel.NodeId, codigo, CopyErrorPolicy.BestEffortCopy);
        }

        Log.Info("RecetasDemo", "Recetas base creadas");
    }

    private void Poner(string nombre, object valor) => editModel.GetVariable(nombre).Value = new UAValue(valor);
}
