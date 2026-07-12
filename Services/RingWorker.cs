using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace MonitorPapa.Api.Services;

public class RingWorker : BackgroundService
{
    private readonly MonitorStateStore _store;
    private readonly DeviceSelectionStore _selectionStore;
    private readonly ILogger<RingWorker> _logger;

    private BluetoothLEDevice? _device;
    private GattCharacteristic? _writeChar;
    private GattCharacteristic? _notifyChar;
    private GattCharacteristic? _batteryChar;

    private DateTime _lastCommunication = DateTime.MinValue;
    private DateTime _lastValidClinicalReading = DateTime.MinValue;
    private DateTime _lastBatteryRead = DateTime.MinValue;
    private DateTime _lastLog = DateTime.MinValue;

    private int _validReadingCount;
    private int _reconnectFailures;
    private int _emptyMeasurementCycles;
    private bool _connectedSequenceOk;

    private TaskCompletionSource<bool>? _measurementCompleted;

    private int _activeRingIndex;
    private RingBleConfig.RingDeviceProfile? _activeRing;
    private DateTime _sessionStartedAt = DateTime.MinValue;

    private const byte CmdSyncTime = 0x01;
    private const byte CmdDeviceInfo = 0x0C;
    private const byte CmdLocale = 0x21;
    private const byte CmdFindPhone = 0x23;
    private const byte CmdMeasurementPacket = 0x24;
    private const byte CmdRealtime = 0x25;
    private const byte CmdAppId = 0x48;
    private const byte CmdStatus = 0x52;

    private const int GreenWaitMs = 15000;
    private const int MeasurementTimeoutSeconds = 180;
    private const int DelayBetweenMeasurementsMs = 30000;

    private const int CommunicationWatchdogSeconds = 90;
    private const int ClinicalWatchdogSeconds = 300;
    private const int MaxSessionWithoutValidReadingSeconds = 300;
    private const int DeviceFailoverSeconds = 180;

    public RingWorker(
        MonitorStateStore store,
        DeviceSelectionStore selectionStore,
        ILogger<RingWorker> logger)
    {
        _store = store;
        _selectionStore = selectionStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RingWorker BLE iniciado en espera de habilitación");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_selectionStore.IsRingEnabled)
                {
                    HardResetRingBle(markDisabled: true);
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }

                await ConnectAndListenAsync(stoppingToken);
                _sessionStartedAt = DateTime.Now;

                await InitSequenceAsync(stoppingToken);
                await PowerOnSequenceAsync(stoppingToken);

                _connectedSequenceOk = true;
                _lastCommunication = DateTime.Now;
                _lastValidClinicalReading = DateTime.MinValue;
                _emptyMeasurementCycles = 0;
                _sessionStartedAt = DateTime.Now;

                _logger.LogInformation(
                    "Ring: esperando {Seconds}s con luz verde antes de pedir SpO2",
                    GreenWaitMs / 1000);

                await Task.Delay(GreenWaitMs, stoppingToken);

                while (!stoppingToken.IsCancellationRequested && _selectionStore.IsRingEnabled)
                {
                    ThrowIfSessionIsStuck();

                    _measurementCompleted = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);

                    _store.SetRingMeasuring();

                    await Spo2SequenceAsync(stoppingToken);

                    var timeoutTask = Task.Delay(
                        TimeSpan.FromSeconds(MeasurementTimeoutSeconds),
                        stoppingToken);

                    var completedTask = await Task.WhenAny(
                        _measurementCompleted.Task,
                        timeoutTask);

                    if (completedTask != _measurementCompleted.Task)
                    {
                        _emptyMeasurementCycles++;

                        var segundosDesdeComunicacion = SecondsSince(_lastCommunication);
                        var hayComunicacionBleReciente = _lastCommunication != DateTime.MinValue &&
                            segundosDesdeComunicacion <= CommunicationWatchdogSeconds;

                        _logger.LogWarning(
                            "Ring: sigue midiendo sin resultado clínico válido dentro de {Seconds}s. Ciclos vacíos={Cycles}. SegDesdeComunicacion={SegCom}s SegDesdeValido={SegValido}s. No se reinicia si BLE sigue vivo.",
                            MeasurementTimeoutSeconds,
                            _emptyMeasurementCycles,
                            segundosDesdeComunicacion,
                            SecondsSince(_lastValidClinicalReading));

                        if (hayComunicacionBleReciente)
                        {
                            _store.SetRingMeasuring();
                        }
                        else
                        {
                            throw new Exception($"Ring sin comunicación BLE reciente durante medición. SegDesdeComunicacion={segundosDesdeComunicacion}");
                        }
                    }

                    ThrowIfSessionIsStuck();

                    if (_batteryChar != null &&
                        DateTime.Now.Subtract(_lastBatteryRead).TotalSeconds >= 60)
                    {
                        await ReadBatteryAsync(stoppingToken);
                    }

                    await Task.Delay(DelayBetweenMeasurementsMs, stoppingToken);
                }

                HardResetRingBle(markDisabled: true);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error/reconexión ring");

                _store.SetRingError(ex.Message);

                if (ShouldFailoverRing(ex))
                    SwitchToNextRing(ex.Message);

                HardResetRingBle(markDisabled: false);

                _reconnectFailures++;

                var delay = GetReconnectDelayMs(ex);

                _logger.LogWarning(
                    "Ring: reset BLE duro terminado. Reintento #{Intento} en {Delay}s. Error={Error}",
                    _reconnectFailures,
                    delay / 1000,
                    ex.Message);

                await Task.Delay(delay, stoppingToken);
            }
        }

        HardResetRingBle(markDisabled: true);
    }

    private void ThrowIfSessionIsStuck()
    {
        if (!_connectedSequenceOk)
            return;

        var now = DateTime.Now;

        if (_lastCommunication != DateTime.MinValue &&
            now.Subtract(_lastCommunication).TotalSeconds > CommunicationWatchdogSeconds)
        {
            throw new Exception($"Ring sin comunicación BLE por más de {CommunicationWatchdogSeconds} segundos");
        }

        if (_lastValidClinicalReading == DateTime.MinValue)
        {
            if (_sessionStartedAt != DateTime.MinValue &&
                now.Subtract(_sessionStartedAt).TotalSeconds > MaxSessionWithoutValidReadingSeconds)
            {
                throw new Exception($"Ring sin primera lectura clínica válida por más de {MaxSessionWithoutValidReadingSeconds} segundos");
            }

            return;
        }

        if (now.Subtract(_lastValidClinicalReading).TotalSeconds > ClinicalWatchdogSeconds)
        {
            throw new Exception($"Ring sin lectura clínica válida por más de {ClinicalWatchdogSeconds} segundos");
        }
    }

    private int GetReconnectDelayMs(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;

        bool gattError =
            msg.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Unreachable", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("OperationCanceled", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("sin comunicación BLE", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("sin primera lectura clínica", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("sin lectura clínica válida", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("sin datos clínicos válidos", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("No se pudieron leer servicios", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("No se pudieron leer características", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("No se pudo activar Notify", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Falló comando ring", StringComparison.OrdinalIgnoreCase);

        if (!gattError)
            return 3000;

        return _reconnectFailures switch
        {
            1 => 5000,
            2 => 10000,
            3 => 15000,
            _ => 20000
        };
    }

    private async Task ConnectAndListenAsync(CancellationToken token)
    {
        HardResetRingBle(markDisabled: false);

        var found = await FindRingAsync(token);
        DeviceInformation info = found.Info;
        _activeRing = found.Profile;

        _logger.LogInformation(
            "Ring encontrado: {Alias} | {Mac} | {Name} | {Id}",
            _activeRing.Alias,
            _activeRing.CanonicalMac,
            info.Name,
            info.Id);

        _device = await BluetoothLEDevice.FromIdAsync(info.Id).AsTask(token);

        if (_device == null)
            throw new Exception("No se pudo abrir BluetoothLEDevice del ring con FromIdAsync");

        _logger.LogInformation(
            "Conectado a ring: {Alias} | {Name}",
            _activeRing?.Alias ?? "(sin alias)",
            _device.Name);

        _store.UpdateRingMac($"{_activeRing?.Alias} {_activeRing?.CanonicalMac}".Trim());

        var servicesResult = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(token);

        if (servicesResult.Status != GattCommunicationStatus.Success)
            throw new Exception($"No se pudieron leer servicios del ring. Status: {servicesResult.Status}");

        foreach (var service in servicesResult.Services)
            _logger.LogInformation("Ring SERVICE {Uuid}", service.Uuid);

        var mainService = servicesResult.Services.FirstOrDefault(s => s.Uuid == RingBleConfig.ServiceUuid);

        if (mainService == null)
            throw new Exception("No se encontró servicio 56FF del ring");

        var allCharsResult = await mainService.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(token);

        if (allCharsResult.Status != GattCommunicationStatus.Success)
            throw new Exception($"No se pudieron leer características del servicio 56FF. Status: {allCharsResult.Status}");

        foreach (var c in allCharsResult.Characteristics)
            _logger.LogInformation("Ring CHAR {Uuid} props={Props}", c.Uuid, c.CharacteristicProperties);

        _writeChar = allCharsResult.Characteristics.FirstOrDefault(c => c.Uuid == RingBleConfig.WriteCharacteristicUuid);
        _notifyChar = allCharsResult.Characteristics.FirstOrDefault(c => c.Uuid == RingBleConfig.NotifyCharacteristicUuid);

        if (_writeChar == null || _notifyChar == null)
            throw new Exception("No se encontró 33F3 write o 33F4 notify del ring");

        _notifyChar.ValueChanged += NotifyCharOnValueChanged;

        var cccd = await _notifyChar
            .WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify)
            .AsTask(token);

        if (cccd != GattCommunicationStatus.Success)
            throw new Exception($"No se pudo activar Notify en ring. Status: {cccd}");

        _batteryChar = await TryGetBatteryCharacteristicAsync(servicesResult.Services, token);

        if (_batteryChar != null)
        {
            _logger.LogInformation("Ring: característica estándar de batería encontrada (180F/2A19). Leyendo batería inicial...");
            await ReadBatteryAsync(token);
        }
        else
        {
            _logger.LogWarning("Ring: no se encontró característica estándar de batería 180F/2A19");
        }

        _lastCommunication = DateTime.Now;
        _lastValidClinicalReading = DateTime.MinValue;
        _validReadingCount = 0;
        _emptyMeasurementCycles = 0;
        _connectedSequenceOk = false;

        _store.SetRingConnected();

        if (_reconnectFailures > 0)
            _reconnectFailures--;
    }

    private async Task<GattCharacteristic?> TryGetBatteryCharacteristicAsync(
        IReadOnlyList<GattDeviceService> services,
        CancellationToken token)
    {
        var batteryService = services.FirstOrDefault(s => s.Uuid == RingBleConfig.BatteryServiceUuid);

        if (batteryService == null)
        {
            _logger.LogWarning("Ring: servicio Battery 180F no encontrado");
            return null;
        }

        try
        {
            var chars = await batteryService
                .GetCharacteristicsForUuidAsync(
                    RingBleConfig.BatteryLevelCharacteristicUuid,
                    BluetoothCacheMode.Uncached)
                .AsTask(token);

            if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0)
            {
                _logger.LogWarning(
                    "Ring: no se pudo obtener BatteryLevel 2A19. Status={Status} Count={Count}",
                    chars.Status,
                    chars.Characteristics.Count);

                return null;
            }

            return chars.Characteristics[0];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ring: error obteniendo característica de batería");
            return null;
        }
    }

    private async Task<(DeviceInformation Info, RingBleConfig.RingDeviceProfile Profile)> FindRingAsync(CancellationToken token)
    {
        _logger.LogInformation(
            "Ring: escaneando con DeviceInformation. Preferido={Alias}",
            RingBleConfig.Devices[_activeRingIndex].Alias);

        var selector = BluetoothLEDevice.GetDeviceSelector();
        var devices = await DeviceInformation.FindAllAsync(selector).AsTask(token);

        var orderedProfiles = RingBleConfig.Devices
            .Skip(_activeRingIndex)
            .Concat(RingBleConfig.Devices.Take(_activeRingIndex))
            .ToArray();

        foreach (var profile in orderedProfiles)
        {
            foreach (var dev in devices.OrderByDescending(d => d.Name == "SMART_RING").ThenBy(d => d.Name))
            {
                var name = string.IsNullOrWhiteSpace(dev.Name) ? "(sin nombre)" : dev.Name;
                var id = dev.Id ?? string.Empty;
                var idLower = id.ToLowerInvariant();

                bool matchName = RingBleConfig.DeviceNames.Any(x =>
                    name.Contains(x, StringComparison.OrdinalIgnoreCase));

                bool matchId = profile.MatchFragments.Any(x =>
                {
                    var raw = x.ToLowerInvariant();
                    var withoutColon = raw.Replace(":", "");
                    return idLower.Contains(raw) || idLower.Contains(withoutColon);
                });

                _logger.LogInformation(
                    "Ring BLE visto: {Name} | Perfil={Alias} | MatchName={MatchName} | MatchId={MatchId} | Id={Id}",
                    name,
                    profile.Alias,
                    matchName,
                    matchId,
                    id);

                if (matchId)
                    return (dev, profile);
            }
        }

        foreach (var dev in devices.OrderByDescending(d => d.Name == "SMART_RING").ThenBy(d => d.Name))
        {
            var name = string.IsNullOrWhiteSpace(dev.Name) ? "(sin nombre)" : dev.Name;

            bool matchName = RingBleConfig.DeviceNames.Any(x =>
                name.Contains(x, StringComparison.OrdinalIgnoreCase));

            if (matchName)
                return (dev, RingBleConfig.Devices[_activeRingIndex]);
        }

        throw new Exception("No se encontró SMART_RING por BLE usando DeviceInformation");
    }

    private bool ShouldFailoverRing(Exception ex)
    {
        if (RingBleConfig.Devices.Length < 2)
            return false;

        var now = DateTime.Now;

        var sinceValid = _lastValidClinicalReading == DateTime.MinValue
            ? double.MaxValue
            : now.Subtract(_lastValidClinicalReading).TotalSeconds;

        var sinceSessionStart = _sessionStartedAt == DateTime.MinValue
            ? double.MaxValue
            : now.Subtract(_sessionStartedAt).TotalSeconds;

        var msg = ex.Message ?? string.Empty;

        bool unstableError =
            msg.Contains("Unreachable", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("operation was canceled", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("OperationCanceled", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("sin comunicación BLE", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("sin primera lectura clínica", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("sin lectura clínica válida", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("sin datos clínicos válidos", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("No se pudieron leer servicios", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("No se pudieron leer características", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Falló comando ring", StringComparison.OrdinalIgnoreCase);

        bool enoughUnstableTime = _lastValidClinicalReading == DateTime.MinValue
            ? sinceSessionStart >= DeviceFailoverSeconds
            : sinceValid >= DeviceFailoverSeconds;

        return unstableError && enoughUnstableTime;
    }

    private void SwitchToNextRing(string reason)
    {
        var previous = RingBleConfig.Devices[_activeRingIndex];

        _activeRingIndex = (_activeRingIndex + 1) % RingBleConfig.Devices.Length;

        var next = RingBleConfig.Devices[_activeRingIndex];

        _sessionStartedAt = DateTime.MinValue;
        _lastValidClinicalReading = DateTime.MinValue;
        _emptyMeasurementCycles = 0;
        _reconnectFailures = 0;

        _logger.LogWarning(
            "Ring failover tras {Seconds}s sin estabilidad: {PreviousAlias} -> {NextAlias}. Motivo={Reason}",
            DeviceFailoverSeconds,
            previous.Alias,
            next.Alias,
            reason);

        _store.UpdateRingMac($"FAILOVER {previous.Alias}->{next.Alias} | {next.CanonicalMac}");
    }

    private async Task InitSequenceAsync(CancellationToken token)
    {
        _logger.LogInformation("Ring: INIT");

        await SendCommandAsync(CmdSyncTime, BuildSyncTimePayload(), token);
        await Task.Delay(300, token);

        await SendCommandAsync(CmdAppId, BuildFixedAsciiPayload("Hedc7bfb7-b8b5-4dfa", 19), token);
        await Task.Delay(300, token);

        await SendCommandAsync(CmdStatus, new byte[] { 0, 0, 0, 0, 1 }, token);
        await Task.Delay(300, token);

        await SendCommandAsync(CmdLocale, BuildFixedAsciiPayload("es-CL", 19), token);
        await Task.Delay(300, token);

        await SendCommandAsync(CmdDeviceInfo, null, token);
    }

    private async Task PowerOnSequenceAsync(CancellationToken token)
    {
        _logger.LogInformation("Ring: POWER ON / WAKE");

        await SendCommandAsync(CmdSyncTime, BuildSyncTimePayload(), token);
        await Task.Delay(300, token);

        await SendCommandAsync(CmdLocale, BuildFixedAsciiPayload("es-CL", 19), token);
        await Task.Delay(300, token);

        await SendCommandAsync(CmdFindPhone, new byte[] { 0x02 }, token);
        await Task.Delay(800, token);

        await SendCommandAsync(CmdFindPhone, new byte[] { 0x00 }, token);
        await Task.Delay(800, token);

        await SendCommandAsync(CmdFindPhone, new byte[] { 0x02 }, token);
        await Task.Delay(800, token);

        await SendCommandAsync(CmdFindPhone, new byte[] { 0x00 }, token);
        await Task.Delay(800, token);

        await SendCommandAsync(0x04, new byte[] { 0x05 }, token);
        await Task.Delay(800, token);

        await SendCommandAsync(0x04, new byte[] { 0x05 }, token);
    }

    private async Task Spo2SequenceAsync(CancellationToken token)
    {
        _logger.LogInformation("Ring: SPO2 SEQUENCE / debería encender LEDs rojos");

        await SendCommandAsync(CmdSyncTime, BuildSyncTimePayload(), token);
        await Task.Delay(300, token);

        await SendCommandAsync(CmdAppId, BuildFixedAsciiPayload("Hedc7bfb7-b8b5-4dfa", 19), token);
        await Task.Delay(300, token);

        await SendCommandAsync(0x10, new byte[] { 0x06 }, token);
        await Task.Delay(250, token);

        await SendCommandAsync(CmdStatus, new byte[] { 0, 0, 0, 0, 1 }, token);
        await Task.Delay(250, token);

        await SendCommandAsync(CmdLocale, BuildFixedAsciiPayload("es-CL", 19), token);
        await Task.Delay(250, token);

        await SendCommandAsync(0x16, new byte[] { 0x06 }, token);
        await Task.Delay(250, token);

        await SendCommandAsync(0x39, new byte[] { 0x06 }, token);
        await Task.Delay(250, token);

        await SendCommandAsync(CmdStatus, new byte[] { 0, 0, 0, 0, 2 }, token);
        await Task.Delay(250, token);

        await SendCommandAsync(0x40, new byte[] { 0x06 }, token);
        await Task.Delay(250, token);

        await SendCommandAsync(CmdStatus, new byte[] { 0, 0, 0, 0, 1 }, token);
        await Task.Delay(250, token);

        await SendCommandAsync(0x55, new byte[] { 0x06 }, token);
        await Task.Delay(250, token);

        await SendCommandAsync(CmdFindPhone, new byte[] { 0x02 }, token);
        await Task.Delay(300, token);

        await SendCommandAsync(CmdRealtime, new byte[] { 0x06 }, token);
        await Task.Delay(700, token);

        await SendCommandAsync(0x10, new byte[] { 0x05 }, token);
        await Task.Delay(250, token);

        await SendCommandAsync(0x16, new byte[] { 0x05 }, token);
        await Task.Delay(250, token);

        await SendCommandAsync(0x39, new byte[] { 0x05 }, token);
        await Task.Delay(250, token);

        await SendCommandAsync(0x40, new byte[] { 0x05 }, token);
        await Task.Delay(250, token);

        await SendCommandAsync(0x55, new byte[] { 0x05 }, token);
        await Task.Delay(250, token);

        await SendCommandAsync(CmdRealtime, new byte[] { 0x05 }, token);
        await Task.Delay(700, token);

        await SendCommandAsync(0x10, new byte[] { 0x04 }, token);
        await Task.Delay(250, token);

        await SendCommandAsync(0x16, new byte[] { 0x04 }, token);
        await Task.Delay(250, token);

        await SendCommandAsync(0x39, new byte[] { 0x04 }, token);
    }

    private async Task ReadBatteryAsync(CancellationToken token)
    {
        if (_batteryChar == null)
            return;

        try
        {
            var result = await _batteryChar.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(token);

            if (result.Status != GattCommunicationStatus.Success)
            {
                _logger.LogWarning(
                    "Ring: no se pudo leer batería estándar 180F/2A19. Status={Status}",
                    result.Status);

                return;
            }

            var bytes = result.Value.ToArray();

            if (bytes.Length < 1)
            {
                _logger.LogWarning("Ring: batería estándar 180F/2A19 respondió sin bytes");
                return;
            }

            int battery = bytes[0];

            if (battery is < 0 or > 100)
            {
                _logger.LogWarning(
                    "Ring: batería estándar fuera de rango. Raw={Raw} Hex={Hex}",
                    battery,
                    ToHex(bytes));

                return;
            }

            _lastBatteryRead = DateTime.Now;
            _lastCommunication = DateTime.Now;

            _store.UpdateRingBattery(battery);

            _logger.LogInformation(
                "Ring batería estándar 180F/2A19 = {Battery}% | Hex={Hex}",
                battery,
                ToHex(bytes));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ring: error leyendo batería estándar 180F/2A19");
        }
    }

    private async Task SendCommandAsync(byte cmd, byte[]? payload, CancellationToken token)
    {
        if (_writeChar == null)
            throw new Exception($"No se puede enviar comando ring 0x{cmd:X2}: característica Write no inicializada");

        var packet = new byte[20];
        packet[0] = cmd;

        if (payload != null)
            Array.Copy(payload, 0, packet, 1, Math.Min(payload.Length, 19));

        using var writer = new DataWriter();
        writer.WriteBytes(packet);

        var status = await _writeChar
            .WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse)
            .AsTask(token);

        _logger.LogInformation("Ring TX 0x{Cmd:X2}: {Hex} => {Status}", cmd, ToHex(packet), status);

        if (status != GattCommunicationStatus.Success)
            throw new Exception($"Falló comando ring 0x{cmd:X2}. Status: {status}");

        _lastCommunication = DateTime.Now;
    }

    private void NotifyCharOnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            byte[] data = args.CharacteristicValue.ToArray();

            if (data.Length == 0)
                return;

            _lastCommunication = DateTime.Now;

            _logger.LogInformation("Ring RX 0x{Cmd:X2}: {Hex}", data[0], ToHex(data));

            switch (data[0])
            {
                case CmdDeviceInfo:
                    if (data.Length >= 9)
                    {
                        var mac = string.Join(
                            ":",
                            data.Skip(3).Take(6).Reverse().Select(b => b.ToString("X2")));

                        _store.UpdateRingMac(mac);
                    }
                    break;

                case 0x0B:
                    _logger.LogDebug("Ring RX 0x0B ignorado para batería. Hex={Hex}", ToHex(data));
                    break;

                case CmdMeasurementPacket:
                    ParseMeasurementPacket24(data);
                    break;

                case 0x28:
                    // IMPORTANTE:
                    // 0x28 parece ser ACK/keep-alive, pero NO trae SpO2/pulso.
                    // Antes esto completaba la medición y evitaba que el timeout reconectara.
                    _store.SetRingConnected();
                    _logger.LogDebug("Ring RX 0x28 recibido; no completa medición clínica.");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parseando datos ring");
        }
    }

    private void ParseMeasurementPacket24(byte[] data)
    {
        if (data.Length < 9)
            return;

        byte estado = data[1];

        byte pulse = data[3];
        byte spo2 = data[4];
        byte metric = data[8];

        _lastCommunication = DateTime.Now;

        _store.TouchRingCommunication(estado, metric);

        if (spo2 < 70 || spo2 > 100 || pulse < 30 || pulse > 220)
        {
            if (DateTime.Now.Subtract(_lastLog).TotalSeconds >= 10)
            {
                _lastLog = DateTime.Now;

                _logger.LogInformation(
                    "Ring midiendo todavía: Estado=0x{Estado:X2} SpO2Raw={Spo2} PulsoRaw={Pulso} Metric={Metric} SegDesdeValido={SegValido}s",
                    estado,
                    spo2,
                    pulse,
                    metric,
                    SecondsSince(_lastValidClinicalReading));
            }

            return;
        }

        _lastValidClinicalReading = DateTime.Now;
        _validReadingCount++;
        _emptyMeasurementCycles = 0;

        _store.UpdateRingMeasurement(spo2, pulse, estado, metric);
        _measurementCompleted?.TrySetResult(true);

        if (_validReadingCount % 10 == 0 && _batteryChar != null)
            _ = Task.Run(async () => await ReadBatteryAsync(CancellationToken.None));

        if (DateTime.Now.Subtract(_lastLog).TotalSeconds >= 10)
        {
            _lastLog = DateTime.Now;

            _logger.LogInformation(
                "Ring SpO2={Spo2}% Pulso={Pulso} Metric={Metric} Lecturas={Lecturas}",
                spo2,
                pulse,
                metric,
                _validReadingCount);
        }
    }

    private void HardResetRingBle(bool markDisabled)
    {
        try
        {
            _logger.LogInformation("Ring: iniciando reset BLE duro. markDisabled={MarkDisabled}", markDisabled);

            try
            {
                if (_notifyChar != null)
                    _notifyChar.ValueChanged -= NotifyCharOnValueChanged;
            }
            catch { }

            try
            {
                if (_notifyChar != null)
                {
                    _notifyChar
                        .WriteClientCharacteristicConfigurationDescriptorAsync(
                            GattClientCharacteristicConfigurationDescriptorValue.None)
                        .AsTask()
                        .Wait(2000);
                }
            }
            catch { }

            Thread.Sleep(500);

            _notifyChar = null;
            _writeChar = null;
            _batteryChar = null;

            try
            {
                _device?.Dispose();
            }
            catch { }

            _device = null;

            _measurementCompleted?.TrySetCanceled();

            _connectedSequenceOk = false;
            _lastCommunication = DateTime.MinValue;
            _lastValidClinicalReading = DateTime.MinValue;
            _emptyMeasurementCycles = 0;

            if (markDisabled)
                _store.SetRingDisabled();
            else
                _store.SetRingError("Ring en reconexión BLE");

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Thread.Sleep(2000);
        }
        catch
        {
        }
    }

    private static int SecondsSince(DateTime dt)
    {
        if (dt == DateTime.MinValue)
            return -1;

        return Math.Max(0, (int)Math.Round(DateTime.Now.Subtract(dt).TotalSeconds));
    }

    private static byte[] BuildSyncTimePayload()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var ts = BitConverter.GetBytes((uint)now);

        return new byte[]
        {
            ts[0],
            ts[1],
            ts[2],
            ts[3],
            0xFC
        };
    }

    private static byte[] BuildFixedAsciiPayload(string value, int maxPayloadBytes)
    {
        var bytes = new byte[maxPayloadBytes];
        var ascii = Encoding.ASCII.GetBytes(value);

        Array.Copy(ascii, 0, bytes, 0, Math.Min(ascii.Length, bytes.Length));

        return bytes;
    }

    private static string ToHex(byte[] data)
    {
        return BitConverter.ToString(data).Replace("-", " ");
    }
}