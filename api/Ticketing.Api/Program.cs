using Ticketing.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapHealthEndpoints();

app.Run();

// Exposes the implicit Program class to WebApplicationFactory in the test project.
public partial class Program;
