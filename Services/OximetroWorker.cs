using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace MonitorPapa.Api.Services;

public class OximetroWorker : BackgroundService
{
    private readonly MonitorStateStore _store;
    private readonly ILogger<OximetroWorker> _logger;
    private readonly DeviceSelectionStore _selectionStore;

    private BluetoothLEAdvertisementWatcher? _watcher;
    private BluetoothLEDevice? _device;
    private GattCharacteristic? _characteristic;

    private DateTime _lastData = DateTime.MinValue;
    private ulong? _lastAddress;

    private DateTime _lastLog = DateTime.MinValue;

    public OximetroWorker(
        MonitorStateStore store,
        DeviceSelectionStore selectionStore,
        ILogger<OximetroWorker> logger)
    {
        _store = store;
        _selectionStore = selectionStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        //_logger.LogInformation("OximetroWorker BLE iniciado");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_selectionStore.IsOximetroEnabled)
                {
                    Cleanup();
                    _lastData = DateTime.MinValue;
                    _store.SetOximetroDisabled();
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }

                await ConnectAndListenAsync(stoppingToken);

                while (!stoppingToken.IsCancellationRequested)
                {
                    if (!_selectionStore.IsOximetroEnabled)
                    {
                        Cleanup();
                        _lastData = DateTime.MinValue;
                        _store.SetOximetroDisabled();
                        break;
                    }

                    if (_lastData != DateTime.MinValue &&
                        DateTime.Now.Subtract(_lastData).TotalSeconds > 30)
                    {
                        throw new Exception("Oxímetro sin datos por más de 30 segundos");
                    }

                    await Task.Delay(1000, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error/reconexión oxímetro");
                _store.SetOximetroError(ex.Message);

                Cleanup();

                await Task.Delay(3000, stoppingToken);
            }
        }
    }

    private async Task ConnectAndListenAsync(CancellationToken token)
    {
        ulong address = await FindOximeterAsync(token);

        _lastAddress = address;

        _logger.LogInformation("Oxímetro encontrado: {Address}", address);

        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);

        if (_device == null)
            throw new Exception("No se pudo abrir BluetoothLEDevice");

        _logger.LogInformation("Conectado a: {Name}", _device.Name);

        var servicesResult = await _device.GetGattServicesForUuidAsync(
            OximetroBleConfig.ServiceUuid,
            BluetoothCacheMode.Uncached);

        if (servicesResult.Status != GattCommunicationStatus.Success ||
            servicesResult.Services.Count == 0)
        {
            throw new Exception($"No se encontró servicio FFE0. Status: {servicesResult.Status}");
        }

        var service = servicesResult.Services[0];

        var charsResult = await service.GetCharacteristicsForUuidAsync(
            OximetroBleConfig.CharacteristicUuid,
            BluetoothCacheMode.Uncached);

        if (charsResult.Status != GattCommunicationStatus.Success ||
            charsResult.Characteristics.Count == 0)
        {
            throw new Exception($"No se encontró característica FFE1. Status: {charsResult.Status}");
        }

        _characteristic = charsResult.Characteristics[0];
        _characteristic.ValueChanged += Characteristic_ValueChanged;

        var status = await _characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);

        if (status != GattCommunicationStatus.Success)
        {
            throw new Exception($"No se pudo activar Notify. Status: {status}");
        }

        _lastData = DateTime.Now;

        _logger.LogInformation("Notify activado correctamente en oxímetro");
    }

    private async Task<ulong> FindOximeterAsync(CancellationToken token)
    {
        var tcs = new TaskCompletionSource<ulong>();

        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        _watcher.Received += (_, args) =>
        {
            string name = args.Advertisement.LocalName ?? "";

            bool macOximetro =
    OximetroBleConfig.OximeterMacs.Contains(args.BluetoothAddress);

            bool nombreOximetro =
                name.Contains("OXIMETER", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("HealthTree", StringComparison.OrdinalIgnoreCase);

            _logger.LogInformation(
                "BLE visto: {Name} | {Address:X} | RSSI {Rssi} | MatchMac={MatchMac} | MatchName={MatchName}",
                string.IsNullOrWhiteSpace(name) ? "(sin nombre)" : name,
                args.BluetoothAddress,
                args.RawSignalStrengthInDBm,
                macOximetro,
                nombreOximetro);

            if (macOximetro || nombreOximetro)
            {
                tcs.TrySetResult(args.BluetoothAddress);
            }
        };

        _watcher.Start();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));

        await using var _ = timeoutCts.Token.Register(() =>
        {
            tcs.TrySetCanceled();
        });

        try
        {
            return await tcs.Task;
        }
        catch
        {
            throw new Exception("No se encontró el oxímetro por BLE");
        }
        finally
        {
            _watcher.Stop();
        }
    }

    private void Characteristic_ValueChanged(
        GattCharacteristic sender,
        GattValueChangedEventArgs args)
    {
        try
        {
            byte[] data = args.CharacteristicValue.ToArray();

            string hex = BitConverter.ToString(data);

            //_logger.LogInformation("DATA OXIMETRO: {Hex}", hex);

            if (data.Length >= 6 && data[0] == 0xFF && data[1] == 0x44)
            {
                int spo2 = data[4];
                int pulso = data[5];

                //_logger.LogInformation("PAQUETE VALIDO -> SPO2={Spo2} PULSO={Pulso}", spo2, pulso);

                if (spo2 > 20 && spo2 <= 100 && pulso > 20 && pulso < 250)
                {
                    _lastData = DateTime.Now;
                    _store.UpdateOximetro(spo2, pulso);

                    if (DateTime.Now.Subtract(_lastLog).TotalSeconds >= 10)
                    {
                        _lastLog = DateTime.Now;

                        _logger.LogInformation(
                            "SpO2={Spo2}% Pulso={Pulso}",
                            spo2,
                            pulso);
                    }

                    //_logger.LogInformation("GUARDADO EN STORE -> SPO2={Spo2} PULSO={Pulso}", spo2, pulso);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parseando datos oxímetro");
        }
    }

    private void Cleanup()
    {
        try
        {
            if (_characteristic != null)
            {
                _characteristic.ValueChanged -= Characteristic_ValueChanged;

                try
                {
                    _characteristic
                        .WriteClientCharacteristicConfigurationDescriptorAsync(
                            GattClientCharacteristicConfigurationDescriptorValue.None)
                        .AsTask()
                        .Wait(1500);
                }
                catch { }
            }

            _characteristic = null;

            _device?.Dispose();
            _device = null;
        }
        catch
        {
        }
    }
}