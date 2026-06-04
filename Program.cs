using HomeAssistantAcDefender.Models;
using HomeAssistantAcDefender.Options;
using HomeAssistantAcDefender.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<HomeAssistantOptions>(builder.Configuration.GetSection(HomeAssistantOptions.SectionName));
builder.Services.Configure<DefenderOptions>(builder.Configuration.GetSection(DefenderOptions.SectionName));
builder.Services.AddSingleton<DefenderStateStore>();
builder.Services.AddHttpClient<HomeAssistantClient>();
builder.Services.AddHostedService<AcDefenderWorker>();
builder.Services.AddRazorPages();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseRouting();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.MapGet("/api/status", (DefenderStateStore store) => Results.Ok(store.GetSnapshot()));

app.MapPost("/api/target/generate", (DefenderStateStore store) =>
{
    var snapshot = store.GenerateTarget();
    return Results.Ok(snapshot);
});

app.MapPost("/api/target", (TargetTemperatureRequest request, DefenderStateStore store) =>
{
    var snapshot = store.SetTarget(request.TemperatureCelsius);
    return Results.Ok(snapshot);
});

app.MapPost("/api/defender", (DefenderEnabledRequest request, DefenderStateStore store) =>
{
    var snapshot = store.SetDefenderEnabled(request.Enabled);
    return Results.Ok(snapshot);
});

app.MapPost("/api/dummy", (DummyThermostatRequest request, DefenderStateStore store) =>
{
    var snapshot = store.UpdateDummyThermostat(request);
    return Results.Ok(snapshot);
});

app.Run();
