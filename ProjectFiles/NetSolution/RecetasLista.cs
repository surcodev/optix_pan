#region Using directives
using System;
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
    [ExportMethod]
    public void Editar() => Llamar("Editar");

    [ExportMethod]
    public void Eliminar() => Llamar("Eliminar");

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
