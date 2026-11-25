#region Using directives
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using FTOptix.Core;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Store;
using FTOptix.UI;
using FTOptix.WebUI;
using UAManagedCore;
#endregion

/// <summary>
/// Pantalla Historial: elige fecha y hora, y muestra los gráficos y la tabla del registro del proceso
/// guardado en la base de datos. También descarga esos datos en Excel.
/// Vive en la pantalla: cada usuario elige su propio intervalo. Todas las fechas son locales.
/// </summary>
public class HistorialProceso : BaseNetLogic
{
    private const string Tabla = "RegistroProceso";
    private const string CarpetaDescargas = "Descargas";
    private const int HorasMaximas = 24;
    private const int Celdas = 42;

    private static readonly string[] Meses = { "Enero", "Febrero", "Marzo", "Abril", "Mayo", "Junio", "Julio", "Agosto", "Septiembre", "Octubre", "Noviembre", "Diciembre" };
    private static readonly Color Azul = new Color(0xFF003478), Blanco = new Color(0xFFFFFFFF), Oscuro = new Color(0xFF1F2F3D), Claro = new Color(0xFFCFE9F7);

    // Columna de la base y título en Excel
    private static readonly (string Columna, string Titulo)[] Columnas =
    {
        ("LocalTimestamp", "Fecha y hora"), ("M1", "M1 Tamiz"), ("M2", "M2 Mezclador"), ("SC1", "SC1 Producto"), ("V1", "V1 Descarga"), ("SI1", "SI1 Manguera"),
        ("CronoM2", "Cronómetro M2 (min)"), ("CronoM3", "Cronómetro M3 (min)"), ("CronoM4", "Cronómetro M4 (min)"),
    };

    private Trend[] graficos;
    private Calendario calDesde, calHasta;
    private DateTime desde, hasta;
    private DataGrid tabla;
    private UISession sesionWeb;
    private int registrosDescarga;
    private bool descargando;
    private Store store;
    private DelayedTask apertura;
    private LongRunningTask descarga;

    public override void Start()
    {
        var pagina = Owner.Owner;
        graficos = new[] { pagina.Get<Trend>("Digital/Grafico"), pagina.Get<Trend>("Cronos/Grafico") };
        calDesde = new Calendario(pagina.Get("Fechas/Desde"));
        calHasta = new Calendario(pagina.Get("Fechas/Hasta"));
        store = Project.Current.Get<Store>("DataStores/DB_SQLSERVER");

        tabla = pagina.Get<DataGrid>("Datos/GridDatos");
        if (EsWeb())
        {
            sesionWeb = (UISession)Session;
            sesionWeb.DownloadFileStarted += AlIniciarDescarga;
        }
        Ultimas(1);
    }

    public override void Stop()
    {
        if (sesionWeb != null)
            sesionWeb.DownloadFileStarted -= AlIniciarDescarga;
        apertura?.Dispose();
        descarga?.Dispose();
    }

    #region Barra
    /// <summary>Abre o cierra el calendario.</summary>
    [ExportMethod]
    public void Fechas()
    {
        if ((bool)LogicObject.GetVariable("Abierto").Value)
        {
            Cerrar();
            return;
        }

        calDesde.Poner(desde);
        calHasta.Poner(hasta);
        Escribir("Aviso", "");
        Escribir("Abierto", true);
    }
    #endregion

    #region Calendario
    /// <summary>lado: 0 Desde, 1 Hasta.</summary>
    [ExportMethod]
    public void Dia(int lado, int celda)
    {
        Lado(lado).Elegir(celda);

        // Al elegir el inicio, el fin pasa al mismo día si quedó antes o más de un día después
        if (lado == 0 && (calHasta.Fecha < calDesde.Fecha || calHasta.Fecha > calDesde.Fecha.AddDays(1)))
            calHasta.PonerDia(calDesde.Fecha);
    }

    [ExportMethod]
    public void Mes(int lado, int cambio) => Lado(lado).CambiarMes(cambio);

    [ExportMethod]
    public void Hora(int lado, int cambio) => Lado(lado).CambiarHora(cambio, 0);

    [ExportMethod]
    public void Minuto(int lado, int cambio) => Lado(lado).CambiarHora(0, cambio);

    [ExportMethod]
    public void Cerrar() => Escribir("Abierto", false);

    /// <summary>Muestra lo elegido en el calendario.</summary>
    [ExportMethod]
    public void Ver()
    {
        if (!calDesde.Leer(out var inicio) || !calHasta.Leer(out var fin))
            Escribir("Aviso", "La hora va de 0 a 23 y los minutos, de 0 a 59.");
        else if (fin <= inicio)
            Escribir("Aviso", "Hasta debe ser posterior a Desde.");
        else if (fin - inicio > TimeSpan.FromHours(HorasMaximas))
            Escribir("Aviso", "El máximo es de " + HorasMaximas + " horas.");
        else
            Mostrar(inicio, fin);
    }

    [ExportMethod]
    public void Ultimas(int horas)
    {
        var fin = DateTime.Now;
        Mostrar(fin.AddHours(-horas), fin);
    }

    /// <summary>Día completo: 0 hoy, 1 ayer.</summary>
    [ExportMethod]
    public void DiaCompleto(int atras)
    {
        var inicio = DateTime.Today.AddDays(-atras);
        Mostrar(inicio, atras == 0 ? DateTime.Now : inicio.AddDays(1));
    }

    private Calendario Lado(int lado) => lado == 1 ? calHasta : calDesde;
    #endregion

    private void Mostrar(DateTime inicio, DateTime fin)
    {
        desde = inicio;
        hasta = fin;
        Cerrar();

        foreach (var grafico in graficos)
        {
            grafico.XAxis.Window = 1000;
            grafico.XAxis.Time = inicio;
        }

        // Cambiar inicio y duración a la vez deja tramos sin cargar en la vista web:
        // primero se mueve el inicio y después se abre la ventana
        uint duracion = (uint)(fin - inicio).TotalMilliseconds;
        apertura?.Dispose();
        apertura = new DelayedTask(() =>
        {
            foreach (var grafico in graficos)
                grafico.XAxis.Window = duracion;
        }, 400, LogicObject);
        apertura.Start();

        tabla.Query = Consulta(inicio, fin);
        Escribir("TextoFechas", Txt(inicio) + "  a  " + Txt(fin) + "  ▼");
        try
        {
            long filas = Convert.ToInt64(Consultar($"SELECT COUNT(*) FROM \"{Tabla}\" WHERE {Entre(inicio, fin)}")[0, 0]);
            if (filas > 0)
                Texto(Miles(filas) + " registros.");
            else
                Texto("No hay registros en ese intervalo. " + DatosGuardados(), true);
        }
        catch (Exception e)
        {
            Texto("No se pudo consultar la base de datos.", true);
            Log.Error("HistorialProceso", e.Message);
        }
    }

    private string DatosGuardados()
    {
        var limites = Consultar($"SELECT MIN(\"LocalTimestamp\"), MAX(\"LocalTimestamp\") FROM \"{Tabla}\"");
        if (limites[0, 0] == null)
            return "La base de datos está vacía.";
        return "Hay datos entre el " + Txt(Fecha(limites[0, 0])) + " y el " + Txt(Fecha(limites[0, 1])) + ".";
    }

    #region Excel
    /// <summary>Descarga en Excel los registros del intervalo mostrado.</summary>
    [ExportMethod]
    public void Excel()
    {
        if (descargando)
            return;

        descargando = true;
        DateTime inicio = desde, fin = hasta;
        Texto("Preparando el archivo de Excel...");
        descarga?.Dispose();
        descarga = new LongRunningTask(() => Descargar(inicio, fin), LogicObject);
        descarga.Start();
    }

    private void Descargar(DateTime inicio, DateTime fin)
    {
        try
        {
            var filas = Consultar(Consulta(inicio, fin));
            int total = filas.GetLength(0);
            if (total == 0)
            {
                Texto("No hay registros para descargar en ese intervalo.", true);
                return;
            }

            string nombre = "Historial_" + inicio.ToString("yyyyMMdd_HHmm") + "_a_" + fin.ToString("yyyyMMdd_HHmm") + "_" + DateTime.Now.ToString("HHmmss");
            if (sesionWeb != null)
            {
                // La vista web solo entrega archivos que estén dentro de ProjectFiles
                string carpeta = ResourceUri.FromProjectRelativePath(CarpetaDescargas).Uri;
                Directory.CreateDirectory(carpeta);
                BorrarAntiguos(carpeta);
                EscribirExcel(Path.Combine(carpeta, nombre + ".xlsx"), filas, inicio, fin);
                registrosDescarga = total;
                sesionWeb.DownloadFile(ResourceUri.FromProjectRelativePath(CarpetaDescargas + "/" + nombre + ".xlsx"), nombre);
            }
            else
            {
                string carpeta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                Directory.CreateDirectory(carpeta);
                string archivo = Path.Combine(carpeta, nombre + ".xlsx");
                EscribirExcel(archivo, filas, inicio, fin);
                Texto("Excel guardado en la carpeta Descargas de Windows: " + Miles(total) + " registros.");
                if (OperatingSystem.IsWindows())
                    Process.Start("explorer.exe", "/select,\"" + archivo + "\"");
            }
        }
        catch (Exception e)
        {
            Texto("No se pudo crear el archivo de Excel.", true);
            Log.Error("HistorialProceso", "Excel: " + e.Message);
        }
        finally
        {
            descargando = false;
        }
    }

    private void AlIniciarDescarga(object sender, DownloadFileStartedEvent e)
    {
        if (e.Result == DownloadFileStartedResultCode.Success)
        {
            Texto("Excel descargado: " + Miles(registrosDescarga) + " registros.");
            return;
        }

        Texto("No se pudo descargar el archivo de Excel.", true);
        Log.Error("HistorialProceso", "Descarga rechazada: " + e.Result);
    }

    private bool EsWeb()
    {
        for (IUANode nodo = LogicObject; nodo != null; nodo = nodo.Owner)
        {
            if (nodo is WebUIPresentationEngine)
                return true;
        }
        return false;
    }

    private static void BorrarAntiguos(string carpeta)
    {
        foreach (var archivo in Directory.GetFiles(carpeta, "Historial_*.xlsx"))
        {
            try
            {
                if (File.GetLastWriteTime(archivo) < DateTime.Now.AddHours(-1))
                    File.Delete(archivo);
            }
            catch (IOException)
            {
                // Sigue en uso: se borrará en otra descarga
            }
        }
    }

    /// <summary>Un .xlsx es un ZIP con archivos XML: no hace falta tener Excel instalado.</summary>
    private static void EscribirExcel(string archivo, object[,] filas, DateTime inicio, DateTime fin)
    {
        const string Xml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";
        const string Hoja = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string Paquete = "http://schemas.openxmlformats.org/package/2006/";
        const string Tipo = "application/vnd.openxmlformats-officedocument.spreadsheetml.";
        int total = filas.GetLength(0), columnas = Columnas.Length;
        char ultima = (char)('A' + columnas - 1);

        using var zip = ZipFile.Open(archivo, ZipArchiveMode.Create);
        Parte(zip, "[Content_Types].xml", Xml + $"<Types xmlns=\"{Paquete}content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            $"<Override PartName=\"/xl/workbook.xml\" ContentType=\"{Tipo}sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"{Tipo}worksheet+xml\"/>" +
            $"<Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"{Tipo}worksheet+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"{Tipo}styles+xml\"/></Types>");
        Parte(zip, "_rels/.rels", Xml + $"<Relationships xmlns=\"{Paquete}relationships\"><Relationship Id=\"rId1\" Type=\"{Rel}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        Parte(zip, "xl/_rels/workbook.xml.rels", Xml + $"<Relationships xmlns=\"{Paquete}relationships\"><Relationship Id=\"rId1\" Type=\"{Rel}/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            $"<Relationship Id=\"rId2\" Type=\"{Rel}/worksheet\" Target=\"worksheets/sheet2.xml\"/><Relationship Id=\"rId3\" Type=\"{Rel}/styles\" Target=\"styles.xml\"/></Relationships>");
        Parte(zip, "xl/workbook.xml", Xml + $"<workbook xmlns=\"{Hoja}\" xmlns:r=\"{Rel}\"><sheets><sheet name=\"Proceso\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Información\" sheetId=\"2\" r:id=\"rId2\"/></sheets>" +
            $"<definedNames><definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"0\" hidden=\"1\">Proceso!$A$1:${ultima}${total + 1}</definedName></definedNames></workbook>");

        // Estilos: 0 normal, 1 encabezado, 2 fecha y hora, 3 dos decimales
        Parte(zip, "xl/styles.xml", Xml + $"<styleSheet xmlns=\"{Hoja}\"><numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"dd/mm/yyyy\\ hh:mm:ss\"/></numFmts>" +
            "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/></font></fonts>" +
            "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF003478\"/></patternFill></fill></fills>" +
            "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"4\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\"/></xf>" +
            "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/><xf numFmtId=\"2\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/></cellXfs>" +
            "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");

        using (var salida = new StreamWriter(zip.CreateEntry("xl/worksheets/sheet1.xml").Open(), new UTF8Encoding(false)))
        {
            salida.Write(Xml + $"<worksheet xmlns=\"{Hoja}\"><sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>" +
                "<cols><col min=\"1\" max=\"1\" width=\"20\" customWidth=\"1\"/><col min=\"2\" max=\"6\" width=\"16\" customWidth=\"1\"/><col min=\"7\" max=\"9\" width=\"24\" customWidth=\"1\"/></cols><sheetData><row>");
            foreach (var c in Columnas)
                salida.Write(Celda(c.Titulo, 1));
            salida.Write("</row>");
            for (int f = 0; f < total; f++)
            {
                salida.Write("<row>" + Numero(Fecha(filas[f, 0]).ToOADate(), 2));
                for (int c = 1; c < columnas; c++)
                    salida.Write(filas[f, c] == null ? "<c/>" : Numero(Convert.ToDouble(filas[f, c], CultureInfo.InvariantCulture), c > 5 ? 3 : 0));
                salida.Write("</row>");
            }
            salida.Write($"</sheetData><autoFilter ref=\"A1:{ultima}{total + 1}\"/></worksheet>");
        }

        Parte(zip, "xl/worksheets/sheet2.xml", Xml + $"<worksheet xmlns=\"{Hoja}\"><cols><col min=\"1\" max=\"1\" width=\"22\" customWidth=\"1\"/><col min=\"2\" max=\"2\" width=\"64\" customWidth=\"1\"/></cols><sheetData>" +
            "<row>" + Celda("Mezcladora Marion", 1) + Celda("Registro del proceso", 1) + "</row>" +
            "<row>" + Celda("Desde", 0) + Celda(Txt(inicio), 0) + "</row><row>" + Celda("Hasta", 0) + Celda(Txt(fin), 0) + "</row>" +
            "<row>" + Celda("Registros", 0) + Celda(Miles(total), 0) + "</row><row>" + Celda("Generado", 0) + Celda(Txt(DateTime.Now), 0) + "</row>" +
            "<row>" + Celda("Valores", 0) + Celda("1 = encendido, abierto o detecta. 0 = apagado, cerrado o no detecta.", 0) + "</row></sheetData></worksheet>");
    }

    private static void Parte(ZipArchive zip, string nombre, string contenido)
    {
        using var salida = new StreamWriter(zip.CreateEntry(nombre).Open(), new UTF8Encoding(false));
        salida.Write(contenido);
    }

    private static string Celda(string texto, int estilo) => "<c t=\"inlineStr\" s=\"" + estilo + "\"><is><t>" + texto + "</t></is></c>";

    private static string Numero(double valor, int estilo) => "<c s=\"" + estilo + "\"><v>" + valor.ToString("R", CultureInfo.InvariantCulture) + "</v></c>";
    #endregion

    private object[,] Consultar(string sql)
    {
        store.Query(sql, out _, out object[,] filas);
        return filas;
    }

    private static string Consulta(DateTime inicio, DateTime fin) =>
        "SELECT " + string.Join(", ", Array.ConvertAll(Columnas, c => "\"" + c.Columna + "\"")) + $" FROM \"{Tabla}\" WHERE {Entre(inicio, fin)} ORDER BY \"LocalTimestamp\"";

    private static string Entre(DateTime inicio, DateTime fin) => $"\"LocalTimestamp\" BETWEEN \"{Sql(inicio)}\" AND \"{Sql(fin)}\"";

    private static string Sql(DateTime fecha) => fecha.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static DateTime Fecha(object valor) => valor is DateTime f ? f : DateTime.Parse(Convert.ToString(valor, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static string Txt(DateTime fecha) => fecha.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    private static string Miles(long numero) => numero.ToString("N0", CultureInfo.InvariantCulture).Replace(',', ' ');

    private void Escribir(string nombre, object valor) => LogicObject.GetVariable(nombre).Value = new UAValue(valor);

    private void Texto(string texto, bool error = false)
    {
        Escribir("EsError", error);
        Escribir("Texto", texto);
    }

    /// <summary>Un mes con sus botones de día, la hora y los minutos.</summary>
    private sealed class Calendario
    {
        private readonly Button[] dias = new Button[Celdas];
        private readonly Label titulo;
        private readonly TextBox hora, minuto;
        private DateTime mes;

        public Calendario(IUANode panel)
        {
            titulo = panel.Get<Label>("Mes");
            hora = panel.Get<TextBox>("Hora");
            minuto = panel.Get<TextBox>("Minuto");
            for (int i = 0; i < Celdas; i++)
                dias[i] = panel.Get<Button>("Dia" + i);
        }

        /// <summary>Día elegido, sin hora.</summary>
        public DateTime Fecha { get; private set; }

        public void Poner(DateTime valor)
        {
            hora.Text = valor.Hour.ToString("00");
            minuto.Text = valor.Minute.ToString("00");
            PonerDia(valor);
        }

        public void PonerDia(DateTime valor)
        {
            Fecha = valor.Date;
            mes = new DateTime(valor.Year, valor.Month, 1);
            Dibujar();
        }

        public void Elegir(int celda)
        {
            var dia = Primero().AddDays(celda);
            if (dia.Month == mes.Month)
                PonerDia(dia);
        }

        public void CambiarMes(int cambio)
        {
            mes = mes.AddMonths(Math.Sign(cambio));
            Dibujar();
        }

        public void CambiarHora(int horas, int minutos)
        {
            int.TryParse(hora.Text, out int h);
            int.TryParse(minuto.Text, out int m);
            hora.Text = ((h + horas + 24) % 24).ToString("00");
            minuto.Text = ((m + minutos + 60) % 60).ToString("00");
        }

        public bool Leer(out DateTime valor)
        {
            bool numeros = int.TryParse(hora.Text, out int h) & int.TryParse(minuto.Text, out int m);
            bool ok = numeros && h >= 0 && h < 24 && m >= 0 && m < 60;
            valor = ok ? Fecha.AddHours(h).AddMinutes(m) : default;
            return ok;
        }

        // Lunes de la primera semana que se dibuja
        private DateTime Primero() => mes.AddDays(-(((int)mes.DayOfWeek + 6) % 7));

        private void Dibujar()
        {
            titulo.Text = Meses[mes.Month - 1] + " " + mes.Year;
            var dia = Primero();
            foreach (var boton in dias)
            {
                bool elegido = dia == Fecha;
                boton.Text = dia.Day.ToString();
                boton.Visible = dia.Month == mes.Month;
                boton.BackgroundColor = elegido ? Azul : dia == DateTime.Today ? Claro : Blanco;
                boton.TextColor = elegido ? Blanco : Oscuro;
                dia = dia.AddDays(1);
            }
        }
    }
}
