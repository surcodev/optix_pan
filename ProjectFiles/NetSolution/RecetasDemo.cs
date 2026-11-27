#region Using directives
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FTOptix.Core;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Recipe;
using FTOptix.Store;
using UAManagedCore;
#endregion

/// <summary>
/// Página Recetas: crear, editar, eliminar y usar recetas. Guarda el historial de acciones
/// y crea las 5 recetas del proyecto si no existe ninguna.
/// El formulario trabaja sobre EditModel del esquema de recetas.
/// </summary>
public class RecetasDemo : BaseNetLogic
{
    private const string TablaHistorial = "HistorialRecetas";
    private const string RecetaInicial = "BPE-M-18-C-4";
    private const string TituloVacio = "Formulario de receta";
    private const int LargoCodigo = 100;
    private const string CarpetaImportar = "Importar";
    private static readonly string[] Tiempos = { "NoTam_MinInicio", "NoTam_MinConfirmacion", "NoTam_MinFinCronometro" };

    private readonly object bloqueo = new object();
    private readonly List<IUAVariable> observadas = new List<IUAVariable>();
    private DelayedTask arranque, limpiarMensaje;
    private int intentos;
    private RecipeSchema schema;
    private IUANode editModel, proceso;
    private Store store;
    private string porEliminar = "";
    private DateTime limiteEliminar;

    public override void Start()
    {
        schema = Project.Current.Get<RecipeSchema>("Recipes/SchemaBatidora");
        editModel = schema.Get("EditModel");
        proceso = Project.Current.Get("Model/Proceso");
        store = Project.Current.Get<Store>("DataStores/DB_SQLSERVER");

        foreach (var nombre in Tiempos)
        {
            var variable = editModel.GetVariable(nombre);
            variable.VariableChange += AlCambiarTiempo;
            observadas.Add(variable);
        }

        CerrarFormulario();
        Programar(500);
    }

    public override void Stop()
    {
        foreach (var variable in observadas)
            variable.VariableChange -= AlCambiarTiempo;
        arranque?.Dispose();
        limpiarMensaje?.Dispose();
    }

    #region Botones de la lista
    [ExportMethod]
    public void Crear()
    {
        lock (bloqueo)
        {
            LimpiarCampos();
            // Valores comunes a todas las recetas del proyecto
            Poner("Carga_HabilitarM2", true);
            Poner("Carga_TiempoSeg", 15);
            Poner("Descarga_TiempoMinMin", 2f);
            AbrirFormulario("", true, "Nueva receta");
            Aviso("Escriba el código, complete los datos y pulse Guardar.");
        }
    }

    [ExportMethod]
    public void Editar(string codigo)
    {
        Intentar(() =>
        {
            Comprobar(codigo);
            schema.CopyFromStoreRecipe(codigo, editModel.NodeId, CopyErrorPolicy.BestEffortCopy);
            AbrirFormulario(codigo, false, "Editando " + codigo);
            Aviso("Cambie los datos y pulse Guardar.");
        });
    }

    [ExportMethod]
    public void Eliminar(string codigo)
    {
        Intentar(() =>
        {
            Comprobar(codigo);

            // Se pide pulsar dos veces para evitar borrados por error
            if (porEliminar != codigo || DateTime.Now > limiteEliminar)
            {
                porEliminar = codigo;
                limiteEliminar = DateTime.Now.AddSeconds(6);
                Aviso("¿Eliminar " + codigo + "? Pulse Eliminar otra vez para confirmar.", true);
                return;
            }

            porEliminar = "";
            schema.DeleteStoreRecipe(codigo);
            Registrar(codigo, "Eliminar");
            if (Texto("EditCodigo") == codigo)
                CerrarFormulario();
            Aviso("Receta " + codigo + " eliminada.");
        });
    }

    /// <summary>Envía la receta guardada al proceso.</summary>
    [ExportMethod]
    public void Usar(string codigo)
    {
        Intentar(() =>
        {
            Comprobar(codigo);
            if ((bool)proceso.GetVariable("CicloActivo").Value)
                throw new InvalidOperationException("No se puede cambiar la receta con un ciclo en marcha.");
            schema.CopyFromStoreRecipe(codigo, schema.TargetNode, CopyErrorPolicy.BestEffortCopy);
            Escribir("RecetaCodigo", codigo);
            Registrar(codigo, "Aplicar");
            Aviso("La máquina usará la receta " + codigo + ".");
        });
    }
    #endregion

    /// <summary>
    /// Importa recetas del Excel elegido. Con ruta vacía (vista web) usa el Recetas*.xlsx más reciente
    /// de la carpeta Descargas de Windows o de ProjectFiles/Importar.
    /// Crea las recetas nuevas y actualiza las que ya existen; cada fila pasa las mismas validaciones que el formulario.
    /// </summary>
    [ExportMethod]
    public void ImportarExcel(string ruta)
    {
        Intentar(() =>
        {
            if ((bool)proceso.GetVariable("EditActivo").Value)
                throw new InvalidOperationException("Cierre el formulario (Guardar o Cancelar) antes de importar.");

            string archivo = string.IsNullOrWhiteSpace(ruta) ? BuscarExcel() : ruta;
            if (!File.Exists(archivo))
                throw new InvalidOperationException("No se encontró el archivo " + archivo + ".");
            var filas = ImportadorRecetas.Leer(archivo);
            if (filas.Count == 0)
                throw new InvalidOperationException("El Excel " + Path.GetFileName(archivo) + " no tiene recetas.");

            int nuevas = 0, actualizadas = 0;
            var errores = new List<string>();
            foreach (var fila in filas)
            {
                try
                {
                    if (fila.Error != null)
                        throw new InvalidOperationException(fila.Error);

                    // Mismos valores por defecto que Crear para las columnas que falten
                    LimpiarCampos();
                    Poner("Carga_HabilitarM2", true);
                    Poner("Carga_TiempoSeg", 15);
                    Poner("Descarga_TiempoMinMin", 2f);
                    foreach (var valor in fila.Valores)
                        Poner(valor.Key, valor.Value);
                    Validar(fila.Codigo, false);

                    if (Existe(fila.Codigo))
                        actualizadas++;
                    else
                    {
                        schema.CreateStoreRecipe(fila.Codigo);
                        nuevas++;
                    }
                    schema.CopyToStoreRecipe(editModel.NodeId, fila.Codigo, CopyErrorPolicy.BestEffortCopy);
                    Registrar(fila.Codigo, "Importar");
                }
                catch (Exception e)
                {
                    errores.Add("fila " + fila.Numero + (string.IsNullOrEmpty(fila.Codigo) ? "" : " (" + fila.Codigo + ")") + ": " + e.Message);
                }
            }
            CerrarFormulario();

            string resumen = Path.GetFileName(archivo) + ": " + nuevas + " nuevas, " + actualizadas + " actualizadas";
            Log.Info("RecetasDemo", "Importación de " + archivo + ": " + nuevas + " nuevas, " + actualizadas + " actualizadas, " + errores.Count + " con error.");
            foreach (var error in errores)
                Log.Warning("RecetasDemo", "Importación, " + error);

            if (errores.Count == 0)
                Aviso("Importado " + resumen + ".");
            else
                Aviso("Importado " + resumen + ", " + errores.Count + " con error. Primera: " + errores[0], true);
        });
    }

    private static string BuscarExcel()
    {
        var carpetas = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            ResourceUri.FromProjectRelativePath(CarpetaImportar).Uri,
        };
        var archivo = carpetas.Where(Directory.Exists)
            .SelectMany(c => Directory.GetFiles(c, "Recetas*.xlsx"))
            .Where(f => !Path.GetFileName(f).StartsWith("~$"))
            .OrderByDescending(File.GetLastWriteTime)
            .FirstOrDefault();
        return archivo ?? throw new InvalidOperationException(
            "No hay ningún archivo Recetas*.xlsx en la carpeta Descargas ni en ProjectFiles/" + CarpetaImportar + ".");
    }

    #region Botones del formulario
    [ExportMethod]
    public void Guardar()
    {
        Intentar(() =>
        {
            if (!(bool)proceso.GetVariable("EditActivo").Value)
                throw new InvalidOperationException("Pulse Crear, o elija una receta y pulse Editar.");

            bool nueva = (bool)proceso.GetVariable("EditNueva").Value;
            string codigo = Texto("EditCodigo").Trim();
            Validar(codigo, nueva);

            if (nueva)
                schema.CreateStoreRecipe(codigo);
            schema.CopyToStoreRecipe(editModel.NodeId, codigo, CopyErrorPolicy.BestEffortCopy);
            Registrar(codigo, nueva ? "Crear" : "Modificar");
            CerrarFormulario();
            Aviso(nueva ? "Receta " + codigo + " creada." : "Cambios de " + codigo + " guardados. Pulse Usar en máquina para aplicarlos.");
        });
    }

    [ExportMethod]
    public void Cancelar()
    {
        lock (bloqueo)
        {
            CerrarFormulario();
            Aviso("No se guardaron cambios.");
        }
    }

    [ExportMethod]
    public void Categoria(int valor)
    {
        lock (bloqueo)
        {
            if (!(bool)proceso.GetVariable("EditActivo").Value || valor < 0 || valor > 4)
                return;

            Poner("NoTam_Categoria", valor);
            if (valor == 0)
            {
                foreach (var nombre in Tiempos)
                    Poner(nombre, 0f);
            }
        }
    }
    #endregion

    private void Validar(string codigo, bool nueva)
    {
        if (codigo.Length == 0)
            throw new InvalidOperationException("Falta el código de la receta.");
        if (codigo.Length > LargoCodigo)
            throw new InvalidOperationException("El código admite hasta " + LargoCodigo + " caracteres.");
        if (nueva && Existe(codigo))
            throw new InvalidOperationException("Ya existe la receta " + codigo + ". Use otro código.");
        if (((string)editModel.GetVariable("NombreReceta").Value ?? "").Trim().Length == 0)
            throw new InvalidOperationException("Falta el nombre de la receta.");

        double mezcla = Numero("Mezcla_TiempoTotalMin");
        if (mezcla <= 0)
            throw new InvalidOperationException("El tiempo de mezcla debe ser mayor que 0.");

        if (Numero("NoTam_Categoria") > 0)
        {
            double inicio = Numero("NoTam_MinInicio"), limite = Numero("NoTam_MinConfirmacion"), pausa = Numero("NoTam_MinFinCronometro");
            if (limite <= inicio || limite > mezcla)
                throw new InvalidOperationException("El límite para confirmar debe ser posterior al inicio y no superar la mezcla.");
            if (pausa > mezcla)
                throw new InvalidOperationException("La pausa del cronómetro no puede superar el tiempo de mezcla.");
        }

        if (Numero("Chopper_MinutosUso") > 0 && Numero("Chopper_MinEncendido") + Numero("Chopper_MinutosUso") > mezcla)
            throw new InvalidOperationException("Los choppers terminan después de la mezcla. Reduzca sus minutos.");
    }

    private void Intentar(Action operacion)
    {
        lock (bloqueo)
        {
            try
            {
                operacion();
            }
            catch (Exception e)
            {
                Aviso(e.Message, true);
            }
        }
    }

    private void Comprobar(string codigo)
    {
        if (string.IsNullOrEmpty(codigo))
            throw new InvalidOperationException("Elija una receta de la lista.");
        if (!Existe(codigo))
            throw new InvalidOperationException("La receta " + codigo + " ya no existe.");
    }

    private void AbrirFormulario(string codigo, bool nueva, string titulo)
    {
        porEliminar = "";
        Escribir("EditCodigo", codigo);
        Escribir("EditNueva", nueva);
        Escribir("EditActivo", true);
        Escribir("EditTitulo", titulo);
        ActualizarTextos();
    }

    private void CerrarFormulario()
    {
        LimpiarCampos();
        Escribir("EditCodigo", "");
        Escribir("EditNueva", false);
        Escribir("EditActivo", false);
        Escribir("EditTitulo", TituloVacio);
        ActualizarTextos();
    }

    private void LimpiarCampos()
    {
        Poner("NombreReceta", "");
        Poner("Carga_HabilitarM2", false);
        Poner("Carga_TiempoSeg", 0);
        Poner("Carga_ModoOperador", false);
        Poner("Mezcla_TiempoTotalMin", 0);
        Poner("NoTam_Categoria", 0);
        Poner("NoTam_MinInicio", 0f);
        Poner("NoTam_MinConfirmacion", 0f);
        Poner("NoTam_MinFinCronometro", 0f);
        Poner("Chopper_MinEncendido", 0f);
        Poner("Chopper_MinutosUso", 0f);
        Poner("Descarga_TiempoMinMin", 0f);
    }

    private void Registrar(string codigo, string accion)
    {
        try
        {
            store.Insert(TablaHistorial, new[] { "Timestamp", "Receta", "Accion" },
                new object[,] { { DateTime.Now, codigo, accion } });
        }
        catch (Exception e)
        {
            Log.Error("RecetasDemo", "No se registró la acción en el historial: " + e.Message);
        }
    }

    private bool Existe(string codigo)
    {
        store.Query("SELECT Name FROM \"SchemaBatidora\" WHERE Name = '" + codigo.Replace("'", "''") + "'", out _, out object[,] filas);
        return filas != null && filas.GetLength(0) > 0;
    }

    private void Aviso(string texto, bool error = false)
    {
        Escribir("MensajeRecetasError", error);
        Escribir("MensajeRecetas", texto);
        Escribir("HayMensajeRecetas", true);
        limpiarMensaje?.Dispose();
        limpiarMensaje = new DelayedTask(() => Escribir("HayMensajeRecetas", false), 8000, LogicObject);
        limpiarMensaje.Start();
    }

    private void AlCambiarTiempo(object sender, VariableChangeEventArgs e) => ActualizarTextos();

    private void ActualizarTextos()
    {
        foreach (var nombre in Tiempos)
        {
            int segundos = (int)Math.Round(Numero(nombre) * 60);
            Escribir("Txt_" + nombre, $"{segundos / 60:00}:{segundos % 60:00}");
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
            lock (bloqueo)
            {
                CrearRecetasBase();
                if (Existe(RecetaInicial))
                {
                    schema.CopyFromStoreRecipe(RecetaInicial, schema.TargetNode, CopyErrorPolicy.BestEffortCopy);
                    Escribir("RecetaCodigo", RecetaInicial);
                }
            }
        }
        catch (Exception e)
        {
            // SQL Server puede tardar en responder: se reintenta hasta que esté en línea
            if (++intentos == 10)
                Log.Error("RecetasDemo", "Recetas no disponibles, se sigue reintentando: " + e.Message);
            Programar(intentos < 10 ? 1000 : 5000);
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

        CerrarFormulario();
        Log.Info("RecetasDemo", "Recetas base creadas");
    }

    private void Poner(string nombre, object valor) => editModel.GetVariable(nombre).Value = new UAValue(valor);

    private double Numero(string nombre) => Convert.ToDouble(editModel.GetVariable(nombre).Value.Value);

    private string Texto(string nombre) => (string)proceso.GetVariable(nombre).Value ?? "";

    private void Escribir(string nombre, object valor) => proceso.GetVariable(nombre).Value = new UAValue(valor);
}
