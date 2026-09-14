using System.Threading.RateLimiting;
using Scalar.AspNetCore;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<TicketingDbContext>(options =>
    options.UseOracle(builder.Configuration.GetConnectionString("Ticketing")));

builder.Services.AddScoped<HoldService>();
builder.Services.AddHostedService<ExpiredHoldSweeper>();

// Only the public demo resets itself. Never enable this anywhere real.
if (builder.Configuration.GetValue<bool>("Ticketing:DemoResetEnabled"))
    builder.Services.AddHostedService<DemoResetService>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

// The public demo has no authentication, so writes are rate limited per client IP.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("writes", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
            }));
});

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy => policy
        .WithOrigins("http://localhost:4200")
        .AllowAnyHeader()
        .AllowAnyMethod()));

var app = builder.Build();

// In containers the database starts empty, so the app applies its own schema.
// Off by default: local development applies migrations explicitly, and a real
// production database should never be migrated by accident.
if (app.Configuration.GetValue<bool>("Ticketing:ApplyMigrationsOnStartup"))
{
    var connectionString = app.Configuration.GetConnectionString("Ticketing")!;
    var migrationsDirectory = app.Configuration["Ticketing:MigrationsDirectory"] ?? "/app/migrations";
    await MigrationRunner.ApplyAsync(connectionString, migrationsDirectory, app.Logger);
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseRateLimiter();

// The deployed link should show something usable, so the interactive API
// reference is served at the root and stays on in production deliberately.
// In production the Angular build lives in wwwroot and is served by this same
// app, so there is one origin and CORS never applies.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapOpenApi();
app.MapScalarApiReference("/docs", options => options
    .WithTitle("Concert Ticketing API")
    .WithTheme(ScalarTheme.Purple));

app.MapHealthEndpoints();
app.MapShowEndpoints();
app.MapHoldEndpoints();
app.MapOrderEndpoints();

// Client-side routes like /shows/1 are not files on disk. Without this a refresh
// on a deep link would 404. Guarded so the API still runs with no frontend built,
// which is how the integration tests run it.
if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html")))
    app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
