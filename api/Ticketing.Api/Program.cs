using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<TicketingDbContext>(options =>
    options.UseOracle(builder.Configuration.GetConnectionString("Ticketing")));

builder.Services.AddScoped<HoldService>();

var app = builder.Build();

app.MapHealthEndpoints();
app.MapShowEndpoints();
app.MapHoldEndpoints();

app.Run();

public partial class Program;
