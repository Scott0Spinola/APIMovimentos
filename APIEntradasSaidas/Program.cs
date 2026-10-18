

using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.OpenApi;
using Microsoft.Extensions.Options;
using APIEntradasSaidas.Auth;
using APIEntradasSaidas.Integrations;
using APIEntradasSaidas.Services;

using APIEntradasSaidas.Data;
using Serilog;


var builder = WebApplication.CreateBuilder(args);


builder.Host.UseSerilog((context, services, loggerConfiguration) =>
{
    var logDirectory = Path.Combine(context.HostingEnvironment.ContentRootPath, "Logs");
    Directory.CreateDirectory(logDirectory);

    loggerConfiguration
        .Enrich.FromLogContext()
        .MinimumLevel.Information()
        .WriteTo.Console()
        .WriteTo.File(
            Path.Combine(logDirectory, "log-.txt"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7);
});

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Configura ConnectionStrings:DefaultConnection antes de iniciar a API.");
}

builder.Services.AddDbContext<APIcontext>(options =>
    options.UseSqlServer(
        connectionString,
        sqlServerOptions =>
        {
            sqlServerOptions.EnableRetryOnFailure();
        }));


builder.Services.AddOptions<StocksApiOptions>()
    .BindConfiguration("StocksApi")
    .Validate(options => Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) &&
        (uri.Scheme == "https" || uri.Scheme == "http") && string.IsNullOrEmpty(uri.UserInfo),
        "StocksApi:BaseUrl deve ser um endereço HTTP ou HTTPS absoluto.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.ApiKey), "Falta StocksApi:ApiKey.");
builder.Services.AddHttpClient<IStocksApiClient, StocksApiClient>((services, client) =>
{
    var options = services.GetRequiredService<IOptions<StocksApiOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
    client.DefaultRequestHeaders.Add("X-API-KEY", options.ApiKey);
    client.Timeout = TimeSpan.FromSeconds(30);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<EntregaService>();
builder.Services.AddScoped<StocksClientesService>();
builder.Services.AddSingleton<EntregaExcelService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition(ApiKeyAuthenticationHandler.SchemeName, new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        Name = ApiKeyAuthenticationHandler.HeaderName,
        In = ParameterLocation.Header
    });
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        { new OpenApiSecuritySchemeReference(ApiKeyAuthenticationHandler.SchemeName, document), new List<string>() }
    });
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseSerilogRequestLogging();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers().RequireAuthorization();




app.Run();

public partial class Program;
