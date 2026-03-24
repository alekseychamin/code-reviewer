using System.Text.Json.Serialization;
using TfsReviewPlatform.Application.DependencyInjection;
using TfsReviewPlatform.Infrastructure.DependencyInjection;
using TfsReviewPlatform.Integrations.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.AddOpenApi();
builder.Services.AddCors(options =>
{
    options.AddPolicy("frontend", policy =>
    {
        policy.WithOrigins("http://localhost:5173", "http://localhost:3000")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddApplication(builder.Configuration);
builder.Services.AddInfrastructure();
builder.Services.AddIntegrations();

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
    options.SingleLine = true;
});

var app = builder.Build();

app.MapOpenApi();
app.UseCors("frontend");
app.UseHttpsRedirection();
app.MapControllers();

app.Run();
