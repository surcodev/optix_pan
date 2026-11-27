using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

/// <summary>
/// PDF de una página (A4 vertical) con los dos gráficos de la pantalla Historial, uno debajo del otro,
/// dibujados igual que los Trend: mismas plumas, colores, escalas y carriles.
/// Se escribe a mano, sin librerías: fuentes estándar Helvetica y dibujo vectorial.
/// Las filas tienen las columnas de HistorialProceso: fecha, M1, M2, SC1, V1, SI1, CronoM2, CronoM3, CronoM4.
/// </summary>
internal static class HistorialPdf
{
    private const float Ancho = 595f, Alto = 842f, Margen = 42f;
    private const string Azul = "#003478", Gris = "#6b7a87", Oscuro = "#1f2f3d", Grilla = "#d9dee3", Marco = "#c9d6e0", Eje = "#8a97a3";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Igual que Digital/Grafico: cada señal en su carril (T_*), arriba = activa (SimuladorProceso.Carril)
    private static readonly (int Columna, string Nombre, string Color, int Carril)[] Senales =
    {
        (1, "M1", "#003478", 4), (2, "M2", "#25aae1", 3), (3, "SC1", "#ff9100", 2), (4, "V1", "#1fae66", 1), (5, "SI1", "#7a4fbf", 0),
    };

    // Igual que Cronos/Grafico
    private static readonly (int Columna, string Nombre, string Color)[] Cronos =
    {
        (6, "M2", "#25aae1"), (7, "M3", "#d7263d"), (8, "M4", "#ff9100"),
    };

    private sealed class Serie
    {
        public string Nombre, Color;
        public List<(double X, double V)> Puntos;
    }

    /// <param name="previa">Última fila anterior al intervalo: une la curva con el borde izquierdo, como el Trend. Puede ser null.</param>
    public static void Escribir(string archivo, object[,] filas, object[] previa, DateTime inicio, DateTime fin)
    {
        var l = new Lienzo();
        int total = filas.GetLength(0);
        var tiempos = new DateTime[total];
        for (int f = 0; f < total; f++)
            tiempos[f] = Fecha(filas[f, 0]);
        double segundos = (fin - inicio).TotalSeconds;

        // Encabezado
        l.Texto(Margen, 50, 8.5f, true, "MEZCLADORA MARION", Gris);
        l.Texto(Margen, 74, 19, true, "Historial del proceso", Oscuro);
        l.Texto(Ancho - Margen, 50, 8.5f, false, "Desde  " + Txt(inicio), Oscuro, 1);
        l.Texto(Ancho - Margen, 63, 8.5f, false, "Hasta  " + Txt(fin), Oscuro, 1);
        l.Texto(Ancho - Margen, 76, 8.5f, false, Duracion(fin - inicio) + "  ·  " + Miles(total) + " registros", Gris, 1);
        l.Linea(Margen, 90, Ancho - Margen, 90, Azul, 1.2f);

        var marcas = Marcas(inicio, fin);

        // Gráfico 1: señales en carriles
        var digitales = Senales.Select(s => new Serie
        {
            Nombre = s.Nombre,
            Color = s.Color,
            Puntos = Puntos(filas, tiempos, previa, s.Columna, inicio, segundos, v => Activo(v) is bool a ? s.Carril + (a ? 0.7 : 0) : null),
        }).ToList();
        Grafico(l, 108, 330, "Equipos y sensores", "Carriles de abajo hacia arriba: SI1, V1, SC1, M2, M1. Arriba = señal activa.",
            digitales, 5, 1, "0", inicio, fin, marcas);

        // Gráfico 2: cronómetros, escala 0 a 30 como en pantalla salvo que un valor la supere
        var cronos = Cronos.Select(c => new Serie
        {
            Nombre = c.Nombre,
            Color = c.Color,
            Puntos = Puntos(filas, tiempos, previa, c.Columna, inicio, segundos, Numero),
        }).ToList();
        double maximo = cronos.SelectMany(s => s.Puntos).Select(p => p.V).DefaultIfEmpty(0).Max();
        double tope = maximo <= 30 ? 30 : Math.Ceiling(maximo / 10) * 10;
        Grafico(l, 458, 330, "Cronómetros (min)", "Donde no hubo registro, los puntos se unen con una recta.",
            cronos, tope, tope / 6, "0", inicio, fin, marcas);

        // Pie
        l.Linea(Margen, Alto - 40, Ancho - Margen, Alto - 40, Marco, 0.6f);
        l.Texto(Margen, Alto - 28, 7.5f, false, "Generado el " + Txt(DateTime.Now) + "  ·  Horas en tiempo local", Gris);
        l.Texto(Ancho - Margen, Alto - 28, 7.5f, false, "Página 1 de 1", Gris, 1);

        Guardar(archivo, l.Contenido, "Historial del proceso " + Txt(inicio) + " a " + Txt(fin));
    }

    /// <summary>Recuadro con título, leyenda, grilla, curvas y eje de tiempo, al estilo del Trend de Optix.</summary>
    private static void Grafico(Lienzo l, float arriba, float alto, string titulo, string nota, List<Serie> series,
        double tope, double paso, string formato, DateTime inicio, DateTime fin, List<DateTime> marcas)
    {
        float izquierda = Margen, derecha = Ancho - Margen;
        l.Borde(izquierda, arriba, derecha - izquierda, alto, Marco, 0.8f);
        l.Texto(izquierda + 14, arriba + 22, 11, true, titulo, Azul);

        // Leyenda centrada
        const float cuadro = 7, separacion = 14;
        float anchoLeyenda = series.Sum(s => cuadro + 4 + Lienzo.Medir(s.Nombre, 8.5f) + separacion) - separacion;
        float xl = (Ancho - anchoLeyenda) / 2;
        foreach (var s in series)
        {
            l.Rect(xl, arriba + 37, cuadro, cuadro, s.Color);
            l.Texto(xl + cuadro + 4, arriba + 43.5f, 8.5f, false, s.Nombre, Oscuro);
            xl += cuadro + 4 + Lienzo.Medir(s.Nombre, 8.5f) + separacion;
        }

        float x0 = izquierda + 40, x1 = derecha - 16, y0 = arriba + 56, y1 = arriba + alto - 56;
        float X(double fraccion) => x0 + (float)fraccion * (x1 - x0);
        float Y(double v) => y1 - (float)(v / tope) * (y1 - y0);
        float XT(DateTime t) => X((t - inicio).TotalSeconds / (fin - inicio).TotalSeconds);

        // Grilla y escala
        for (double v = 0; v <= tope + paso / 2; v += paso)
        {
            l.Linea(x0, Y(v), x1, Y(v), Grilla, 0.5f);
            l.Texto(x0 - 6, Y(v) + 3, 8, false, v.ToString(formato, Inv), Oscuro, 1);
        }
        foreach (var m in marcas)
            l.Linea(XT(m), y0, XT(m), y1, Grilla, 0.5f);
        l.Linea(x0, y0, x0, y1, Eje, 0.7f);
        l.Linea(x0, y1, x1, y1, Eje, 0.7f);

        // Curvas, en el orden de las plumas
        bool hayDatos = false;
        foreach (var s in series)
        {
            if (s.Puntos.Count == 0)
                continue;
            hayDatos = true;
            var puntos = Reducir(s.Puntos, (int)((x1 - x0) * 2)).Select(p => (X(p.X), Y(p.V))).ToList();
            if (puntos.Count == 1)
                puntos.Add((puntos[0].Item1 + 0.5f, puntos[0].Item2));
            l.Polilinea(puntos, s.Color, 1.3f);
        }
        if (!hayDatos)
            l.Texto((x0 + x1) / 2, (y0 + y1) / 2, 9, false, "Sin datos en el intervalo", Gris, 0.5f);

        // Eje de tiempo: horas en las marcas y fecha en los extremos
        foreach (var m in marcas)
        {
            l.Linea(XT(m), y1, XT(m), y1 + 3, Eje, 0.7f);
            l.Texto(XT(m), y1 + 13, 8, false, m.ToString("HH:mm", Inv), Oscuro, 0.5f);
        }
        l.Texto(x0, y1 + 26, 8, true, inicio.ToString("dd/MM/yyyy", Inv), Oscuro, 0);
        l.Texto(x1, y1 + 26, 8, true, fin.ToString("dd/MM/yyyy", Inv), Oscuro, 1);

        l.Texto(izquierda + 14, arriba + alto - 12, 8, false, nota, Gris);
    }

    /// <summary>Puntos de una columna en fracción del intervalo (0 a 1).</summary>
    private static List<(double X, double V)> Puntos(object[,] filas, DateTime[] tiempos, object[] previa, int columna,
        DateTime inicio, double segundos, Func<object, double?> valor)
    {
        var puntos = new List<(double, double)>();
        if (previa != null && valor(previa[columna]) is double inicial)
            puntos.Add((0, inicial));
        for (int f = 0; f < tiempos.Length; f++)
        {
            double x = (tiempos[f] - inicio).TotalSeconds / segundos;
            if (x >= 0 && x <= 1 && valor(filas[f, columna]) is double v)
                puntos.Add((x, v));
        }
        return puntos;
    }

    /// <summary>Deja como mucho cuatro puntos por tramo (primero, mínimo, máximo y último) sin perder picos.</summary>
    private static List<(double X, double V)> Reducir(List<(double X, double V)> puntos, int tramos)
    {
        if (puntos.Count <= tramos * 4)
            return puntos;

        var salida = new List<(double, double)>();
        int i = 0;
        for (int t = 0; t < tramos && i < puntos.Count; t++)
        {
            double limite = (t + 1.0) / tramos;
            int primero = i, min = i, max = i;
            while (i < puntos.Count && (puntos[i].X < limite || t == tramos - 1))
            {
                if (puntos[i].V < puntos[min].V) min = i;
                if (puntos[i].V > puntos[max].V) max = i;
                i++;
            }
            if (i == primero)
                continue;
            foreach (int k in new[] { primero, min, max, i - 1 }.Distinct().OrderBy(k => k))
                salida.Add(puntos[k]);
        }
        return salida;
    }

    /// <summary>Entre 2 y 8 marcas en horas redondas.</summary>
    private static List<DateTime> Marcas(DateTime inicio, DateTime fin)
    {
        int[] pasos = { 1, 2, 5, 10, 15, 20, 30, 60, 120, 180, 240, 360, 720, 1440 };
        double minutos = (fin - inicio).TotalMinutes;
        int paso = pasos.FirstOrDefault(p => minutos / p <= 8);
        if (paso == 0)
            paso = 1440;

        var marcas = new List<DateTime>();
        var t = inicio.Date;
        while (t < inicio)
            t = t.AddMinutes(paso);
        for (; t <= fin; t = t.AddMinutes(paso))
            marcas.Add(t);
        return marcas;
    }

    #region Archivo PDF
    private static void Guardar(string archivo, string contenido, string titulo)
    {
        var latin = Encoding.Latin1;
        byte[] datos;
        using (var comprimido = new MemoryStream())
        {
            using (var zlib = new ZLibStream(comprimido, CompressionLevel.Optimal, true))
                zlib.Write(latin.GetBytes(contenido));
            datos = comprimido.ToArray();
        }

        string fuente(string nombre) => "<< /Type /Font /Subtype /Type1 /BaseFont /" + nombre + " /Encoding /WinAnsiEncoding >>";
        var objetos = new List<byte[]>
        {
            latin.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"),
            latin.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            latin.GetBytes("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 5 0 R /F2 6 0 R >> >> /Contents 4 0 R >>"),
            latin.GetBytes("<< /Length " + datos.Length + " /Filter /FlateDecode >>\nstream\n").Concat(datos).Concat(latin.GetBytes("\nendstream")).ToArray(),
            latin.GetBytes(fuente("Helvetica")),
            latin.GetBytes(fuente("Helvetica-Bold")),
            latin.GetBytes("<< /Title (" + Lienzo.Escapar(titulo) + ") /Producer (Mezcladora Marion HMI) /CreationDate (D:" + DateTime.Now.ToString("yyyyMMddHHmmss", Inv) + ") >>"),
        };

        using var salida = new FileStream(archivo, FileMode.Create, FileAccess.Write);
        void Poner(string texto) => salida.Write(latin.GetBytes(texto));
        Poner("%PDF-1.4\n%âãÏÓ\n");
        var posiciones = new long[objetos.Count];
        for (int i = 0; i < objetos.Count; i++)
        {
            posiciones[i] = salida.Position;
            Poner((i + 1) + " 0 obj\n");
            salida.Write(objetos[i]);
            Poner("\nendobj\n");
        }

        long xref = salida.Position;
        var tabla = new StringBuilder("xref\n0 " + (objetos.Count + 1) + "\n0000000000 65535 f \n");
        foreach (long p in posiciones)
            tabla.Append(p.ToString("D10", Inv)).Append(" 00000 n \n");
        tabla.Append("trailer\n<< /Size " + (objetos.Count + 1) + " /Root 1 0 R /Info 7 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
        Poner(tabla.ToString());
    }

    /// <summary>Instrucciones de dibujo de la página. Las coordenadas se dan desde arriba a la izquierda.</summary>
    private sealed class Lienzo
    {
        private readonly StringBuilder sb = new StringBuilder();
        public string Contenido => sb.ToString();

        public void Rect(float x, float y, float w, float h, string color)
        {
            sb.Append(Color(color, false)).Append(Caja(x, y, w, h)).Append(" re f\n");
        }

        public void Borde(float x, float y, float w, float h, string color, float grosor)
        {
            sb.Append(Color(color, true)).Append(N(grosor)).Append(" w [] 0 d ").Append(Caja(x, y, w, h)).Append(" re S\n");
        }

        public void Linea(float xa, float ya, float xb, float yb, string color, float grosor)
        {
            sb.Append(Color(color, true)).Append(N(grosor)).Append(" w [] 0 d ")
              .Append(N(xa)).Append(' ').Append(N(Alto - ya)).Append(" m ").Append(N(xb)).Append(' ').Append(N(Alto - yb)).Append(" l S\n");
        }

        public void Polilinea(List<(float X, float Y)> puntos, string color, float grosor)
        {
            sb.Append(Color(color, true)).Append(N(grosor)).Append(" w [] 0 d 1 j 1 J\n");
            for (int i = 0; i < puntos.Count; i++)
                sb.Append(N(puntos[i].X)).Append(' ').Append(N(Alto - puntos[i].Y)).Append(i == 0 ? " m\n" : " l\n");
            sb.Append("S 0 j 0 J\n");
        }

        /// <param name="alineacion">0 izquierda, 0.5 centro, 1 derecha.</param>
        public void Texto(float x, float y, float tamano, bool negrita, string texto, string color, float alineacion = 0)
        {
            if (alineacion > 0)
                x -= Medir(texto, tamano) * (negrita ? 1.05f : 1f) * alineacion;
            sb.Append("BT ").Append(Color(color, false)).Append(negrita ? "/F2 " : "/F1 ").Append(N(tamano)).Append(" Tf ")
              .Append(N(x)).Append(' ').Append(N(Alto - y)).Append(" Td (").Append(Escapar(texto)).Append(") Tj ET\n");
        }

        private static string Caja(float x, float y, float w, float h) => N(x) + " " + N(Alto - y - h) + " " + N(w) + " " + N(h);

        private static string Color(string hex, bool trazo)
        {
            int v = Convert.ToInt32(hex.TrimStart('#'), 16);
            return N(((v >> 16) & 255) / 255f) + " " + N(((v >> 8) & 255) / 255f) + " " + N((v & 255) / 255f) + (trazo ? " RG " : " rg ");
        }

        private static string N(float valor) => valor.ToString("0.###", Inv);

        public static string Escapar(string texto) => texto.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

        // Anchos de Helvetica por cada 1000 unidades, de ' ' a '~'
        private static readonly short[] Anchos =
        {
            278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
            556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
            1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
            667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
            333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
            556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
        };

        public static float Medir(string texto, float tamano)
        {
            float total = 0;
            foreach (char c in texto.Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                    continue;
                total += c >= ' ' && c <= '~' ? Anchos[c - ' '] : 556;
            }
            return total * tamano / 1000f;
        }
    }
    #endregion

    #region Valores
    private static bool? Activo(object valor)
    {
        if (valor == null || valor is DBNull)
            return null;
        if (valor is bool b)
            return b;
        string texto = Convert.ToString(valor, Inv);
        if (bool.TryParse(texto, out b))
            return b;
        return double.TryParse(texto, NumberStyles.Float, Inv, out double d) ? d != 0 : null;
    }

    private static double? Numero(object valor)
    {
        if (valor == null || valor is DBNull)
            return null;
        return double.TryParse(Convert.ToString(valor, Inv), NumberStyles.Float, Inv, out double d) ? d : null;
    }

    private static DateTime Fecha(object valor) => valor is DateTime f ? f : DateTime.Parse(Convert.ToString(valor, Inv), Inv);

    private static string Txt(DateTime fecha) => fecha.ToString("dd/MM/yyyy HH:mm", Inv);

    private static string Miles(long numero) => numero.ToString("N0", Inv).Replace(',', ' ');

    private static string Duracion(TimeSpan t)
    {
        if (t.TotalMinutes < 1)
            return ((int)t.TotalSeconds) + " s";
        if (t.TotalHours < 1)
            return ((int)t.TotalMinutes) + " min";
        return ((int)t.TotalHours) + " h " + t.Minutes.ToString("00") + " min";
    }
    #endregion
}
