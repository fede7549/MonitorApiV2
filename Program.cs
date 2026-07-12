using MonitorPapa.Api.Models;
using MonitorPapa.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService();

// =========================
// Habilitar CORS
// =========================
builder.Services.AddCors(options =>
{
    options.AddPolicy("PermitirTodo", policy =>
    {
        policy
            .AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddSingleton<DeviceSelectionStore>();
builder.Services.AddSingleton<JsonHistoryService>();
builder.Services.AddSingleton<MonitorStateStore>();

builder.Services.AddHttpClient("DiscordWebhook");

builder.Services.AddHostedService(sp => sp.GetRequiredService<JsonHistoryService>());
builder.Services.AddHostedService<OximetroWorker>();
builder.Services.AddHostedService<Wt901Worker>();
builder.Services.AddHostedService<RingWorker>();
builder.Services.AddHostedService<DiscordAlertWorker>();

// Comentar o eliminar este si no se necesita escaneo diagnóstico BLE.
// builder.Services.AddHostedService<BleScannerWorker>();

var app = builder.Build();

// =========================
// Activar CORS
// =========================
app.UseCors("PermitirTodo");

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api", () => "Monitor Papá API funcionando");

app.MapGet("/estado", (MonitorStateStore store) =>
    Results.Ok(store.GetSnapshot()));

app.MapGet("/oximetro", (MonitorStateStore store) =>
    Results.Ok(store.GetSnapshot().Oximetro));

app.MapGet("/postura", (MonitorStateStore store) =>
    Results.Ok(store.GetSnapshot().Wt901));

app.MapGet("/ring", (MonitorStateStore store) =>
    Results.Ok(store.GetSnapshot().Ring));

app.MapGet("/dispositivos", (DeviceSelectionStore selectionStore) =>
    Results.Ok(selectionStore.GetSnapshot()));

app.MapPost("/dispositivos",
    (DeviceSelectionState selection, DeviceSelectionStore selectionStore) =>
    {
        selectionStore.Update(selection);
        return Results.Ok(selectionStore.GetSnapshot());
    });

app.MapGet("/test", (MonitorStateStore store) =>
{
    store.UpdateOximetro(99, 60);
    store.UpdateRingMeasurement(98, 62, 0, 0);
    return Results.Ok("OK");
});

app.Run();