namespace MonitorPapa.Api.Services;

public static class RingBleConfig
{
    public sealed record RingDeviceProfile(string Alias, string CanonicalMac, string[] MatchFragments);

    public static readonly Guid ServiceUuid = Guid.Parse("000056ff-0000-1000-8000-00805f9b34fb");
    public static readonly Guid WriteCharacteristicUuid = Guid.Parse("000033f3-0000-1000-8000-00805f9b34fb");
    public static readonly Guid NotifyCharacteristicUuid = Guid.Parse("000033f4-0000-1000-8000-00805f9b34fb");
    public static readonly Guid BatteryServiceUuid = Guid.Parse("0000180f-0000-1000-8000-00805f9b34fb");
    public static readonly Guid BatteryLevelCharacteristicUuid = Guid.Parse("00002a19-0000-1000-8000-00805f9b34fb");

    public static readonly string[] DeviceNames =
    {
        "SMART_RING",
        "RING"
    };

    // Prioridad de uso:
    // 1) RINGNEGRO: dispositivo principal de tu padre.
    // 2) RINGPLATEADO: respaldo para failover/carga.
    public static readonly RingDeviceProfile[] Devices =
    {
        new("RINGNEGRO", "41:42:7e:93:aa:c5", new[] { "41:42:7e:93:aa:c5", "41427e93aac5" }),
        new("RINGPLATEADO", "41:42:cf:53:e7:2c", new[] { "41:42:cf:53:e7:2c", "4142cf53e72c" })
    };

    public static readonly string[] DeviceIdFragments =
        Devices.SelectMany(d => d.MatchFragments).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
