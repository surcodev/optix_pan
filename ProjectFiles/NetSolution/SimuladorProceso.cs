#region Using directives
using System;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using UAManagedCore;
using FTOptix.ODBCStore;
#endregion

/// <summary>
/// Simula el ciclo de la mezcladora sin PLC. Escribe en Model/Proceso y Model/Produccion
/// y lee la receta activa de Model/Receta. Para conectar un PLC real basta con retirar
/// este NetLogic y enlazar las variables de Model/Proceso a los tags.
/// </summary>
public class SimuladorProceso : BaseNetLogic
{
    private const int Reposo = 0, Tamizado = 1, CargaCompleta = 2, Mezclado = 3, MezclaFin = 4, Descarga = 5;
    private const double PeriodoSeg = 0.2;
    private const double MinutosTamizado = 5.0;
    private const double NivelLleno = 90.0;
    private const double NivelSC1 = 3.0;

    private static readonly string[] Categorias = { "Nada", "Manteca", "Semillas", "Aceite", "Galletas" };
    private static readonly string[] Alarmas =
    {
        "Alm_FallaM1", "Alm_FallaM2", "Alm_FallaM3", "Alm_FallaM4",
        "Alm_NoTamVencida", "Alm_BloqueoTamizado", "Alm_BloqueoDescarga"
    };

    private readonly object bloqueo = new object();
    private PeriodicTask tarea;
    private IUANode proceso, receta, produccion;

    private int estado, ticks;
    private double nivel, nivelIbc, segundosFase, minutosProceso, crono, cronoChopper, segundosVs1, angulo;
    private double segundosBloqueoTamizado, segundosBloqueoDescarga;
    private bool chopperHecho, noTamConfirmada;
    private bool m1, m2, m3, m4, v1, vs1, suspendido;

    public override void Start()
    {
        proceso = Project.Current.Get("Model/Proceso");
        receta = Project.Current.Get("Model/Receta");
        produccion = Project.Current.Get("Model/Produccion");

        Reiniciar();
        tarea = new PeriodicTask(Ciclo, (int)(PeriodoSeg * 1000), LogicObject);
        tarea.Start();
    }

    public override void Stop()
    {
        tarea?.Dispose();
    }

    #region Comandos
    [ExportMethod]
    public void IniciarTamizado()
    {
        lock (bloqueo)
        {
            if (estado != Reposo)
                return;

            if (Bool("V1") || Bool("SC1"))
            {
                segundosBloqueoTamizado = 10;
                return;
            }

            Reposar();
            estado = Tamizado;
        }
    }

    [ExportMethod]
    public void IniciarMezclado()
    {
        lock (bloqueo)
        {
            if (estado == CargaCompleta)
                estado = Mezclado;
        }
    }

    [ExportMethod]
    public void IniciarDescarga()
    {
        lock (bloqueo)
        {
            if (estado != MezclaFin)
                return;

            if (!Bool("SI1"))
            {
                segundosBloqueoDescarga = 10;
                return;
            }

            segundosFase = 0;
            segundosBloqueoDescarga = 0;
            estado = Descarga;
        }
    }

    [ExportMethod]
    public void ConfirmarCarga()
    {
        lock (bloqueo)
        {
            if (estado == Mezclado && NoTamIniciada())
                noTamConfirmada = true;
        }
    }

    [ExportMethod]
    public void Detener()
    {
        lock (bloqueo)
            Reposar();
    }

    [ExportMethod]
    public void ReiniciarDemo()
    {
        lock (bloqueo)
            Reiniciar();
    }

    /// <summary>Tiempo real: 1 segundo de proceso por cada segundo.</summary>
    [ExportMethod]
    public void VelocidadReal() => Escribir("VelocidadSim", 1);

    /// <summary>Velocidad acelerada para presentar la demo.</summary>
    [ExportMethod]
    public void VelocidadDemo() => Escribir("VelocidadSim", 30);
    #endregion

    private void Ciclo()
    {
        lock (bloqueo)
        {
            try
            {
                Avanzar();
                Publicar();
            }
            catch (Exception e)
            {
                Log.Error("SimuladorProceso", e.Message);
            }
        }
    }

    private void Avanzar()
    {
        double dt = PeriodoSeg * Math.Max(1, Math.Min(120, Entero("VelocidadSim")));

        m1 = m2 = m3 = m4 = v1 = suspendido = false;
        segundosBloqueoTamizado = Math.Max(0, segundosBloqueoTamizado - PeriodoSeg);
        segundosBloqueoDescarga = Math.Max(0, segundosBloqueoDescarga - PeriodoSeg);

        switch (estado)
        {
            case Tamizado:
                AvanzarTamizado(dt);
                break;
            case Mezclado:
                AvanzarMezclado(dt);
                break;
            case Descarga:
                AvanzarDescarga(dt);
                break;
        }

        // Limpieza de la camara de choppers: tiempo real, no acelerado
        vs1 = segundosVs1 > 0;
        segundosVs1 = Math.Max(0, segundosVs1 - PeriodoSeg);

        if (m2)
            angulo = (angulo + 45) % 360;
    }

    private void AvanzarTamizado(double dt)
    {
        segundosFase += dt;
        m1 = true;
        m2 = RecetaBool("Carga_HabilitarM2") &&
             (RecetaBool("Carga_ModoOperador") || segundosFase <= RecetaNum("Carga_TiempoSeg"));
        nivel = Math.Min(NivelLleno, nivel + NivelLleno * dt / (MinutosTamizado * 60));

        if (nivel >= NivelLleno)
        {
            m1 = m2 = false;
            estado = CargaCompleta;
        }
    }

    private void AvanzarMezclado(double dt)
    {
        // Sin confirmacion de encendido de M2 el proceso queda en pausa
        if (Bool("SimFallaM2"))
            return;

        m2 = true;
        double total = RecetaNum("Mezcla_TiempoTotalMin");
        double minutos = dt / 60.0;
        minutosProceso += minutos;

        double corte = RecetaNum("NoTam_MinFinCronometro");
        suspendido = NoTamRequerida() && !noTamConfirmada && corte > 0 && crono >= corte;
        if (!suspendido)
            crono = Math.Min(total, crono + minutos);

        double uso = RecetaNum("Chopper_MinutosUso");
        if (!chopperHecho && uso > 0 && crono >= RecetaNum("Chopper_MinEncendido"))
        {
            cronoChopper = Math.Min(uso, cronoChopper + minutos);
            m3 = m4 = cronoChopper < uso;
            if (!m3)
                TerminarChopper();
        }

        if (crono >= total && (!NoTamRequerida() || noTamConfirmada))
        {
            if (m3)
                TerminarChopper();
            m2 = m3 = m4 = false;
            estado = MezclaFin;
        }
    }

    private void TerminarChopper()
    {
        chopperHecho = true;
        segundosVs1 = Math.Max(2, Math.Min(10, Entero("VS1_Segundos")));
    }

    private void AvanzarDescarga(double dt)
    {
        // Si se pierde la conexion de la manguera, V1 cierra y la descarga espera
        if (!Bool("SI1"))
        {
            segundosBloqueoDescarga = 1;
            return;
        }

        v1 = true;
        segundosFase += dt;
        double segundosMin = Math.Max(30, RecetaNum("Descarga_TiempoMinMin") * 60);
        double delta = Math.Min(nivel, NivelLleno * dt / segundosMin);
        nivel -= delta;
        nivelIbc = Math.Min(100, nivelIbc + delta);

        if (nivel <= 0.01 && segundosFase >= segundosMin)
        {
            v1 = false;
            nivel = 0;
            CerrarBatch();
            estado = Reposo;
        }
    }

    private bool NoTamRequerida() => RecetaNum("NoTam_Categoria") > 0;

    private bool NoTamIniciada() => NoTamRequerida() && crono >= RecetaNum("NoTam_MinInicio");

    private bool NoTamVencida() => estado == Mezclado && NoTamRequerida() && !noTamConfirmada &&
                                   minutosProceso >= RecetaNum("NoTam_MinConfirmacion");

    private void CerrarBatch()
    {
        foreach (var orden in produccion.Children)
        {
            var pendientes = orden.GetVariable("Pendientes");
            if ((int)pendientes.Value <= 0)
                continue;

            pendientes.Value = (int)pendientes.Value - 1;
            var terminados = orden.GetVariable("Terminados");
            terminados.Value = (int)terminados.Value + 1;
            return;
        }
    }

    private void Reposar()
    {
        estado = Reposo;
        nivel = nivelIbc = crono = cronoChopper = minutosProceso = segundosFase = segundosVs1 = 0;
        segundosBloqueoTamizado = segundosBloqueoDescarga = 0;
        chopperHecho = noTamConfirmada = false;
    }

    private void Reiniciar()
    {
        Reposar();
        Escribir("SimFallaM2", false);
        Escribir("SI1", true);

        int[,] inicial = { { 2, 4 }, { 0, 6 }, { 0, 4 } };
        for (int i = 0; i < 3; i++)
        {
            var orden = produccion.Get("Orden" + (i + 1));
            orden.GetVariable("Terminados").Value = inicial[i, 0];
            orden.GetVariable("Pendientes").Value = inicial[i, 1];
            orden.GetVariable("Total").Value = inicial[i, 0] + inicial[i, 1];
        }
    }

    private void Publicar()
    {
        bool sc1 = nivel > NivelSC1;
        bool si1 = Bool("SI1");
        bool falla = Bool("SimFallaM2") && estado == Mezclado;
        bool vencida = NoTamVencida();
        bool esperando = estado == Mezclado && NoTamIniciada() && !noTamConfirmada;

        Escribir("Estado", estado);
        Escribir("M1", m1);
        Escribir("M2", m2);
        Escribir("M3", m3);
        Escribir("M4", m4);
        Escribir("V1", v1);
        Escribir("VS1", vs1);
        Escribir("SC1", sc1);

        double total = RecetaNum("Mezcla_TiempoTotalMin");
        Escribir("Nivel", (float)nivel);
        Escribir("NivelIBC", (float)nivelIbc);
        Escribir("CronoM2", (float)Math.Round(crono, 2));
        Escribir("CronoM3", (float)Math.Round(cronoChopper, 2));
        Escribir("CronoM4", (float)Math.Round(cronoChopper, 2));
        Escribir("Progreso", (float)(total > 0 ? Math.Min(100, crono * 100 / total) : 0));
        Escribir("CronoMezclaTxt", MinSeg(crono));
        Escribir("CronoChopperTxt", MinSeg(cronoChopper));
        Escribir("CronoDescargaTxt", MinSeg(estado == Descarga ? segundosFase / 60.0 : 0));
        Escribir("ObjetivoMezclaTxt", "de " + MinSeg(total));
        Escribir("ObjetivoChopperTxt", "de " + MinSeg(RecetaNum("Chopper_MinutosUso")));
        Escribir("ObjetivoDescargaTxt", "mín. " + MinSeg(RecetaNum("Descarga_TiempoMinMin")));

        Escribir("Paleta", (int)(angulo / 45));
        Escribir("BalizaVerde", estado == Descarga && v1);
        Escribir("BalizaNaranja", esperando && !vencida);
        Escribir("BalizaRoja", vencida || falla);
        Escribir("PedirConfirmacion", esperando);
        Escribir("PuedeTamizar", estado == Reposo);
        Escribir("PuedeMezclar", estado == CargaCompleta);
        Escribir("PuedeDescargar", estado == MezclaFin);
        Escribir("CicloActivo", estado != Reposo);

        int categoria = (int)RecetaNum("NoTam_Categoria");
        string categoriaTxt = categoria >= 0 && categoria < Categorias.Length ? Categorias[categoria] : "?";
        Escribir("CategoriaTxt", categoriaTxt);
        Escribir("EstadoTxt", TextoEstado(falla));
        Escribir("Mensaje", TextoMensaje(falla, vencida, esperando, si1, categoriaTxt));

        Escribir("Alm_FallaM2", falla);
        Escribir("Alm_NoTamVencida", vencida);
        Escribir("Alm_BloqueoTamizado", segundosBloqueoTamizado > 0);
        Escribir("Alm_BloqueoDescarga", segundosBloqueoDescarga > 0);

        // Senales apiladas en carriles para la tendencia digital
        Escribir("T_M1", Carril(4, m1));
        Escribir("T_M2", Carril(3, m2));
        Escribir("T_SC1", Carril(2, sc1));
        Escribir("T_V1", Carril(1, v1));
        Escribir("T_SI1", Carril(0, si1));

        if (ticks++ % 5 == 0)
        {
            var ahora = DateTime.Now;
            Escribir("HoraTxt", ahora.ToString("HH:mm:ss"));
            Escribir("FechaTxt", ahora.ToString("dd/MM/yyyy"));
            ContarAlarmas();
            MarcarOrdenActiva();
        }
    }

    private void ContarAlarmas()
    {
        int activas = 0;
        foreach (var nombre in Alarmas)
        {
            if (Bool(nombre))
                activas++;
        }

        Escribir("AlarmasActivas", activas);
        Escribir("HayAlarmas", activas > 0);
    }

    private void MarcarOrdenActiva()
    {
        bool encontrada = false;
        foreach (var orden in produccion.Children)
        {
            bool activa = !encontrada && (int)orden.GetVariable("Pendientes").Value > 0;
            encontrada |= activa;
            orden.GetVariable("Activa").Value = activa;
        }
    }

    private string TextoEstado(bool falla)
    {
        if (falla)
            return "Falla M2";

        switch (estado)
        {
            case Tamizado: return "Tamizado";
            case CargaCompleta: return "Carga completa";
            case Mezclado: return suspendido ? "Mezclado en pausa" : "Mezclado";
            case MezclaFin: return "Mezcla terminada";
            case Descarga: return "Descarga";
            default: return "En reposo";
        }
    }

    private string TextoMensaje(bool falla, bool vencida, bool esperando, bool si1, string categoria)
    {
        if (falla)
            return "M2 sin confirmación de encendido. Revise el contacto auxiliar.";
        if (segundosBloqueoTamizado > 0)
            return "Tamizado bloqueado: V1 debe estar cerrada y SC1 sin producto.";
        if (segundosBloqueoDescarga > 0)
            return "Descarga bloqueada: SI1 no detecta la manguera del IBC.";
        if (vencida)
            return "Confirme AHORA la carga no tamizada: " + categoria + ".";
        if (esperando)
            return "Cargue " + categoria + " y confirme al terminar.";

        switch (estado)
        {
            case Tamizado: return "Cargando ingredientes por el tamiz.";
            case CargaCompleta: return "Carga completa. Pulse Mezclado para continuar.";
            case Mezclado: return m3 ? "Mezclando con choppers M3 y M4." : "Mezclando.";
            case MezclaFin: return si1 ? "Mezcla terminada. Pulse Descarga." : "Conecte la manguera del IBC (SI1).";
            case Descarga: return "Descargando al IBC Matcon.";
            default: return "Listo. Pulse Tamizado para iniciar un batch.";
        }
    }

    private static float Carril(int posicion, bool activa) => posicion + (activa ? 0.7f : 0f);

    private static string MinSeg(double minutos)
    {
        int segundos = (int)Math.Round(minutos * 60);
        return $"{segundos / 60:00}:{segundos % 60:00}";
    }

    private bool Bool(string nombre) => (bool)proceso.GetVariable(nombre).Value;

    private int Entero(string nombre) => (int)proceso.GetVariable(nombre).Value;

    private bool RecetaBool(string nombre) => (bool)receta.GetVariable(nombre).Value;

    private double RecetaNum(string nombre) => Convert.ToDouble(receta.GetVariable(nombre).Value.Value);

    private void Escribir(string nombre, object valor)
    {
        var variable = proceso.GetVariable(nombre);
        if (!Equals(variable.Value.Value, valor))
            variable.Value = new UAValue(valor);
    }
}
