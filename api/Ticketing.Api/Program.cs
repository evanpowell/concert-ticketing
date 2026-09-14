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
app.MapOpenApi();
app.MapScalarApiReference("/", options => options
    .WithTitle("Concert Ticketing API")
    .WithTheme(ScalarTheme.Purple));

app.MapHealthEndpoints();
app.MapShowEndpoints();
app.MapHoldEndpoints();
app.MapOrderEndpoints();

app.Run();

public partial class Program;
