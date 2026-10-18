using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace APIEntradasSaidas.Data;

public sealed class Contextfactory : IDesignTimeDbContextFactory<APIcontext>
{
    public APIcontext CreateDbContext(string[] args)
    {
        // Funciona tanto a partir da raiz do workspace como da pasta do projeto.
        var currentDirectory = Directory.GetCurrentDirectory();
        var projectDirectory = File.Exists(Path.Combine(currentDirectory, "APIEntradasSaidas.csproj"))
            ? currentDirectory
            : Path.Combine(currentDirectory, "APIEntradasSaidas");

        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? "Production";

        var configuration = new ConfigurationBuilder()
        .SetBasePath(projectDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .AddJsonFile($"appsettings.{environment}.json", optional: true)
        .AddEnvironmentVariables()
        .Build();

        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Missing ConnectionStrings:DefaultConnection. Configure it in appsettings.json or via environment variables.");
        }

        var optionsBuilder = new DbContextOptionsBuilder<APIcontext>();
        optionsBuilder.UseSqlServer(
            connectionString,
            sqlServerOptions =>
            {
                sqlServerOptions.EnableRetryOnFailure();
            });

        return new APIcontext(optionsBuilder.Options);
    }
}
