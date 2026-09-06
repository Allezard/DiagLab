using DiagLab.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.AddSingleton<CatalogService>();
builder.Services.AddSingleton<ReportService>();
builder.Services.AddSingleton<RatesService>();
builder.Services.AddSingleton<InventoryService>();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddHostedService<PriceFeedWorker>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.MapControllers();

app.MapGet("/", () => Results.Text("""
    DiagLab — учебный сервис для курса «Диагностика и профилирование .NET».

    GET /api/health
    GET /api/catalog/search?q=<строка>
    GET /api/reports/export?rows=<число>
    GET /api/reports/csv?rows=<число>
    GET /api/notifications/subscribe?user=<имя>
    GET /api/rates?currency=<код>
    GET /api/rates/ping
    GET /api/inventory/reserve?product=<id>&qty=<число>
    GET /api/sessions/open?user=<имя>
    GET /api/sessions/close?sessionId=<id>
    GET /api/stats

    Описание OpenAPI: /openapi/v1.json
    """));

app.Run();