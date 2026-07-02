using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace MonitorPapa.Api.Services;

public class Wt901Worker : BackgroundService
{
    private readonly MonitorStateStore _store;
    private readonly ILogger<Wt901Worker> _logger;
    private readonly DeviceSelectionStore _selectionStore;

    private BluetoothLEAdvertisementWatcher? _watcher;
    private BluetoothLEDevice? _device;
    private GattCharacteristic? _notifyCharacteristic;
    private GattCharacteristic? _writeCharacteristic;

    private DateTime _lastData = DateTime.MinValue;
    private DateTime _lastLog = DateTime.MinValue;
    private DateTime _lastBatteryRequest = DateTime.MinValue;
    private DateTime _lastUnknownFrameLog = DateTime.MinValue;
    private DateTime _pendingReadRegisterAt = DateTime.MinValue;
    private DateTime _sessionStartedAt = DateTime.MinValue;
    private int _activeWt901Index;
    private Wt901BleConfig.Wt901DeviceProfile? _activeWt901;

    private readonly object _rxLock = new();
    private readonly List<byte> _rxBuffer = new();

    // En protocolo normal WitMotion, algunas respuestas de lectura pueden venir como 55-5F
    // y otras como 55-71. Guardamos cuál registro acabamos de pedir.
    private int? _pendingReadRegister;

    private const int WatchdogSeconds = 30;
    private const int RegisterBatVal = 0x5C;

    // Si el WT901 preferido no logra datos estables después de este tiempo, cambia al otro WT901.
    // Mantengo watchdog interno de 30s para reconectar rápido, pero el cambio de dispositivo espera 180s.
    private const int DeviceFailoverSeconds = 180;

    // Comandos WitMotion / WT901
    private static readonly byte[] UnlockCommand = { 0xFF, 0xAA, 0x69, 0x88, 0xB5 };

    // RRATE = registro 0x03
    // 0x03 = 1 Hz
    // 0x05 = 5 Hz
    // 0x06 = 10 Hz
    private static readonly byte[] SetRate5HzCommand = { 0xFF, 0xAA, 0x03, 0x05, 0x00 };

    // Read register: FF AA 27 [REG_LOW] [REG_HIGH]
    // BATVAL = 0x5C según SDK WitMotion
    private static readonly byte[] ReadBatteryCommand = { 0xFF, 0xAA, 0x27, 0x5C, 0x00 };

    public Wt901Worker(
        MonitorStateStore store,
        DeviceSelectionStore selectionStore,
        ILogger<Wt901Worker> logger)
    {
        _store = store;
        _selectionStore = selectionStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Wt901Worker BLE real iniciado");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_selectionStore.IsWt901Enabled)
                {
                    Cleanup();
                    _lastData = DateTime.MinValue;
                    _store.SetWt901Disabled();
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }

                await ConnectAndListenAsync(stoppingToken);
                _sessionStartedAt = DateTime.Now;

                while (!stoppingToken.IsCancellationRequested)
                {
                    if (!_selectionStore.IsWt901Enabled)
                    {
                        Cleanup();
                        _lastData = DateTime.MinValue;
                        _store.SetWt901Disabled();
                        break;
                    }

                    if (_lastData != DateTime.MinValue &&
                        DateTime.Now.Subtract(_lastData).TotalSeconds > WatchdogSeconds)
                    {
                        throw new Exception($"WT901 sin datos por más de {WatchdogSeconds} segundos");
                    }

                    // Pedimos batería cada 60 segundos. Si el modelo responde, se actualizará en el parser.
                    if (_writeCharacteristic != null &&
                        DateTime.Now.Subtract(_lastBatteryRequest).TotalSeconds >= 60)
                    {
                        await RequestBatteryAsync("Leer batería BATVAL 0x5C", stoppingToken);
                    }

                    await Task.Delay(1000, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error/reconexión WT901");
                _store.SetWt901Error(ex.Message);

                if (ShouldFailoverWt901(ex))
                    SwitchToNextWt901(ex.Message);

                Cleanup();

                await Task.Delay(3000, stoppingToken);
            }
        }
    }

    private async Task ConnectAndListenAsync(CancellationToken token)
    {
        var found = await FindWt901Async(token);
        ulong address = found.Profile.Mac;
        _activeWt901 = found.Profile;

        _logger.LogInformation("WT901 encontrado: {Alias} | {Address:X}", _activeWt901.Alias, address);

        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);

        if (_device == null)
            throw new Exception("No se pudo abrir BluetoothLEDevice del WT901");

        _logger.LogInformation("Conectado a WT901: {Alias} | {Name}", _activeWt901?.Alias ?? "(sin alias)", _device.Name);
        _store.UpdateWt901Device(_activeWt901?.Alias, _activeWt901?.Mac);

        await Task.Delay(1500, token);

        GattDeviceServicesResult? servicesResult = null;

        for (int i = 1; i <= 3; i++)
        {
            servicesResult = await _device.GetGattServicesForUuidAsync(
                Wt901BleConfig.ServiceUuid,
                BluetoothCacheMode.Uncached);

            if (servicesResult.Status == GattCommunicationStatus.Success &&
                servicesResult.Services.Count > 0)
            {
                break;
            }

            _logger.LogWarning(
                "Reintento servicios WT901 {Intento}/3. Status: {Status}",
                i,
                servicesResult.Status);

            await Task.Delay(1500, token);
        }

        if (servicesResult == null ||
            servicesResult.Status != GattCommunicationStatus.Success ||
            servicesResult.Services.Count == 0)
        {
            throw new Exception($"No se encontró servicio FFE5. Status: {servicesResult?.Status}");
        }

        var service = servicesResult.Services[0];

        await ConfigureNotifyAsync(service, token);
        await ConfigureWriteAsync(service, token);

        _lastData = DateTime.Now;
        _lastBatteryRequest = DateTime.MinValue;
        _pendingReadRegister = null;
        _pendingReadRegisterAt = DateTime.MinValue;

        lock (_rxLock)
        {
            _rxBuffer.Clear();
        }

        _logger.LogInformation("Notify activado correctamente en WT901");

        // Primero bajamos a 5 Hz. Esto NO guarda permanente; se aplica a la sesión.
        await WriteWt901CommandAsync(UnlockCommand, "Unlock WT901", token);
        await Task.Delay(200, token);
        await WriteWt901CommandAsync(SetRate5HzCommand, "Set RRATE 5 Hz", token);

        // Pedimos batería una vez al conectar.
        await Task.Delay(300, token);
        await RequestBatteryAsync("Leer batería inicial BATVAL 0x5C", token);
    }

    private async Task ConfigureNotifyAsync(GattDeviceService service, CancellationToken token)
    {
        var charsResult = await service.GetCharacteristicsForUuidAsync(
            Wt901BleConfig.NotifyCharacteristicUuid,
            BluetoothCacheMode.Uncached);

        if (charsResult.Status != GattCommunicationStatus.Success ||
            charsResult.Characteristics.Count == 0)
        {
            throw new Exception($"No se encontró característica Notify FFE4. Status: {charsResult.Status}");
        }

        _notifyCharacteristic = charsResult.Characteristics[0];
        _notifyCharacteristic.ValueChanged += NotifyCharacteristic_ValueChanged;

        var status = await _notifyCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);

        if (status != GattCommunicationStatus.Success)
        {
            throw new Exception($"No se pudo activar Notify en WT901. Status: {status}");
        }
    }

    private async Task ConfigureWriteAsync(GattDeviceService service, CancellationToken token)
    {
        var charsResult = await service.GetCharacteristicsForUuidAsync(
            Wt901BleConfig.WriteCharacteristicUuid,
            BluetoothCacheMode.Uncached);

        if (charsResult.Status != GattCommunicationStatus.Success ||
            charsResult.Characteristics.Count == 0)
        {
            throw new Exception($"No se encontró característica Write FFE9. Status: {charsResult.Status}");
        }

        _writeCharacteristic = charsResult.Characteristics[0];

        _logger.LogInformation(
            "WT901 Write encontrada: {Uuid} | Properties={Properties}",
            _writeCharacteristic.Uuid,
            _writeCharacteristic.CharacteristicProperties);

        await Task.CompletedTask;
    }

    private async Task RequestBatteryAsync(string description, CancellationToken token)
    {
        _pendingReadRegister = RegisterBatVal;
        _pendingReadRegisterAt = DateTime.Now;
        _lastBatteryRequest = DateTime.Now;

        await WriteWt901CommandAsync(ReadBatteryCommand, description, token);
    }

    private async Task WriteWt901CommandAsync(byte[] command, string description, CancellationToken token)
    {
        if (_writeCharacteristic == null)
            throw new Exception($"No se puede enviar comando WT901 '{description}': característica Write no inicializada");

        using var writer = new DataWriter();
        writer.WriteBytes(command);

        // En muchos BLE UART de WitMotion funciona mejor WriteWithoutResponse.
        var option = _writeCharacteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
            ? GattWriteOption.WriteWithoutResponse
            : GattWriteOption.WriteWithResponse;

        var status = await _writeCharacteristic.WriteValueAsync(writer.DetachBuffer(), option)
            .AsTask(token);

        if (status != GattCommunicationStatus.Success)
        {
            throw new Exception($"Falló comando WT901 '{description}'. Status: {status}");
        }

        _logger.LogInformation(
            "Comando WT901 enviado: {Description} | {Hex}",
            description,
            BitConverter.ToString(command));
    }

    private async Task<(ulong Address, Wt901BleConfig.Wt901DeviceProfile Profile)> FindWt901Async(CancellationToken token)
    {
        var tcs = new TaskCompletionSource<Wt901BleConfig.Wt901DeviceProfile>();

        var orderedProfiles = Wt901BleConfig.Devices
            .Skip(_activeWt901Index)
            .Concat(Wt901BleConfig.Devices.Take(_activeWt901Index))
            .ToArray();

        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        _watcher.Received += (_, args) =>
        {
            string name = args.Advertisement.LocalName ?? "";

            var profile = orderedProfiles.FirstOrDefault(x => x.Mac == args.BluetoothAddress);
            bool matchMac = profile != null;

            bool matchName = Wt901BleConfig.DeviceNames.Any(x =>
                name.Contains(x, StringComparison.OrdinalIgnoreCase));

            _logger.LogInformation(
                "BLE WT901 visto: {Name} | {Address:X} | RSSI {Rssi} | MatchMac={MatchMac} | MatchName={MatchName} | Perfil={Alias}",
                string.IsNullOrWhiteSpace(name) ? "(sin nombre)" : name,
                args.BluetoothAddress,
                args.RawSignalStrengthInDBm,
                matchMac,
                matchName,
                profile?.Alias ?? "(no configurado)");

            // Para no confundirlo con otros BLE, el failover multi-WT901 usa MAC exacta.
            if (matchMac && profile != null)
            {
                tcs.TrySetResult(profile);
            }
        };

        _watcher.Start();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

        using var registration = timeoutCts.Token.Register(() =>
        {
            tcs.TrySetCanceled();
        });

        try
        {
            var profile = await tcs.Task;
            return (profile.Mac, profile);
        }
        catch
        {
            throw new Exception("No se encontró WT901 por BLE");
        }
        finally
        {
            try { _watcher.Stop(); } catch { }
        }
    }

    private bool ShouldFailoverWt901(Exception ex)
    {
        if (Wt901BleConfig.Devices.Length < 2)
            return false;

        var now = DateTime.Now;
        var sinceData = _lastData == DateTime.MinValue
            ? double.MaxValue
            : now.Subtract(_lastData).TotalSeconds;

        var sinceSessionStart = _sessionStartedAt == DateTime.MinValue
            ? double.MaxValue
            : now.Subtract(_sessionStartedAt).TotalSeconds;

        var msg = ex.Message ?? string.Empty;
        bool unstableError =
            msg.Contains("sin datos", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("No se encontró WT901", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("No se pudo abrir", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("No se encontró servicio", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("No se pudo activar Notify", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Falló comando WT901", StringComparison.OrdinalIgnoreCase);

        bool enoughUnstableTime = _lastData == DateTime.MinValue
            ? sinceSessionStart >= DeviceFailoverSeconds
            : sinceData >= DeviceFailoverSeconds;

        return unstableError && enoughUnstableTime;
    }

    private void SwitchToNextWt901(string reason)
    {
        var previous = Wt901BleConfig.Devices[_activeWt901Index];
        _activeWt901Index = (_activeWt901Index + 1) % Wt901BleConfig.Devices.Length;
        var next = Wt901BleConfig.Devices[_activeWt901Index];
        _sessionStartedAt = DateTime.MinValue;
        _lastData = DateTime.MinValue;

        _logger.LogWarning(
            "WT901 failover tras {Seconds}s sin estabilidad: {PreviousAlias} -> {NextAlias}. Motivo={Reason}",
            DeviceFailoverSeconds,
            previous.Alias,
            next.Alias,
            reason);

        _store.UpdateWt901Device($"FAILOVER {previous.Alias}->{next.Alias}", next.Mac);
    }

    private void NotifyCharacteristic_ValueChanged(
        GattCharacteristic sender,
        GattValueChangedEventArgs args)
    {
        try
        {
            byte[] data = args.CharacteristicValue.ToArray();

            _lastData = DateTime.Now;

            if (data.Length == 0)
                return;

            // Algunos WT901 BLE envían un paquete combinado de 20 bytes:
            // 55 61 + acc(6) + gyro(6) + angle(6)
            // Lo manejamos antes del parser de frames de 11 bytes.
            if (data.Length >= 20 && data[0] == 0x55 && data[1] == 0x61)
            {
                ParseCombinedAnglePacket(data);

                if (data.Length == 20)
                    return;
            }

            ProcessReceivedBytes(data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error leyendo datos WT901");
        }
    }

    private void ProcessReceivedBytes(byte[] data)
    {
        lock (_rxLock)
        {
            _rxBuffer.AddRange(data);

            // Evita crecimiento infinito si llega basura BLE.
            if (_rxBuffer.Count > 512)
            {
                _logger.LogWarning(
                    "WT901 RX buffer excedió 512 bytes. Se limpia. Último paquete={Hex}",
                    BitConverter.ToString(data));

                _rxBuffer.Clear();
                return;
            }

            while (true)
            {
                int start = _rxBuffer.IndexOf(0x55);

                if (start < 0)
                {
                    if (_rxBuffer.Count > 0)
                        LogUnknownRaw("WT901 descartando bytes sin cabecera 55", _rxBuffer.ToArray());

                    _rxBuffer.Clear();
                    return;
                }

                if (start > 0)
                {
                    var discarded = _rxBuffer.Take(start).ToArray();
                    _rxBuffer.RemoveRange(0, start);
                    LogUnknownRaw("WT901 descartando basura antes de cabecera 55", discarded);
                }

                // Protocolo normal WitMotion: frames de 11 bytes:
                // 55 [tipo] [8 bytes payload] [checksum]
                if (_rxBuffer.Count < 11)
                    return;

                byte[] frame = _rxBuffer.Take(11).ToArray();
                byte type = frame[1];

                bool checksumOk = IsValidWitChecksum(frame);

                // Caso observado en tu log:
                // 55-71-5C-00-00-00-00-00-00-00-00
                // Viene como respuesta de registro, pero no respeta el checksum estándar.
                // Para 55-71 aceptamos el frame completo si byte[2] trae el registro pedido.
                if (!checksumOk)
                {
                    if (type == 0x71)
                    {
                        _rxBuffer.RemoveRange(0, 11);
                        HandleWitFrame(frame);
                        continue;
                    }

                    LogUnknownRaw("WT901 frame con checksum inválido; se desplaza 1 byte", frame);
                    _rxBuffer.RemoveAt(0);
                    continue;
                }

                _rxBuffer.RemoveRange(0, 11);
                HandleWitFrame(frame);
            }
        }
    }

    private static bool IsValidWitChecksum(byte[] frame)
    {
        if (frame.Length != 11)
            return false;

        int sum = 0;
        for (int i = 0; i < 10; i++)
            sum += frame[i];

        return (byte)sum == frame[10];
    }

    private void HandleWitFrame(byte[] frame)
    {
        byte type = frame[1];

        switch (type)
        {
            case 0x51:
                // Aceleración. No la usamos por ahora.
                break;

            case 0x52:
                // Giroscopio. No lo usamos por ahora.
                break;

            case 0x53:
                ParseAnglePacket(frame);
                break;

            case 0x5F:
                ParseRegisterReadPacket55_5F(frame);
                break;

            case 0x71:
                ParseRegisterReadPacket55_71(frame);
                break;

            default:
                LogUnknownRaw($"WT901 frame 55-{type:X2} recibido pero no interpretado", frame);
                break;
        }
    }

    private void ParseCombinedAnglePacket(byte[] data)
    {
        short rollRaw = BitConverter.ToInt16(data, 14);
        short pitchRaw = BitConverter.ToInt16(data, 16);
        short yawRaw = BitConverter.ToInt16(data, 18);

        double roll = rollRaw / 32768.0 * 180.0;
        double pitch = pitchRaw / 32768.0 * 180.0;
        double yaw = yawRaw / 32768.0 * 180.0;

        _store.UpdateWt901(pitch, roll, yaw);

        LogAngles(pitch, roll, yaw);
    }

    private void ParseAnglePacket(byte[] frame)
    {
        // Frame 55 53:
        // byte 2-3 = Roll, byte 4-5 = Pitch, byte 6-7 = Yaw, little endian.
        short rollRaw = BitConverter.ToInt16(frame, 2);
        short pitchRaw = BitConverter.ToInt16(frame, 4);
        short yawRaw = BitConverter.ToInt16(frame, 6);

        double roll = rollRaw / 32768.0 * 180.0;
        double pitch = pitchRaw / 32768.0 * 180.0;
        double yaw = yawRaw / 32768.0 * 180.0;

        _store.UpdateWt901(pitch, roll, yaw);

        LogAngles(pitch, roll, yaw);
    }

    private void ParseRegisterReadPacket55_5F(byte[] frame)
    {
        // Frame normal WitMotion 55 5F:
        // El primer valor leído queda en bytes 2-3 little endian.
        // La respuesta normalmente NO incluye el número de registro; usamos _pendingReadRegister.
        ushort value0 = BitConverter.ToUInt16(frame, 2);
        ushort value1 = BitConverter.ToUInt16(frame, 4);
        ushort value2 = BitConverter.ToUInt16(frame, 6);
        ushort value3 = BitConverter.ToUInt16(frame, 8);

        int? pending = _pendingReadRegister;
        double secondsSinceRequest = SecondsSincePendingRead();

        _logger.LogInformation(
            "WT901 REGVALUE 55-5F recibido | PendingReg=0x{PendingReg:X2} | SegDesdeReq={Seconds:F1} | V0={V0} V1={V1} V2={V2} V3={V3} | Hex={Hex}",
            pending ?? -1,
            secondsSinceRequest,
            value0,
            value1,
            value2,
            value3,
            BitConverter.ToString(frame));

        if (pending == RegisterBatVal && secondsSinceRequest <= 10)
        {
            StoreBatteryRaw(value0, "55-5F", frame);
        }
    }

    private void ParseRegisterReadPacket55_71(byte[] frame)
    {
        // Caso observado:
        // 55 71 5C 00 00 00 00 00 00 00 00
        // byte 2 = registro leído, byte 3-4 = valor little endian.
        int register = frame[2];
        ushort value = BitConverter.ToUInt16(frame, 3);

        double secondsSinceRequest = SecondsSincePendingRead();

        _logger.LogInformation(
            "WT901 REGVALUE 55-71 recibido | Reg=0x{Reg:X2} | SegDesdeReq={Seconds:F1} | Value={Value} | Hex={Hex}",
            register,
            secondsSinceRequest,
            value,
            BitConverter.ToString(frame));

        if (register == RegisterBatVal)
        {
            StoreBatteryRaw(value, "55-71", frame);
        }
    }

    private double SecondsSincePendingRead()
    {
        return _pendingReadRegisterAt == DateTime.MinValue
            ? double.MaxValue
            : DateTime.Now.Subtract(_pendingReadRegisterAt).TotalSeconds;
    }

    private void StoreBatteryRaw(ushort raw, string sourceFrameType, byte[] frame)
    {
        _store.UpdateWt901BatteryRaw(raw);

        _logger.LogInformation(
            "WT901 BATVAL detectado | Raw={Raw} | Registro=0x5C | FrameType={FrameType} | Hex={Hex}",
            raw,
            sourceFrameType,
            BitConverter.ToString(frame));

        _pendingReadRegister = null;
        _pendingReadRegisterAt = DateTime.MinValue;
    }

    private void LogAngles(double pitch, double roll, double yaw)
    {
        if (DateTime.Now.Subtract(_lastLog).TotalSeconds >= 2)
        {
            _lastLog = DateTime.Now;

            _logger.LogInformation(
                "WT901 ANGULOS -> Pitch={Pitch:F2} Roll={Roll:F2} Yaw={Yaw:F2}",
                pitch,
                roll,
                yaw);
        }
    }

    private void LogUnknownRaw(string message, byte[] data)
    {
        if (DateTime.Now.Subtract(_lastUnknownFrameLog).TotalSeconds >= 5)
        {
            _lastUnknownFrameLog = DateTime.Now;
            _logger.LogInformation("{Message} | Hex={Hex}", message, BitConverter.ToString(data));
        }
    }

    private void Cleanup()
    {
        try
        {
            if (_notifyCharacteristic != null)
            {
                _notifyCharacteristic.ValueChanged -= NotifyCharacteristic_ValueChanged;

                try
                {
                    _notifyCharacteristic
                        .WriteClientCharacteristicConfigurationDescriptorAsync(
                            GattClientCharacteristicConfigurationDescriptorValue.None)
                        .AsTask()
                        .Wait(1500);
                }
                catch { }
            }

            _notifyCharacteristic = null;
            _writeCharacteristic = null;
            _pendingReadRegister = null;
            _pendingReadRegisterAt = DateTime.MinValue;

            lock (_rxLock)
            {
                _rxBuffer.Clear();
            }

            _device?.Dispose();
            _device = null;
        }
        catch
        {
        }
    }
}
