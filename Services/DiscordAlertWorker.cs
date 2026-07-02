using System.Net.Http.Json;
using MonitorPapa.Api.Models;

namespace MonitorPapa.Api.Services;

public class DiscordAlertWorker : BackgroundService
{
    private readonly MonitorStateStore _store;
    private readonly ILogger<DiscordAlertWorker> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string? _discordWebhookUrl;

    private string _lastAlertKey = "OK";
    private DateTime _lastSentAt = DateTime.MinValue;
    private DateTime _supinoStartedAt = DateTime.MinValue;
    private DateTime _sinSpo2ConfiableStartedAt = DateTime.MinValue;
    private DateTime _spo2CriticoStartedAt = DateTime.MinValue;
    private DateTime _spo2AlertaStartedAt = DateTime.MinValue;
    private DateTime _spo2PrecaucionStartedAt = DateTime.MinValue;
    private DateTime _startedAt = DateTime.Now;

    private const int LoopDelayMs = 1000;
    private const int StartupGraceSeconds = 60;
    private const int RepeatSameAlertMinutes = 5;

    // Alineado con index_v5.html.
    private const int OxRecentSeconds = 15;
    private const int RingRecentSeconds = 45;
    private const int WtRecentSeconds = 15;

    private const int SupinoConfirmSeconds = 15;
    private const int SinSpo2ConfiableConfirmSeconds = 10;
    private const int Spo2CriticoConfirmSeconds = 3;
    private const int Spo2AlertaConfirmSeconds = 7;
    private const int Spo2PrecaucionConfirmSeconds = 12;

    public DiscordAlertWorker(
        MonitorStateStore store,
        ILogger<DiscordAlertWorker> logger,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory)
    {
        _store = store;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _discordWebhookUrl =
            configuration["DiscordWebhookUrl"] ??
            configuration["_discordWebhookUrl"] ??
            configuration["Discord:WebhookUrl"];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _startedAt = DateTime.Now;

        if (string.IsNullOrWhiteSpace(_discordWebhookUrl))
        {
            _logger.LogWarning("DiscordAlertWorker iniciado sin webhook configurado.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var decision = BuildAlertDecision(_store.GetSnapshot());

                if (ShouldSend(decision))
                {
                    await SendDiscordAsync(decision, stoppingToken);
                    _lastAlertKey = decision.Key;
                    _lastSentAt = DateTime.Now;
                }

                await Task.Delay(LoopDelayMs, stoppingToken);
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en DiscordAlertWorker");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private AlertDecision BuildAlertDecision(MonitorState state)
    {
        var d = state.Dispositivos;
        var ox = state.Oximetro;
        var wt = state.Wt901;
        var ring = state.Ring;

        var now = DateTime.Now;
        bool startupGrace = now.Subtract(_startedAt).TotalSeconds < StartupGraceSeconds;

        bool oxConfiable = IsOxConfiable(state);
        bool ringConfiable = IsRingConfiable(state);
        bool wtConfiable = IsWtConfiable(state);

        bool oxNormal = IsOxNormal(state);
        bool ringNormal = IsRingNormal(state);
        bool algunaSpo2Confiable = oxConfiable || ringConfiable;
        bool algunaSpo2Normal = oxNormal || ringNormal;

        var clinical = GetClinicalSource(state);
        bool spo2Valida = clinical.Spo2.HasValue && clinical.Spo2.Value > 20;

        bool supinoAhora = d.Wt901 && (wt.Posicion == "BOCA_ARRIBA" || state.AlertaSupino);
        if (supinoAhora)
        {
            if (_supinoStartedAt == DateTime.MinValue)
                _supinoStartedAt = now;
        }
        else
        {
            _supinoStartedAt = DateTime.MinValue;
        }

        bool supinoConfirmado = _supinoStartedAt != DateTime.MinValue &&
                                now.Subtract(_supinoStartedAt).TotalSeconds >= SupinoConfirmSeconds;

        bool oxBajoPeroRingNormal =
            clinical.Source == "Oxímetro" &&
            spo2Valida && clinical.Spo2!.Value < 94 &&
            ringNormal &&
            !supinoConfirmado;

        bool ringBajoPeroOxNormal =
            clinical.Source == "Ring" &&
            spo2Valida && clinical.Spo2!.Value < 94 &&
            oxNormal &&
            !supinoConfirmado;

        var alertas = new List<AlertDecision>();

        if (!startupGrace && (d.Oximetro || d.Ring))
        {
            bool sinSpo2Confiable = !algunaSpo2Confiable;
            bool sinSpo2Confirmado = ConfirmCondition(
                sinSpo2Confiable,
                ref _sinSpo2ConfiableStartedAt,
                SinSpo2ConfiableConfirmSeconds,
                now);

            if (sinSpo2Confirmado)
            {
                alertas.Add(new AlertDecision("SIN_SPO2_CONFIABLE", 105,
                    "🚨 **SIN SpO₂ CONFIABLE**\n" +
                    "Oxímetro/ring sin lectura útil para vigilancia.\n" +
                    $"Oxímetro: {(ox.Conectado ? "conectado" : "desconectado")}, última lectura {FormatSeconds(state.SegundosUltimaLecturaOximetro)}, SpO₂ {FormatNullable(ox.Spo2)}%\n" +
                    $"Ring: {(ring.Conectado ? "conectado" : "desconectado")}, última lectura {FormatSeconds(state.SegundosUltimaLecturaRing)}, SpO₂ {FormatNullable(ring.Spo2)}%\n" +
                    $"Postura: {wt.Posicion ?? "--"}\nHora: {now:HH:mm:ss}"));
            }
        }
        else
        {
            _sinSpo2ConfiableStartedAt = DateTime.MinValue;
        }

        if (!startupGrace && d.Oximetro)
        {
            bool oxSinDatos = !ox.Conectado ||
                              state.SegundosUltimaLecturaOximetro == null ||
                              state.SegundosUltimaLecturaOximetro > OxRecentSeconds;

            if (oxSinDatos && !ringNormal)
            {
                alertas.Add(new AlertDecision("OXIMETRO_SIN_DATOS", 96,
                    "❌ **OXÍMETRO SIN DATOS / DESCONECTADO**\n" +
                    $"Última lectura: {FormatSeconds(state.SegundosUltimaLecturaOximetro)}\n" +
                    $"Estado API: {ox.Estado}\nError: {ox.Error ?? "sin detalle"}\n" +
                    $"Ring: SpO₂ {FormatNullable(ring.Spo2)}%, {(ringConfiable ? "confiable" : "no confiable")}\n" +
                    $"Postura: {wt.Posicion ?? "--"}\nHora: {now:HH:mm:ss}"));
            }
        }

        if (!startupGrace && d.Ring)
        {
            bool ringSinDatos = !ring.Conectado ||
                                state.SegundosUltimaLecturaRing == null ||
                                state.SegundosUltimaLecturaRing > RingRecentSeconds;

            // Mejora principal: si el oxímetro está normal y reciente, no mandar Discord por microdesconexiones del ring.
            if (ringSinDatos && !oxNormal)
            {
                alertas.Add(new AlertDecision("RING_SIN_DATOS", 94,
                    "❌ **ANILLO / RING SIN COMUNICACIÓN ÚTIL**\n" +
                    $"Última señal BLE: {FormatSeconds(state.SegundosUltimaLecturaRing)}\n" +
                    $"Estado API: {ring.Estado}\nError: {ring.Error ?? "sin detalle"}\n" +
                    $"Oxímetro: SpO₂ {FormatNullable(ox.Spo2)}%, {(oxConfiable ? "confiable" : "no confiable")}\n" +
                    $"Postura: {wt.Posicion ?? "--"}\nHora: {now:HH:mm:ss}"));
            }
        }

        if (spo2Valida)
        {
            bool spo2Menor80 = clinical.Spo2!.Value < 80;
            bool spo2Critico = clinical.Spo2.Value >= 80 && clinical.Spo2.Value < 85;
            bool spo2Alerta = clinical.Spo2.Value >= 85 && clinical.Spo2.Value < 90;
            bool spo2Precaucion = clinical.Spo2.Value >= 90 && clinical.Spo2.Value < 94;

            // Si el oxímetro baja por movimiento, pero el ring está normal y no hay supino confirmado,
            // se corta la confirmación para no mandar Discord por falso bajo.
            if (oxBajoPeroRingNormal || ringBajoPeroOxNormal)
            {
                _spo2CriticoStartedAt = DateTime.MinValue;
                _spo2AlertaStartedAt = DateTime.MinValue;
                _spo2PrecaucionStartedAt = DateTime.MinValue;
            }
            else
            {
                bool criticoConfirmado = ConfirmCondition(
                    spo2Critico,
                    ref _spo2CriticoStartedAt,
                    Spo2CriticoConfirmSeconds,
                    now);

                bool alertaConfirmada = ConfirmCondition(
                    spo2Alerta,
                    ref _spo2AlertaStartedAt,
                    Spo2AlertaConfirmSeconds,
                    now);

                bool precaucionConfirmada = ConfirmCondition(
                    spo2Precaucion,
                    ref _spo2PrecaucionStartedAt,
                    Spo2PrecaucionConfirmSeconds,
                    now);

                if (spo2Menor80 || criticoConfirmado)
                {
                    alertas.Add(new AlertDecision("SPO2_CRITICA", 100,
                        $"🚨 **SpO₂ CRÍTICA ({clinical.Source})**\n" +
                        $"SpO₂: **{clinical.Spo2.Value}%**\nPulso: {FormatNullable(clinical.Pulse)} bpm\n" +
                        $"Oxímetro: {FormatNullable(ox.Spo2)}% / Ring: {FormatNullable(ring.Spo2)}%\n" +
                        $"Postura: {wt.Posicion ?? "SIN_POSICION"}\nHora: {now:HH:mm:ss}"));
                }
                else if (alertaConfirmada)
                {
                    alertas.Add(new AlertDecision("SPO2_ALERTA", 90,
                        $"⚠️ **SpO₂ BAJA 85-89% sostenida ({clinical.Source})**\n" +
                        $"SpO₂: **{clinical.Spo2.Value}%**\nPulso: {FormatNullable(clinical.Pulse)} bpm\n" +
                        $"Oxímetro: {FormatNullable(ox.Spo2)}% / Ring: {FormatNullable(ring.Spo2)}%\n" +
                        $"Postura: {wt.Posicion ?? "SIN_POSICION"}\nHora: {now:HH:mm:ss}"));
                }
                else if (precaucionConfirmada)
                {
                    alertas.Add(new AlertDecision("SPO2_PRECAUCION", 75,
                        $"⚠️ **SpO₂ PRECAUCIÓN 90-93% sostenida ({clinical.Source})**\n" +
                        $"SpO₂: **{clinical.Spo2.Value}%**\nPulso: {FormatNullable(clinical.Pulse)} bpm\n" +
                        $"Oxímetro: {FormatNullable(ox.Spo2)}% / Ring: {FormatNullable(ring.Spo2)}%\n" +
                        $"Postura: {wt.Posicion ?? "SIN_POSICION"}\nHora: {now:HH:mm:ss}"));
                }
            }
        }
        else
        {
            _spo2CriticoStartedAt = DateTime.MinValue;
            _spo2AlertaStartedAt = DateTime.MinValue;
            _spo2PrecaucionStartedAt = DateTime.MinValue;
        }

        if (supinoConfirmado && spo2Valida && clinical.Spo2!.Value < 94)
        {
            alertas.Add(new AlertDecision("SUPINO_CONFIRMADO", 98,
                $"🛌 **BOCA ARRIBA > {SupinoConfirmSeconds}s Y SpO₂ < 94%**\n" +
                $"Postura: **{wt.Posicion}**\n" +
                $"SpO₂: **{clinical.Spo2.Value}%** ({clinical.Source})\n" +
                $"Pulso: {FormatNullable(clinical.Pulse)} bpm\nHora: {now:HH:mm:ss}"));
        }

        if (!startupGrace && d.Wt901)
        {
            bool wtSinDatos = !wt.Conectado ||
                              state.SegundosUltimaLecturaWt901 == null ||
                              state.SegundosUltimaLecturaWt901 > WtRecentSeconds;

            // Si alguna fuente de SpO₂ está normal, no mandar Discord por caída aislada del WT901.
            if (wtSinDatos && !algunaSpo2Normal)
            {
                alertas.Add(new AlertDecision("WT901_SIN_DATOS", 65,
                    "❌ **WT901 SIN DATOS / DESCONECTADO**\n" +
                    $"Última lectura: {FormatSeconds(state.SegundosUltimaLecturaWt901)}\n" +
                    $"Estado API: {wt.Estado}\nError: {wt.Error ?? "sin detalle"}\n" +
                    $"SpO₂ clínica: {FormatNullable(clinical.Spo2)}% ({clinical.Source})\n" +
                    $"Hora: {now:HH:mm:ss}"));
            }
        }

        if (alertas.Count > 0)
        {
            return alertas
                .OrderByDescending(x => x.Severity)
                .ThenBy(x => x.Key)
                .First();
        }

        return new AlertDecision("OK", 0,
            "✅ **Monitor Papá volvió a OK**\n" +
            $"SpO₂: {FormatNullable(clinical.Spo2)}% ({clinical.Source})\n" +
            $"Pulso: {FormatNullable(clinical.Pulse)} bpm\n" +
            $"Postura: {wt.Posicion ?? "--"}\nHora: {now:HH:mm:ss}");
    }

    private static bool IsOxConfiable(MonitorState state)
    {
        return state.Dispositivos.Oximetro &&
               state.Oximetro.Conectado &&
               state.Oximetro.Spo2.HasValue &&
               state.Oximetro.Spo2.Value > 20 &&
               state.SegundosUltimaLecturaOximetro.HasValue &&
               state.SegundosUltimaLecturaOximetro.Value <= OxRecentSeconds;
    }

    private static bool IsRingConfiable(MonitorState state)
    {
        return state.Dispositivos.Ring &&
               state.Ring.Conectado &&
               state.Ring.Spo2.HasValue &&
               state.Ring.Spo2.Value > 20 &&
               state.SegundosUltimaLecturaRing.HasValue &&
               state.SegundosUltimaLecturaRing.Value <= RingRecentSeconds;
    }

    private static bool IsWtConfiable(MonitorState state)
    {
        return state.Dispositivos.Wt901 &&
               state.Wt901.Conectado &&
               state.SegundosUltimaLecturaWt901.HasValue &&
               state.SegundosUltimaLecturaWt901.Value <= WtRecentSeconds;
    }

    private static bool IsOxNormal(MonitorState state)
    {
        return IsOxConfiable(state) && state.Oximetro.Spo2!.Value >= 94;
    }

    private static bool IsRingNormal(MonitorState state)
    {
        return IsRingConfiable(state) && state.Ring.Spo2!.Value >= 94;
    }

    private static ClinicalSource GetClinicalSource(MonitorState state)
    {
        if (IsOxConfiable(state))
            return new ClinicalSource("Oxímetro", state.Oximetro.Spo2, state.Oximetro.Pulso);

        if (IsRingConfiable(state))
            return new ClinicalSource("Ring", state.Ring.Spo2, state.Ring.Pulso);

        if (state.Dispositivos.Oximetro && state.Oximetro.Spo2.HasValue && state.Oximetro.Spo2.Value > 20)
            return new ClinicalSource("Oxímetro no reciente", state.Oximetro.Spo2, state.Oximetro.Pulso);

        if (state.Dispositivos.Ring && state.Ring.Spo2.HasValue && state.Ring.Spo2.Value > 20)
            return new ClinicalSource("Ring no reciente", state.Ring.Spo2, state.Ring.Pulso);

        return new ClinicalSource("Sin fuente", null, null);
    }

    private static bool ConfirmCondition(bool condition, ref DateTime startedAt, int seconds, DateTime now)
    {
        if (!condition)
        {
            startedAt = DateTime.MinValue;
            return false;
        }

        if (startedAt == DateTime.MinValue)
            startedAt = now;

        return now.Subtract(startedAt).TotalSeconds >= seconds;
    }

    private bool ShouldSend(AlertDecision decision)
    {
        if (decision.Key == "OK" && _lastAlertKey == "OK") return false;
        if (decision.Key == "OK" && _lastAlertKey != "OK") return true;
        if (decision.Key != _lastAlertKey) return true;
        return _lastSentAt == DateTime.MinValue || DateTime.Now.Subtract(_lastSentAt).TotalMinutes >= RepeatSameAlertMinutes;
    }

    private async Task SendDiscordAsync(AlertDecision decision, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(_discordWebhookUrl)) return;

        var client = _httpClientFactory.CreateClient("DiscordWebhook");
        using var response = await client.PostAsJsonAsync(_discordWebhookUrl, new { content = decision.Message }, token);

        if (!response.IsSuccessStatusCode)
            _logger.LogWarning("Discord respondió error. Status={StatusCode} Body={Body}", response.StatusCode, await response.Content.ReadAsStringAsync(token));
        else
            _logger.LogInformation("Alerta Discord enviada: {AlertKey}", decision.Key);
    }

    private static string FormatNullable(int? value) => value.HasValue ? value.Value.ToString() : "--";
    private static string FormatSeconds(int? seconds) => seconds.HasValue ? $"hace {seconds.Value}s" : "sin lectura";

    private sealed record ClinicalSource(string Source, int? Spo2, int? Pulse);
    private sealed record AlertDecision(string Key, int Severity, string Message);
}
