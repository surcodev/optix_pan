using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

/// <summary>
/// Lee recetas desde la primera hoja de un .xlsx (un ZIP con XML) o desde un .csv guardado con Excel, sin librerías.
/// La fila 1 tiene los encabezados; cada fila siguiente es una receta. Deben estar todas las columnas de la
/// plantilla y ninguna más (en cualquier orden); si no, no se importa nada. Los encabezados se reconocen
/// sin importar mayúsculas, tildes ni signos, y también por el nombre del parámetro (p. ej. Mezcla_TiempoTotalMin).
/// </summary>
internal static class ImportadorRecetas
{
    public const string Codigo = "Codigo";

    private enum Tipo { Texto, SiNo, Entero, Minutos, Categoria }

    // Parámetro del esquema, tipo y encabezado de la plantilla
    private static readonly (string Parametro, Tipo Tipo, string Encabezado)[] Columnas =
    {
        (Codigo, Tipo.Texto, "Código"),
        ("NombreReceta", Tipo.Texto, "Nombre"),
        ("Carga_HabilitarM2", Tipo.SiNo, "Habilitar M2"),
        ("Carga_TiempoSeg", Tipo.Entero, "Tiempo de carga (s)"),
        ("Carga_ModoOperador", Tipo.SiNo, "Modo operador"),
        ("Mezcla_TiempoTotalMin", Tipo.Entero, "Mezcla total (min)"),
        ("NoTam_Categoria", Tipo.Categoria, "Categoría"),
        ("NoTam_MinInicio", Tipo.Minutos, "Inicio no tamizado (min)"),
        ("NoTam_MinConfirmacion", Tipo.Minutos, "Confirmación no tamizado (min)"),
        ("NoTam_MinFinCronometro", Tipo.Minutos, "Fin cronómetro (min)"),
        ("Chopper_MinEncendido", Tipo.Minutos, "Chopper encendido desde (min)"),
        ("Chopper_MinutosUso", Tipo.Minutos, "Chopper uso (min)"),
        ("Descarga_TiempoMinMin", Tipo.Minutos, "Descarga mínima (min)"),
    };

    private static readonly string[] Categorias = { "nada", "manteca", "semillas", "aceite", "galletas" };

    /// <summary>Encabezados de la plantilla, en orden.</summary>
    public static IEnumerable<string> Encabezados => Columnas.Select(c => c.Encabezado);

    public sealed class Fila
    {
        public int Numero;
        public string Codigo;
        public Dictionary<string, object> Valores = new Dictionary<string, object>();
        public string Error;
    }

    /// <summary>
    /// Recetas del archivo (.xlsx o .csv). Una fila con datos inválidos trae Error y se informa sin detener el resto.
    /// </summary>
    public static List<Fila> Leer(string archivo)
    {
        var hoja = string.Equals(Path.GetExtension(archivo), ".csv", StringComparison.OrdinalIgnoreCase) ? LeerCsv(archivo) : LeerHoja(archivo);
        if (hoja.Count == 0)
            throw new InvalidOperationException("El Excel está vacío.");

        // Encabezados: la fila 1 debe tener exactamente las columnas de la plantilla (en cualquier orden).
        // Si no coinciden no se importa nada.
        var encabezados = hoja.First();
        if (encabezados.Numero != 1)
            throw new InvalidOperationException("La fila 1 debe tener los encabezados de la plantilla.");
        var posiciones = new Dictionary<int, (string Parametro, Tipo Tipo)>();
        var sobran = new List<string>();
        var repetidas = new List<string>();
        foreach (var celda in encabezados.Celdas.Where(c => !string.IsNullOrWhiteSpace(c.Value)).OrderBy(c => c.Key))
        {
            string clave = Normalizar(celda.Value);
            var columna = Columnas.FirstOrDefault(c => Normalizar(c.Encabezado) == clave || Normalizar(c.Parametro) == clave);
            if (columna.Parametro == null)
                sobran.Add(celda.Value.Trim());
            else if (posiciones.Values.Any(p => p.Parametro == columna.Parametro))
                repetidas.Add(celda.Value.Trim());
            else
                posiciones[celda.Key] = (columna.Parametro, columna.Tipo);
        }
        var faltan = Columnas.Where(c => !posiciones.Values.Any(p => p.Parametro == c.Parametro)).Select(c => c.Encabezado).ToList();
        if (faltan.Count > 0 || sobran.Count > 0 || repetidas.Count > 0)
        {
            var partes = new List<string>();
            if (faltan.Count > 0)
                partes.Add("faltan " + Lista(faltan));
            if (sobran.Count > 0)
                partes.Add("no se reconocen " + Lista(sobran));
            if (repetidas.Count > 0)
                partes.Add("están repetidas " + Lista(repetidas));
            throw new InvalidOperationException("Los encabezados no coinciden con la plantilla: " + string.Join("; ", partes) + ". No se importó nada.");
        }

        var filas = new List<Fila>();
        foreach (var r in hoja.Skip(1))
        {
            if (r.Celdas.Values.All(string.IsNullOrWhiteSpace))
                continue;

            var fila = new Fila { Numero = r.Numero };
            foreach (var (indice, columna) in posiciones)
            {
                r.Celdas.TryGetValue(indice, out string texto);
                texto = (texto ?? "").Trim();
                try
                {
                    if (columna.Parametro == Codigo)
                        fila.Codigo = texto;
                    else if (texto.Length > 0 || columna.Tipo == Tipo.Texto)
                        fila.Valores[columna.Parametro] = Convertir(texto, columna.Tipo);
                }
                catch (FormatException)
                {
                    fila.Error ??= "valor no válido en \"" + Columnas.First(c => c.Parametro == columna.Parametro).Encabezado + "\": " + texto;
                }
            }
            if (string.IsNullOrEmpty(fila.Codigo))
                fila.Error ??= "falta el código.";
            filas.Add(fila);
        }
        return filas;
    }

    private static object Convertir(string texto, Tipo tipo)
    {
        switch (tipo)
        {
            case Tipo.Texto:
                return texto;
            case Tipo.SiNo:
                switch (Normalizar(texto))
                {
                    case "si": case "s": case "1": case "true": case "verdadero": case "x": return true;
                    case "no": case "n": case "0": case "false": case "falso": return false;
                }
                throw new FormatException();
            case Tipo.Entero:
                double entero = Numero(texto);
                if (entero < 0 || entero != Math.Round(entero))
                    throw new FormatException();
                return (int)entero;
            case Tipo.Categoria:
                int categoria = Array.IndexOf(Categorias, Normalizar(texto));
                if (categoria < 0)
                {
                    double valor = Numero(texto);
                    if (valor < 0 || valor > 4 || valor != Math.Round(valor))
                        throw new FormatException();
                    categoria = (int)valor;
                }
                return categoria;
            default:
                // Minutos decimales (7.5) o minutos:segundos (7:30)
                double minutos;
                var partes = texto.Split(':');
                if (partes.Length == 2 && int.TryParse(partes[0], out int m) && int.TryParse(partes[1], out int s) && s >= 0 && s < 60)
                    minutos = m + s / 60.0;
                else
                    minutos = Numero(texto);
                if (minutos < 0)
                    throw new FormatException();
                return (float)minutos;
        }
    }

    private static string Lista(List<string> nombres) => string.Join(", ", nombres.Select(n => "\"" + n + "\""));

    private static double Numero(string texto)
    {
        if (double.TryParse(texto.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double valor))
            return valor;
        throw new FormatException();
    }

    private static string Normalizar(string texto)
    {
        var sb = new StringBuilder();
        foreach (char c in (texto ?? "").Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    #region Lectura del .xlsx
    private sealed class FilaHoja
    {
        public int Numero;
        public Dictionary<int, string> Celdas = new Dictionary<int, string>();
    }

    private static readonly XNamespace Hoja = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Paquete = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>Filas de la primera hoja, con el texto de cada celda por número de columna (A = 0).</summary>
    private static List<FilaHoja> LeerHoja(string archivo)
    {
        // Se permite leer con el archivo abierto en Excel
        using var flujo = new FileStream(archivo, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(flujo, ZipArchiveMode.Read);

        var libro = Xml(zip, "xl/workbook.xml") ?? throw new InvalidOperationException("El archivo no es un Excel .xlsx válido.");
        string id = (string)libro.Descendants(Hoja + "sheet").First().Attribute(Rel + "id");
        string destino = Xml(zip, "xl/_rels/workbook.xml.rels")?.Descendants(Paquete + "Relationship")
            .FirstOrDefault(r => (string)r.Attribute("Id") == id)?.Attribute("Target")?.Value ?? "worksheets/sheet1.xml";
        string ruta = destino.StartsWith("/") ? destino.TrimStart('/') : "xl/" + destino;

        var compartidos = Xml(zip, "xl/sharedStrings.xml")?.Root.Elements(Hoja + "si")
            .Select(si => string.Concat(si.Descendants(Hoja + "t").Select(t => t.Value))).ToList() ?? new List<string>();

        var hoja = Xml(zip, ruta) ?? throw new InvalidOperationException("No se encontró la primera hoja del Excel.");
        var filas = new List<FilaHoja>();
        int siguiente = 1;
        foreach (var row in hoja.Descendants(Hoja + "row"))
        {
            var fila = new FilaHoja { Numero = int.TryParse((string)row.Attribute("r"), out int n) ? n : siguiente };
            siguiente = fila.Numero + 1;
            int columna = 0;
            foreach (var c in row.Elements(Hoja + "c"))
            {
                string referencia = (string)c.Attribute("r");
                if (referencia != null)
                    columna = Columna(referencia);
                string tipo = (string)c.Attribute("t");
                string valor = (string)c.Element(Hoja + "v");
                string texto = tipo switch
                {
                    "s" => int.TryParse(valor, out int i) && i < compartidos.Count ? compartidos[i] : "",
                    "inlineStr" => string.Concat(c.Descendants(Hoja + "t").Select(t => t.Value)),
                    _ => valor ?? "",
                };
                fila.Celdas[columna++] = texto;
            }
            filas.Add(fila);
        }
        return filas;
    }

    /// <summary>
    /// CSV guardado desde Excel: separador ; o , (el que aparezca en los encabezados), comillas dobles,
    /// UTF-8 (con o sin BOM) o ANSI de Windows.
    /// </summary>
    private static List<FilaHoja> LeerCsv(string archivo)
    {
        byte[] bytes;
        using (var flujo = new FileStream(archivo, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var memoria = new MemoryStream())
        {
            flujo.CopyTo(memoria);
            bytes = memoria.ToArray();
        }

        string texto;
        try
        {
            texto = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            texto = Encoding.Latin1.GetString(bytes);
        }
        texto = texto.TrimStart('﻿');

        int finPrimera = texto.IndexOfAny(new[] { '\r', '\n' });
        string primera = finPrimera < 0 ? texto : texto.Substring(0, finPrimera);
        char separador = primera.Count(c => c == ';') >= primera.Count(c => c == ',') ? ';' : ',';

        var filas = new List<FilaHoja>();
        var fila = new FilaHoja { Numero = 1 };
        var celda = new StringBuilder();
        bool comillas = false;
        int columna = 0, numero = 1;

        void CerrarCelda()
        {
            fila.Celdas[columna++] = celda.ToString();
            celda.Clear();
        }

        for (int i = 0; i < texto.Length; i++)
        {
            char c = texto[i];
            if (comillas)
            {
                if (c == '"' && i + 1 < texto.Length && texto[i + 1] == '"')
                {
                    celda.Append('"');
                    i++;
                }
                else if (c == '"')
                    comillas = false;
                else
                    celda.Append(c);
            }
            else if (c == '"')
                comillas = true;
            else if (c == separador)
                CerrarCelda();
            else if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < texto.Length && texto[i + 1] == '\n')
                    i++;
                CerrarCelda();
                filas.Add(fila);
                fila = new FilaHoja { Numero = ++numero };
                columna = 0;
            }
            else
                celda.Append(c);
        }
        if (celda.Length > 0 || columna > 0)
        {
            CerrarCelda();
            filas.Add(fila);
        }
        return filas;
    }

    private static XDocument Xml(ZipArchive zip, string nombre)
    {
        var entrada = zip.GetEntry(nombre);
        if (entrada == null)
            return null;
        using var flujo = entrada.Open();
        return XDocument.Load(flujo);
    }

    private static int Columna(string referencia)
    {
        int columna = 0;
        foreach (char c in referencia)
        {
            if (!char.IsLetter(c))
                break;
            columna = columna * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        }
        return columna - 1;
    }
    #endregion
}
