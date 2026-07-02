using Windows.Devices.Bluetooth.Advertisement;

namespace MonitorPapa.Api.Services;

public class BleScannerWorker : BackgroundService
{
    private readonly ILogger<BleScannerWorker> _logger;
    private BluetoothLEAdvertisementWatcher? _watcher;

    public BleScannerWorker(ILogger<BleScannerWorker> logger)
    {
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Escáner BLE iniciado");

        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        _watcher.Received += (_, args) =>
        {
            string name = args.Advertisement.LocalName;

            _logger.LogInformation(
                "BLE visto: {Name} | {Address:X} | RSSI {Rssi}",
                string.IsNullOrWhiteSpace(name) ? "(sin nombre)" : name,
                args.BluetoothAddress,
                args.RawSignalStrengthInDBm);
        };

        _watcher.Start();

        stoppingToken.Register(() =>
        {
            try
            {
                _watcher.Stop();
            }
            catch { }
        });

        return Task.CompletedTask;
    }
}