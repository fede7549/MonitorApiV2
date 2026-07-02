namespace MonitorPapa.Api.Models;

public class RingState
{
    public bool Conectado { get; set; }
    public string Estado { get; set; } = "DESHABILITADO";
    public int? Spo2 { get; set; }
    public int? Pulso { get; set; }
    public int? Bateria { get; set; }
    public int LecturasValidas { get; set; }
    public byte? EstadoMedicion { get; set; }
    public byte? MetricaRaw { get; set; }
    // UltimaLectura: última lectura clínica válida de SpO2/pulso.
    // UltimaComunicacion: último paquete BLE recibido, aunque sea 0x24 con SpO2=0 mientras mide.
    public DateTime? UltimaLectura { get; set; }
    public DateTime? UltimaComunicacion { get; set; }
    public DateTime? UltimaLecturaBateria { get; set; }
    public bool EnMedicion { get; set; }
    public string? Mac { get; set; }
    public string? Error { get; set; }
}
