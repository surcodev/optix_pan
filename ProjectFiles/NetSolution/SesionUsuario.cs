#region Using directives
using System;
using FTOptix.Core;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.UI;
using UAManagedCore;
#endregion

/// <summary>
/// Inicio y cierre de sesión. Vive en MainWindow, así cada sesión (nativa o web) tiene su propia instancia.
/// Muestra el panel Login mientras el usuario es anónimo y escribe en la cabecera el usuario y su rol.
/// </summary>
public class SesionUsuario : BaseNetLogic
{
    private const string Anonimo = "Anonymous";

    public override void Start()
    {
        Session.UserVariable.VariableChange += AlCambiarUsuario;
        Actualizar();
    }

    public override void Stop()
    {
        Session.UserVariable.VariableChange -= AlCambiarUsuario;
    }

    [ExportMethod]
    public void Ingresar()
    {
        var usuario = Owner.Get<TextBox>("Login/Tarjeta/Usuario");
        var clave = Owner.Get<TextBox>("Login/Tarjeta/Clave");
        string nombre = (usuario?.Text ?? "").Trim();
        string contrasena = clave?.Text ?? "";

        if (nombre == "" || contrasena == "")
        {
            Mensaje("Ingrese usuario y contraseña.");
            return;
        }

        var resultado = Session.ChangeUser(nombre, contrasena);
        if (resultado.ResultCode != ChangeUserResultCode.Success)
        {
            Mensaje(resultado.ResultCode == ChangeUserResultCode.LoginAttemptBlocked
                ? "Demasiados intentos. Espere e intente de nuevo."
                : "Usuario o contraseña incorrectos.");
            if (clave != null)
                clave.Text = "";
            return;
        }

        if (clave != null)
            clave.Text = "";
        Mensaje("");
        Log.Info("SesionUsuario", "Inicio de sesión: " + nombre);
    }

    [ExportMethod]
    public void Salir()
    {
        string nombre = Session.User?.BrowseName ?? "";
        Session.Logout();

        // Vuelve a la pantalla principal para que el próximo usuario no herede la página abierta
        var cargador = Owner.Get<PanelLoader>("MainPanelLoader");
        var principal = Project.Current.Get("UI/Screens/Principal");
        if (cargador != null && principal != null)
            cargador.ChangePanel(principal.NodeId, NodeId.Empty);

        Log.Info("SesionUsuario", "Cierre de sesión: " + nombre);
    }

    private void AlCambiarUsuario(object sender, VariableChangeEventArgs e) => Actualizar();

    private void Actualizar()
    {
        var usuario = Session.User;
        bool anonimo = usuario == null || usuario.BrowseName == Anonimo;

        var login = Owner.Get<Item>("Login");
        if (login != null)
            login.Visible = anonimo;

        var nombre = Owner.Get<Label>("Cabecera/Usr");
        if (nombre != null)
            nombre.Text = anonimo ? "" : usuario.BrowseName;

        var rol = Owner.Get<Label>("Cabecera/Turno");
        if (rol != null)
            rol.Text = anonimo ? "" : (GestionUsuarios.EsAdministrador(usuario) ? "Administrador" : "Operario");
    }

    private void Mensaje(string texto)
    {
        var etiqueta = Owner.Get<Label>("Login/Tarjeta/Mensaje");
        if (etiqueta != null)
            etiqueta.Text = texto;
    }
}
