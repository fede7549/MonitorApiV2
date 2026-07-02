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
    private DateTime _startedAt = DateTime.Now;

    private const int LoopDelayMs = 1000;
    private const int StartupGraceSeconds = 60;
    private const int RepeatSameAlertMinutes = 5;
    private const int SupinoConfirmSeconds = 30;
    private const int SinDatosSeconds = 30;
    private const int SinDatosRingSeconds = 90;

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
        bool startupGrace = DateTime.Now.Subtract(_startedAt).TotalSeconds < StartupGraceSeconds;

        if (!startupGrace && d.Oximetro && (!ox.Conectado || state.SegundosUltimaLecturaOximetro == null || state.SegundosUltimaLecturaOximetro > SinDatosSeconds))
        {
            return new AlertDecision("OXIMETRO_SIN_DATOS", 100,
                "❌ **OXÍMETRO SIN DATOS / DESCONECTADO**\n" +
                $"Última lectura: {FormatSeconds(state.SegundosUltimaLecturaOximetro)}\n" +
                $"Estado API: {ox.Estado}\nError: {ox.Error ?? "sin detalle"}\nHora: {DateTime.Now:HH:mm:ss}");
        }

        if (!startupGrace && d.Ring && (!ring.Conectado || state.SegundosUltimaLecturaRing == null || state.SegundosUltimaLecturaRing > SinDatosRingSeconds))
        {
            return new AlertDecision("RING_SIN_DATOS", 98,
                "❌ **ANILLO / RING SIN COMUNICACIÓN BLE**\n" +
                $"Última señal BLE: {FormatSeconds(state.SegundosUltimaLecturaRing)}\n" +
                $"Estado API: {ring.Estado}\nError: {ring.Error ?? "sin detalle"}\nHora: {DateTime.Now:HH:mm:ss}");
        }

        var clinical = GetClinicalSource(state);
        if (clinical.Spo2.HasValue && clinical.Spo2.Value > 20 && clinical.Spo2.Value < 85)
        {
            return new AlertDecision("SPO2_CRITICA", 95,
                $"🚨 **SpO₂ CRÍTICA ({clinical.Source})**\n" +
                $"SpO₂: **{clinical.Spo2.Value}%**\nPulso: {FormatNullable(clinical.Pulse)} bpm\n" +
                $"Postura: {wt.Posicion ?? "SIN_POSICION"}\nHora: {DateTime.Now:HH:mm:ss}");
        }

        if (clinical.Spo2.HasValue && clinical.Spo2.Value >= 85 && clinical.Spo2.Value < 90)
        {
            return new AlertDecision("SPO2_ALERTA", 90,
                $"⚠️ **SpO₂ BAJA ({clinical.Source})**\n" +
                $"SpO₂: **{clinical.Spo2.Value}%**\nPulso: {FormatNullable(clinical.Pulse)} bpm\n" +
                $"Postura: {wt.Posicion ?? "SIN_POSICION"}\nHora: {DateTime.Now:HH:mm:ss}");
        }

        bool supinoAhora = d.Wt901 && (wt.Posicion == "BOCA_ARRIBA" || state.AlertaSupino);
        if (supinoAhora)
        {
            if (_supinoStartedAt == DateTime.MinValue)
                _supinoStartedAt = DateTime.Now;
        }
        else
        {
            _supinoStartedAt = DateTime.MinValue;
        }

        double supinoSeconds = _supinoStartedAt == DateTime.MinValue ? 0 : DateTime.Now.Subtract(_supinoStartedAt).TotalSeconds;
        if (supinoAhora && supinoSeconds >= SupinoConfirmSeconds)
        {
            return new AlertDecision("SUPINO_CONFIRMADO", 85,
                $"🛌 **BOCA ARRIBA > {SupinoConfirmSeconds}s**\n" +
                $"Postura: **{wt.Posicion}**\nSpO₂: {FormatNullable(clinical.Spo2)}% ({clinical.Source})\n" +
                $"Pulso: {FormatNullable(clinical.Pulse)} bpm\nHora: {DateTime.Now:HH:mm:ss}");
        }

        if (!startupGrace && d.Wt901 && (!wt.Conectado || state.SegundosUltimaLecturaWt901 == null || state.SegundosUltimaLecturaWt901 > SinDatosSeconds))
        {
            return new AlertDecision("WT901_SIN_DATOS", 70,
                "❌ **WT901 SIN DATOS / DESCONECTADO**\n" +
                $"Última lectura: {FormatSeconds(state.SegundosUltimaLecturaWt901)}\n" +
                $"Estado API: {wt.Estado}\nError: {wt.Error ?? "sin detalle"}\nHora: {DateTime.Now:HH:mm:ss}");
        }

        return new AlertDecision("OK", 0,
            "✅ **Monitor Papá volvió a OK**\n" +
            $"SpO₂: {FormatNullable(clinical.Spo2)}% ({clinical.Source})\n" +
            $"Pulso: {FormatNullable(clinical.Pulse)} bpm\n" +
            $"Postura: {wt.Posicion ?? "--"}\nHora: {DateTime.Now:HH:mm:ss}");
    }

    private static ClinicalSource GetClinicalSource(MonitorState state)
    {
        if (state.Dispositivos.Oximetro && state.Oximetro.Spo2.HasValue)
            return new ClinicalSource("Oxímetro", state.Oximetro.Spo2, state.Oximetro.Pulso);

        if (state.Dispositivos.Ring && state.Ring.Spo2.HasValue)
            return new ClinicalSource("Ring", state.Ring.Spo2, state.Ring.Pulso);

        return new ClinicalSource("Sin fuente", null, null);
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
