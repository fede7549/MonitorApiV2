namespace MonitorPapa.Api.Services;

public static class Wt901BleConfig
{
    public sealed record Wt901DeviceProfile(string Alias, ulong Mac);

    // Prioridad de uso:
    // 1) WT901_1: dispositivo principal.
    // 2) WT901_2: respaldo para failover/carga.
    public static readonly Wt901DeviceProfile[] Devices =
    {
        new("WT901_1", 0xFEEB6C89C731),
        new("WT901_2", 0xF11D68B280B5)
    };

    public static readonly HashSet<ulong> Wt901Macs = Devices.Select(d => d.Mac).ToHashSet();

    public static readonly Guid ServiceUuid =
        Guid.Parse("0000ffe5-0000-1000-8000-00805f9a34fb");

    public static readonly Guid NotifyCharacteristicUuid =
        Guid.Parse("0000ffe4-0000-1000-8000-00805f9a34fb");

    public static readonly Guid WriteCharacteristicUuid =
        Guid.Parse("0000ffe9-0000-1000-8000-00805f9a34fb");

    public static readonly string[] DeviceNames =
    {
        "WT901",
        "WitMotion"
    };
}
