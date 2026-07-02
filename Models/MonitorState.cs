namespace MonitorPapa.Api.Models;

public class MonitorState
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public DeviceSelectionState Dispositivos { get; set; } = new();
    public OximetroState Oximetro { get; set; } = new();
    public Wt901State Wt901 { get; set; } = new();
    public RingState Ring { get; set; } = new();

    public bool AlertaSpo2Baja =>
        (Dispositivos.Oximetro && Oximetro.Spo2.HasValue && Oximetro.Spo2.Value < 90) ||
        (Dispositivos.Ring && Ring.Spo2.HasValue && Ring.Spo2.Value < 90);

    public bool AlertaSupino =>
        Dispositivos.Wt901 && Wt901.Posicion == "BOCA_ARRIBA";

    public bool SinSenalOximetro =>
        Dispositivos.Oximetro &&
        (Oximetro.UltimaLectura == null || DateTime.Now.Subtract(Oximetro.UltimaLectura.Value).TotalSeconds > 30);

    public bool DatosOximetroVigentes =>
        Dispositivos.Oximetro &&
        Oximetro.UltimaLectura != null &&
        DateTime.Now.Subtract(Oximetro.UltimaLectura.Value).TotalSeconds <= 30;

    public int? SegundosUltimaLecturaOximetro =>
        Oximetro.UltimaLectura == null ? null : (int)DateTime.Now.Subtract(Oximetro.UltimaLectura.Value).TotalSeconds;

    public bool SinSenalWt901 =>
        Dispositivos.Wt901 &&
        (Wt901.UltimaLectura == null || DateTime.Now.Subtract(Wt901.UltimaLectura.Value).TotalSeconds > 30);

    public bool DatosWt901Vigentes =>
        Dispositivos.Wt901 &&
        Wt901.UltimaLectura != null &&
        DateTime.Now.Subtract(Wt901.UltimaLectura.Value).TotalSeconds <= 30;

    public int? SegundosUltimaLecturaWt901 =>
        Wt901.UltimaLectura == null ? null : (int)DateTime.Now.Subtract(Wt901.UltimaLectura.Value).TotalSeconds;

    private DateTime? UltimaSenalRing => Ring.UltimaComunicacion ?? Ring.UltimaLectura;

    public bool SinSenalRing =>
        Dispositivos.Ring &&
        (UltimaSenalRing == null || DateTime.Now.Subtract(UltimaSenalRing.Value).TotalSeconds > 90);

    public bool DatosRingVigentes =>
        Dispositivos.Ring &&
        UltimaSenalRing != null &&
        DateTime.Now.Subtract(UltimaSenalRing.Value).TotalSeconds <= 90;

    public int? SegundosUltimaLecturaRing =>
        UltimaSenalRing == null ? null : (int)DateTime.Now.Subtract(UltimaSenalRing.Value).TotalSeconds;

    public bool AlertaGeneral => NivelAlerta != "OK";

    public string NivelAlerta
    {
        get
        {
            if (Dispositivos.Oximetro && SinSenalOximetro) return "SIN_OXIMETRO";
            if (Dispositivos.Ring && SinSenalRing) return "SIN_RING";

            var spo2Principal = Dispositivos.Oximetro && Oximetro.Spo2.HasValue
                ? Oximetro.Spo2
                : Dispositivos.Ring ? Ring.Spo2 : null;

            if (spo2Principal <= 85) return "SPO2_CRITICA";
            if (spo2Principal <= 89) return "SPO2_ALERTA";
            if (spo2Principal <= 93) return "SPO2_PRECAUCION";

            if (AlertaSupino) return "SUPINO";
            if (Dispositivos.Wt901 && SinSenalWt901) return "SIN_WT901";

            return "OK";
        }
    }
}
