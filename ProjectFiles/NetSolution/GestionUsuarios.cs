#region Using directives
using System;
using System.Linq;
using FTOptix.Core;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using UAManagedCore;
#endregion

/// <summary>
/// Crea al arrancar los grupos y usuarios de la aplicación si no existen:
/// admin/admin (grupo Administradores) y operario/operario(grupo Operarios).
/// La contraseña solo se asigna al crear el usuario, así un cambio posterior no se pisa.
/// </summary>
public class GestionUsuarios : BaseNetLogic
{
    public const string GrupoAdministradores = "Administradores";
    public const string GrupoOperarios = "Operarios";

    public override void Start()
    {
        var grupos = Project.Current.Get("Security/Groups");
        var usuarios = Project.Current.Get("Security/Users");
        if (grupos == null || usuarios == null)
        {
            Log.Error("GestionUsuarios", "No se encontraron las carpetas Security/Groups o Security/Users.");
            return;
        }

        var administradores = AsegurarGrupo(grupos, GrupoAdministradores);
        var operarios = AsegurarGrupo(grupos, GrupoOperarios);

        AsegurarUsuario(usuarios, "admin", "admin", administradores);
        AsegurarUsuario(usuarios, "operario", "operario", operarios);
    }

    private static IUANode AsegurarGrupo(IUANode carpeta, string nombre)
    {
        var grupo = carpeta.Get(nombre);
        if (grupo != null)
            return grupo;

        grupo = InformationModel.MakeObject<Group>(nombre);
        carpeta.Add(grupo);
        Log.Info("GestionUsuarios", "Grupo creado: " + nombre);
        return grupo;
    }

    private void AsegurarUsuario(IUANode carpeta, string nombre, string clave, IUANode grupo)
    {
        var usuario = carpeta.Get<User>(nombre);
        if (usuario == null)
        {
            usuario = InformationModel.MakeObject<User>(nombre);
            carpeta.Add(usuario);

            var resultado = Session.ChangePassword(nombre, clave, string.Empty);
            if (resultado.ResultCode == ChangePasswordResultCode.Success)
                Log.Info("GestionUsuarios", "Usuario creado: " + nombre);
            else
                Log.Error("GestionUsuarios", "No se pudo asignar la contraseña de " + nombre + ": " + resultado.ResultCode);
        }

        bool enGrupo = usuario.Refs.GetObjects(FTOptix.Core.ReferenceTypes.HasGroup, false)
            .Any(g => g.NodeId == grupo.NodeId);
        if (!enGrupo)
            usuario.Refs.AddReference(FTOptix.Core.ReferenceTypes.HasGroup, grupo);
    }

    /// <summary>Indica si el usuario pertenece al grupo Administradores.</summary>
    public static bool EsAdministrador(IUANode usuario)
    {
        if (usuario == null)
            return false;
        return usuario.Refs.GetObjects(FTOptix.Core.ReferenceTypes.HasGroup, false)
            .Any(g => g.BrowseName == GrupoAdministradores);
    }
}
