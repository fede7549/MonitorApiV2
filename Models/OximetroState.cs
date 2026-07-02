namespace MonitorPapa.Api.Models;

public class OximetroState
{
    public bool Conectado { get; set; }
    public string Estado { get; set; } = "SIN_DATOS";
    public int? Spo2 { get; set; }
    public int? Pulso { get; set; }
    public DateTime? UltimaLectura { get; set; }
    public string? Error { get; set; }
}