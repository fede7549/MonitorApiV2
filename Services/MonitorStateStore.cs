using MonitorPapa.Api.Models;

namespace MonitorPapa.Api.Services;

public class MonitorStateStore
{
    private readonly object _lock = new();
    private readonly MonitorState _state = new();
    private readonly DeviceSelectionStore _selectionStore;
    private readonly JsonHistoryService _history;

    public MonitorStateStore(DeviceSelectionStore selectionStore, JsonHistoryService history)
    {
        _selectionStore = selectionStore;
        _history = history;
    }

    public MonitorState GetSnapshot()
    {
        lock (_lock)
        {
            return new MonitorState
            {
                Timestamp = DateTime.Now,
                Dispositivos = _selectionStore.GetSnapshot(),
                Oximetro = new OximetroState
                {
                    Conectado = _state.Oximetro.Conectado,
                    Estado = _state.Oximetro.Estado,
                    Spo2 = _state.Oximetro.Spo2,
                    Pulso = _state.Oximetro.Pulso,
                    UltimaLectura = _state.Oximetro.UltimaLectura,
                    Error = _state.Oximetro.Error
                },
                Wt901 = new Wt901State
                {
                    Conectado = _state.Wt901.Conectado,
                    Estado = _state.Wt901.Estado,
                    Posicion = _state.Wt901.Posicion,
                    Pitch = _state.Wt901.Pitch,
                    Roll = _state.Wt901.Roll,
                    Yaw = _state.Wt901.Yaw,
                    BatteryRaw = _state.Wt901.BatteryRaw,
                    UltimaLecturaBateria = _state.Wt901.UltimaLecturaBateria,
                    UltimaLectura = _state.Wt901.UltimaLectura,
                    Alias = _state.Wt901.Alias,
                    Mac = _state.Wt901.Mac,
                    Error = _state.Wt901.Error
                },
                Ring = new RingState
                {
                    Conectado = _state.Ring.Conectado,
                    Estado = _state.Ring.Estado,
                    Spo2 = _state.Ring.Spo2,
                    Pulso = _state.Ring.Pulso,
                    Bateria = _state.Ring.Bateria,
                    LecturasValidas = _state.Ring.LecturasValidas,
                    EstadoMedicion = _state.Ring.EstadoMedicion,
                    MetricaRaw = _state.Ring.MetricaRaw,
                    UltimaLectura = _state.Ring.UltimaLectura,
                    UltimaComunicacion = _state.Ring.UltimaComunicacion,
                    UltimaLecturaBateria = _state.Ring.UltimaLecturaBateria,
                    EnMedicion = _state.Ring.EnMedicion,
                    Mac = _state.Ring.Mac,
                    Error = _state.Ring.Error
                }
            };
        }
    }

    public void UpdateOximetro(int spo2, int pulso)
    {
        var now = DateTime.Now;
        var estado = ClasificarSpo2(spo2);

        lock (_lock)
        {
            _state.Oximetro.Conectado = true;
            _state.Oximetro.Spo2 = spo2;
            _state.Oximetro.Pulso = pulso;
            _state.Oximetro.UltimaLectura = now;
            _state.Oximetro.Error = null;
            _state.Oximetro.Estado = estado;
        }

        _history.Enqueue("oximetro", "lectura", new
        {
            conectado = true,
            estado,
            spo2,
            pulso,
            ultimaLectura = now
        });
    }

    public void UpdateRingMeasurement(int spo2, int pulso, byte estado, byte metricaRaw)
    {
        var now = DateTime.Now;
        var estadoClinico = ClasificarSpo2(spo2);
        int lecturasValidas;

        lock (_lock)
        {
            _state.Ring.Conectado = true;
            _state.Ring.Spo2 = spo2;
            _state.Ring.Pulso = pulso;
            _state.Ring.EstadoMedicion = estado;
            _state.Ring.MetricaRaw = metricaRaw;
            _state.Ring.LecturasValidas++;
            lecturasValidas = _state.Ring.LecturasValidas;
            _state.Ring.UltimaLectura = now;
            _state.Ring.UltimaComunicacion = now;
            _state.Ring.EnMedicion = false;
            _state.Ring.Error = null;
            _state.Ring.Estado = estadoClinico;
        }

        _history.Enqueue("ring", "lectura", new
        {
            conectado = true,
            estado = estadoClinico,
            spo2,
            pulso,
            estadoMedicion = estado,
            metricaRaw,
            lecturasValidas,
            ultimaLectura = now
        });
    }

    public void UpdateRingBattery(int bateria)
    {
        var now = DateTime.Now;

        lock (_lock)
        {
            _state.Ring.Conectado = true;
            _state.Ring.Bateria = bateria;
            _state.Ring.UltimaLecturaBateria = now;
            _state.Ring.UltimaComunicacion = now;
            _state.Ring.Error = null;
        }

        _history.Enqueue("ring", "bateria", new
        {
            conectado = true,
            bateria,
            ultimaLecturaBateria = now
        });
    }

    public void UpdateRingMac(string mac)
    {
        lock (_lock)
        {
            _state.Ring.Mac = mac;
        }

        _history.Enqueue("ring", "mac", new { mac });
    }

    public void SetRingConnected()
    {
        var now = DateTime.Now;
        bool shouldLog;

        lock (_lock)
        {
            shouldLog = !_state.Ring.Conectado || _state.Ring.Estado != "CONECTADO" || _state.Ring.Error != null;

            _state.Ring.Conectado = true;
            _state.Ring.Estado = "CONECTADO";
            _state.Ring.UltimaComunicacion = now;
            _state.Ring.EnMedicion = false;
            _state.Ring.Error = null;
        }

        if (shouldLog)
        {
            _history.Enqueue("ring", "evento", new
            {
                conectado = true,
                estado = "CONECTADO",
                ultimaComunicacion = now
            });
        }
    }

    public void SetRingMeasuring()
    {
        var now = DateTime.Now;

        lock (_lock)
        {
            _state.Ring.Conectado = true;
            _state.Ring.Estado = "MIDIENDO";
            _state.Ring.EnMedicion = true;
            _state.Ring.UltimaComunicacion = now;
            _state.Ring.Error = null;
        }

        _history.Enqueue("ring", "evento", new
        {
            conectado = true,
            estado = "MIDIENDO",
            enMedicion = true,
            ultimaComunicacion = now
        });
    }

    public void TouchRingCommunication(byte? estadoMedicion = null, byte? metricaRaw = null)
    {
        lock (_lock)
        {
            _state.Ring.Conectado = true;
            _state.Ring.UltimaComunicacion = DateTime.Now;
            _state.Ring.EnMedicion = true;
            if (_state.Ring.Spo2 == null)
                _state.Ring.Estado = "MIDIENDO";
            if (estadoMedicion.HasValue)
                _state.Ring.EstadoMedicion = estadoMedicion.Value;
            if (metricaRaw.HasValue)
                _state.Ring.MetricaRaw = metricaRaw.Value;
            _state.Ring.Error = null;
        }
    }

    public void SetRingError(string error)
    {
        lock (_lock)
        {
            _state.Ring.Conectado = false;
            _state.Ring.Estado = "RECONECTANDO";
            _state.Ring.EnMedicion = false;
            _state.Ring.Error = error;
        }

        _history.Enqueue("ring", "error", new
        {
            conectado = false,
            estado = "RECONECTANDO",
            error
        });
    }

    public void SetRingDisabled()
    {
        bool shouldLog;

        lock (_lock)
        {
            shouldLog = _state.Ring.Conectado || _state.Ring.Estado != "DESHABILITADO" || _state.Ring.Error != null;

            _state.Ring.Conectado = false;
            _state.Ring.Estado = "DESHABILITADO";
            _state.Ring.EnMedicion = false;
            _state.Ring.Error = null;
        }

        if (shouldLog)
        {
            _history.Enqueue("ring", "evento", new
            {
                conectado = false,
                estado = "DESHABILITADO"
            });
        }
    }

    public void UpdateWt901(double pitch, double roll, double yaw)
    {
        var now = DateTime.Now;
        var posicion = CalcularPosicion(pitch, roll);

        lock (_lock)
        {
            _state.Wt901.Conectado = true;
            _state.Wt901.Pitch = pitch;
            _state.Wt901.Roll = roll;
            _state.Wt901.Yaw = yaw;
            _state.Wt901.UltimaLectura = now;
            _state.Wt901.Error = null;
            _state.Wt901.Posicion = posicion;
            _state.Wt901.Estado = "OK";
        }

        _history.Enqueue("wt901", "lectura", new
        {
            conectado = true,
            estado = "OK",
            posicion,
            pitch,
            roll,
            yaw,
            ultimaLectura = now
        });
    }

    public void UpdateWt901BatteryRaw(ushort batteryRaw)
    {
        var now = DateTime.Now;

        lock (_lock)
        {
            _state.Wt901.Conectado = true;
            _state.Wt901.BatteryRaw = batteryRaw;
            _state.Wt901.UltimaLecturaBateria = now;
            _state.Wt901.Error = null;
        }

        _history.Enqueue("wt901", "bateria", new
        {
            conectado = true,
            batteryRaw,
            ultimaLecturaBateria = now
        });
    }


    public void UpdateWt901Device(string? alias, ulong? mac)
    {
        string? macText = mac.HasValue ? mac.Value.ToString("X12") : null;

        lock (_lock)
        {
            _state.Wt901.Alias = alias;
            _state.Wt901.Mac = macText;
        }

        _history.Enqueue("wt901", "dispositivo", new
        {
            alias,
            mac = macText
        });
    }

    public void SetOximetroError(string error)
    {
        lock (_lock)
        {
            _state.Oximetro.Conectado = false;
            _state.Oximetro.Estado = "RECONECTANDO";
            _state.Oximetro.Error = error;
        }

        _history.Enqueue("oximetro", "error", new
        {
            conectado = false,
            estado = "RECONECTANDO",
            error
        });
    }

    public void SetOximetroDisabled()
    {
        bool shouldLog;

        lock (_lock)
        {
            shouldLog = _state.Oximetro.Conectado || _state.Oximetro.Estado != "DESHABILITADO" || _state.Oximetro.Error != null;

            _state.Oximetro.Conectado = false;
            _state.Oximetro.Estado = "DESHABILITADO";
            _state.Oximetro.Error = null;
        }

        if (shouldLog)
        {
            _history.Enqueue("oximetro", "evento", new
            {
                conectado = false,
                estado = "DESHABILITADO"
            });
        }
    }

    public void SetWt901Error(string error)
    {
        lock (_lock)
        {
            _state.Wt901.Conectado = false;
            _state.Wt901.Estado = "RECONECTANDO";
            _state.Wt901.Error = error;
        }

        _history.Enqueue("wt901", "error", new
        {
            conectado = false,
            estado = "RECONECTANDO",
            error
        });
    }

    public void SetWt901Disabled()
    {
        bool shouldLog;

        lock (_lock)
        {
            shouldLog = _state.Wt901.Conectado || _state.Wt901.Estado != "DESHABILITADO" || _state.Wt901.Error != null;

            _state.Wt901.Conectado = false;
            _state.Wt901.Estado = "DESHABILITADO";
            _state.Wt901.Error = null;
        }

        if (shouldLog)
        {
            _history.Enqueue("wt901", "evento", new
            {
                conectado = false,
                estado = "DESHABILITADO"
            });
        }
    }

    private static string ClasificarSpo2(int spo2) => spo2 switch
    {
        >= 94 => "OK",
        >= 90 => "PRECAUCION",
        >= 85 => "ALERTA",
        _ => "CRITICO"
    };

    private static string CalcularPosicionAntes(double pitch, double roll)
    {
        if (roll >= 55 && roll <= 125 && pitch > -45 && pitch < 45) return "DE_PIE_O_SENTADO";
        if (roll > -25 && roll < 25 && pitch > -45 && pitch < 45) return "BOCA_ARRIBA";
        if (pitch >= 45 && pitch < 65) return "LEVEMENTE_IZQUIERDA";
        if (pitch <= -45 && pitch > -65) return "LEVEMENTE_DERECHA";
        if (pitch >= 65) return "LATERAL_IZQUIERDO";
        if (pitch <= -65) return "LATERAL_DERECHO";
        if (roll <= -70 && roll >= -120 && pitch > -45 && pitch < 45) return "CABEZA_ABAJO";
        return "INDETERMINADO";
    }

    private static string CalcularPosicion(double pitch, double roll)
    {
        // Boca arriba únicamente si ambos ángulos están realmente cerca del centro.
        if (Math.Abs(roll) <= 20 && Math.Abs(pitch) <= 20)
            return "BOCA_ARRIBA";

        // De pie o sentado.
        if (roll >= 55 && roll <= 125 && Math.Abs(pitch) <= 30)
            return "DE_PIE_O_SENTADO";

        // Lateral derecho.
        if (pitch <= -30)
            return "LATERAL_DERECHO";

        // Lateral izquierdo.
        if (pitch >= 30)
            return "LATERAL_IZQUIERDO";

        // Boca abajo.
        if (roll <= -70 && roll >= -120)
            return "CABEZA_ABAJO";

        return "INDETERMINADO";
    }
}
