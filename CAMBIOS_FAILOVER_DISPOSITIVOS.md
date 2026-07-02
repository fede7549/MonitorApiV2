# Cambios agregados: failover Ring + WT901

## Dispositivos configurados

### SMART_RING
1. RINGNEGRO: 41:42:7e:93:aa:c5
2. RINGPLATEADO: 41:42:cf:53:e7:2c

### WT901BLE67
1. WT901_1: fe:eb:6c:89:c7:31
2. WT901_2: f1:1d:68:b2:80:b5

## Lógica aplicada

- El sistema intenta primero el dispositivo principal:
  - RINGNEGRO
  - WT901_1
- Si la conexión queda inestable, se fuerza failover al segundo dispositivo:
  - RINGPLATEADO
  - WT901_2
- El tiempo de failover por inestabilidad queda en 180 segundos.
- Se mantiene reconexión rápida interna para microcortes, pero no se cambia de dispositivo por cada corte breve.

## Archivos modificados

- Services/RingBleConfig.cs
- Services/Wt901BleConfig.cs
- Services/RingWorker.cs
- Services/Wt901Worker.cs
- Models/Wt901State.cs
- Services/MonitorStateStore.cs

## Nota

No pude compilar dentro del entorno porque aquí no está instalado `dotnet`, pero el proyecto fue editado directamente sobre el código fuente extraído.
