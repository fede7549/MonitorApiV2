using System.Text.Json;
using System.Threading.Channels;

namespace MonitorPapa.Api.Services;

public sealed class JsonHistoryService : BackgroundService
{
    private readonly Channel<JsonHistoryItem> _channel;
    private readonly ILogger<JsonHistoryService> _logger;
    private readonly string _folderPath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public JsonHistoryService(IHostEnvironment environment, ILogger<JsonHistoryService> logger)
    {
        _logger = logger;
        _folderPath = Path.Combine(environment.ContentRootPath, "Data", "Lecturas");

        _channel = Channel.CreateUnbounded<JsonHistoryItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    public void Enqueue(string sensor, string tipo, object data)
    {
        var item = new JsonHistoryItem(
            Timestamp: DateTime.Now,
            Sensor: NormalizeName(sensor),
            Tipo: tipo,
            Data: data);

        if (!_channel.Writer.TryWrite(item))
        {
            _logger.LogWarning("No se pudo encolar lectura JSONL. Sensor={Sensor} Tipo={Tipo}", sensor, tipo);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_folderPath);
        _logger.LogInformation("JsonHistoryService iniciado. Carpeta={Folder}", _folderPath);

        await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await WriteItemAsync(item, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error escribiendo historial JSONL");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();

        while (_channel.Reader.TryRead(out var item))
        {
            try
            {
                await WriteItemAsync(item, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error vaciando cola JSONL al detener servicio");
            }
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task WriteItemAsync(JsonHistoryItem item, CancellationToken token)
    {
        Directory.CreateDirectory(_folderPath);

        string date = item.Timestamp.ToString("yyyyMMdd");
        string line = JsonSerializer.Serialize(item, JsonOptions);

        string allPath = Path.Combine(_folderPath, $"lecturas_{date}.jsonl");
        string sensorPath = Path.Combine(_folderPath, $"{item.Sensor}_{date}.jsonl");

        await File.AppendAllTextAsync(allPath, line + Environment.NewLine, token);
        await File.AppendAllTextAsync(sensorPath, line + Environment.NewLine, token);
    }

    private static string NormalizeName(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "desconocido"
            : value.Trim().ToLowerInvariant().Replace(" ", "_");
    }

    private sealed record JsonHistoryItem(
        DateTime Timestamp,
        string Sensor,
        string Tipo,
        object Data);
}
