using LucidCartographer.Components;
using LucidCartographer.Data;
using LucidCartographer.Services;
using LucidCartographer.Services.Export;
using LucidCartographer.Services.Import;
using LucidCartographer.Services.Operations;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var dbPath = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Production"
    ? "/data/cartographer.db"
    : "data/cartographer.db";
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddScoped<PoiService>();
builder.Services.AddScoped<IFileImporter, GpxImporter>();
builder.Services.AddScoped<IFileImporter, KmlImporter>();
builder.Services.AddScoped<IFileImporter, GeoJsonImporter>();
builder.Services.AddScoped<IFileImporter, CsvImporter>();
builder.Services.AddScoped<ImportOrchestrator>();
builder.Services.AddScoped<KmlExporter>();
builder.Services.AddScoped<GpxExporter>();
builder.Services.AddScoped<PoiMatcher>();
builder.Services.AddScoped<SetOperationService>();
builder.Services.AddScoped<GoogleMapsListScraper>();
builder.Services.AddScoped<IMapService, LeafletMapService>();

var app = builder.Build();

// Ensure database is created
using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseAntiforgery();

app.UseStaticFiles();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
