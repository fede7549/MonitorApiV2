namespace MonitorPapa.Api.Services;

public static class OximetroBleConfig
{
    public static readonly HashSet<ulong> OximeterMacs = new()
{
    0xD13133321D46,
    0xCF3930385D36,
    0xD13B2B8A6B46 // solo si detectas que este es otro de tus oxímetros
};

    public static readonly Guid ServiceUuid =
        Guid.Parse("0000ffe0-0000-1000-8000-00805f9b34fb");

    public static readonly Guid CharacteristicUuid =
        Guid.Parse("0000ffe1-0000-1000-8000-00805f9b34fb");
}