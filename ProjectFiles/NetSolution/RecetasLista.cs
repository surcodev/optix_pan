#region Using directives
using System;
using System.IO;
using FTOptix.Core;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.UI;
using UAManagedCore;
#endregion

/// <summary>
/// Botones de la lista de recetas: lee la fila elegida y llama a RecetasDemo.
/// Vive en la pantalla porque las filas de la grilla solo existen en la sesión del usuario.
/// </summary>
public class RecetasLista : BaseNetLogic
{
    public override void Start()
    {
        Session.UserVariable.VariableChange += AlCambiarUsuario;
        AplicarPermisos();
    }

    private void AlCambiarUsuario(object sender, VariableChangeEventArgs e) => AplicarPermisos();

    /// <summary>
    /// El operario solo puede usar recetas en la máquina: se ocultan crear, editar, eliminar, importar
    /// y el formulario (su estado es compartido entre sesiones). Se repite si cambia el usuario de la sesión.
    /// </summary>
    private void AplicarPermisos()
    {
        bool admin = GestionUsuarios.EsAdministrador(Session.User);
        foreach (var nombre in new[] { "BtnCrear", "BtnEditar", "BtnEliminar", "BtnImportar" })
        {
            var boton = Owner.Get<Item>(nombre);
            if (boton != null)
                boton.Visible = admin;
        }

        var editor = Owner.Owner?.Get<Item>("Editor");
        if (editor == null)
            return;
        editor.Visible = admin;

        // Sin formulario, la lista ocupa también el espacio del editor (una sola vez)
        if (!admin && !ensanchada && Owner is Item lista)
        {
            ensanchada = true;
            float extra = editor.LeftMargin + editor.Width - (lista.LeftMargin + lista.Width);
            if (extra <= 0)
                return;
            lista.Width += extra;
            foreach (var ruta in new[] { "GridRecetas", "EnUso", "EnUso/N" })
            {
                var item = lista.Get<Item>(ruta);
                if (item != null)
                    item.Width += extra;
            }
        }
    }

    [ExportMethod]
    public void Editar()
    {
        if (PermitidoSoloAdmin("Editar"))
            Llamar("Editar");
    }

    [ExportMethod]
    public void Eliminar()
    {
        if (PermitidoSoloAdmin("Eliminar"))
            Llamar("Eliminar");
    }

    private bool ensanchada;

    public override void Stop()
    {
        Session.UserVariable.VariableChange -= AlCambiarUsuario;
        dialogo?.Dispose();
        if (sesionWeb != null)
            sesionWeb.UploadFileCompleted -= AlSubirArchivo;
        ocultarMensaje?.Dispose();
    }

    /// <summary>
    /// Importa recetas desde Excel (solo administradores). En el runtime nativo abre el diálogo de Windows;
    /// en la vista web abre el selector del navegador, sube el archivo al servidor y lo importa al terminar.
    /// </summary>
    [ExportMethod]
    public void Importar()
    {
        if (!PermitidoSoloAdmin("Importar"))
            return;

        if (EsWeb())
        {
            SubirDesdeNavegador();
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            Mensaje("El diálogo para elegir archivos solo está disponible en Windows.", true);
            return;
        }
        // El diálogo espera al usuario: se abre fuera del hilo del evento para no frenar el runtime
        if (dialogo != null && !dialogo.IsCancellationRequested && dialogoAbierto)
            return;
        dialogo?.Dispose();
        dialogoAbierto = true;
        dialogo = new LongRunningTask(() =>
        {
            try
            {
                string descargas = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                string ruta = DialogoArchivo.ElegirExcel("Elegir Excel de recetas", descargas);
                if (ruta != null)
                {
                    ImportarArchivo(ruta);
                    Refrescar();
                }
            }
            finally
            {
                dialogoAbierto = false;
            }
        }, LogicObject);
        dialogo.Start();
    }

    private LongRunningTask dialogo;
    private volatile bool dialogoAbierto;

    #region Carga desde el navegador
    private const string CarpetaSubidas = "Subidas";
    private UISession sesionWeb;
    private Guid subida = Guid.Empty;
    private string destinoSubida = "";
    private DelayedTask ocultarMensaje;

    private void SubirDesdeNavegador()
    {
        try
        {
            if (sesionWeb == null)
            {
                sesionWeb = (UISession)Session;
                sesionWeb.UploadFileCompleted += AlSubirArchivo;
            }
            // UploadFile recibe una carpeta existente dentro de ProjectFiles; el archivo conserva su nombre.
            // Optix responde Forbidden si no está permitido en AllowedLocalSources del WebPresentationEngine
            // (se lee al arrancar: *.xlsx, Subidas y Subidas/* están en UI.yaml).
            Directory.CreateDirectory(ResourceUri.FromProjectRelativePath(CarpetaSubidas).Uri);
            destinoSubida = CarpetaSubidas;
            subida = sesionWeb.UploadFile(ResourceUri.FromProjectRelativePath(CarpetaSubidas));
        }
        catch (Exception e)
        {
            Mensaje("No se pudo abrir el selector de archivos del navegador.", true);
            Log.Error("RecetasLista", "UploadFile: " + e.Message);
        }
    }

    private void AlSubirArchivo(object sender, UploadFileCompletedEvent e)
    {
        if (e.OperationId != subida)
            return;
        subida = Guid.Empty;

        if (e.Result == UploadFileCompletedResultCode.UploadFileCancelled)
            return;
        if (e.Result != UploadFileCompletedResultCode.Success)
        {
            Log.Error("RecetasLista", "Carga de archivo rechazada: " + e.Result + " (destino ProjectFiles/" + destinoSubida + ", usuario " + Session.User?.BrowseName + ")");
            Mensaje(e.Result switch
            {
                UploadFileCompletedResultCode.FileTooLarge => "El archivo es demasiado grande.",
                // Optix web solo acepta contenido ZIP con extensión .zip o .jar, y un .xlsx es un ZIP
                UploadFileCompletedResultCode.ContentExtensionMismatch =>
                    "En el navegador no se pueden subir archivos .xlsx. En Excel use Guardar como > CSV UTF-8 y suba el .csv.",
                UploadFileCompletedResultCode.Forbidden => "El servidor rechazó la carpeta de destino del archivo.",
                _ => "No se pudo subir el archivo (" + e.Result + ").",
            }, true);
            return;
        }

        try
        {
            string extension = Path.GetExtension(e.FilePath).ToLowerInvariant();
            if (extension != ".csv" && extension != ".xlsx")
            {
                Mensaje("Elija un archivo .csv (en Excel: Guardar como > CSV UTF-8).", true);
                return;
            }
            ImportarArchivo(e.FilePath);
            Refrescar();
        }
        finally
        {
            // El archivo subido solo hace falta para importar
            try { File.Delete(e.FilePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private void Refrescar()
    {
        Owner.Get<DataGrid>("GridRecetas")?.Refresh();
        Owner.Owner?.Get<DataGrid>("Historial/GridHistorial")?.Refresh();
    }

    /// <summary>Muestra un aviso en la barra de mensajes de Recetas (las mismas variables que usa RecetasDemo).</summary>
    private void Mensaje(string texto, bool error)
    {
        var proceso = Project.Current.Get("Model/Proceso");
        proceso.GetVariable("MensajeRecetasError").Value = error;
        proceso.GetVariable("MensajeRecetas").Value = texto;
        proceso.GetVariable("HayMensajeRecetas").Value = true;
        ocultarMensaje?.Dispose();
        ocultarMensaje = new DelayedTask(() => proceso.GetVariable("HayMensajeRecetas").Value = false, 8000, LogicObject);
        ocultarMensaje.Start();
    }
    #endregion

    private static void ImportarArchivo(string ruta) =>
        Project.Current.Get<NetLogicObject>("NetLogic/RecetasDemo").ExecuteMethod("ImportarExcel", new object[] { ruta });

    private bool EsWeb()
    {
        for (IUANode nodo = LogicObject; nodo != null; nodo = nodo.Owner)
        {
            if (nodo is FTOptix.WebUI.WebUIPresentationEngine)
                return true;
        }
        return false;
    }

    private bool PermitidoSoloAdmin(string accion)
    {
        if (GestionUsuarios.EsAdministrador(Session.User))
            return true;
        Log.Warning("RecetasLista", accion + " rechazado para " + (Session.User?.BrowseName ?? "?"));
        return false;
    }

    [ExportMethod]
    public void Usar() => Llamar("Usar");

    private void Llamar(string metodo)
    {
        string codigo = "";
        var grilla = Owner.Get<DataGrid>("GridRecetas");
        var fila = grilla.UISelectedItem;
        if (fila != null && !fila.IsEmpty)
        {
            var variable = InformationModel.Get(fila)?.GetVariable("Codigo");
            if (variable != null)
                codigo = Convert.ToString(variable.Value.Value);
        }

        Project.Current.Get<NetLogicObject>("NetLogic/RecetasDemo").ExecuteMethod(metodo, new object[] { codigo });
    }
}
