namespace MonitorPapa.Api.Models;

public class Wt901State
{
    public bool Conectado { get; set; }
    public string Estado { get; set; } = "SIN_DATOS";
    public string? Posicion { get; set; }
    public double? Pitch { get; set; }
    public double? Roll { get; set; }
    public double? Yaw { get; set; }

    // Batería WT901.
    // BatteryRaw es el valor directo leído desde el registro BATVAL 0x5C.
    // De momento lo dejamos como "raw" porque el SDK no documenta claramente si es mV, % o escala propia.
    public ushort? BatteryRaw { get; set; }
    public DateTime? UltimaLecturaBateria { get; set; }

    public DateTime? UltimaLectura { get; set; }
    public string? Alias { get; set; }
    public string? Mac { get; set; }
    public string? Error { get; set; }
}
