using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using APIEntradasSaidas.Data;
using APIEntradasSaidas.Integrations;
using APIEntradasSaidas.Models;
using APIEntradasSaidas.Services;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace APIEntradasSaidas.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    public StocksHandler Stocks { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../APIEntradasSaidas")));
        builder.UseEnvironment("Development");
        builder.UseSetting("Authentication:ApiKey", "test-inbound-key");
        builder.UseSetting("StocksApi:ApiKey", "test-stocks-key");
        builder.UseSetting("StocksApi:BaseUrl", "https://stocks.test/");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<APIcontext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<APIcontext>>();
            connection.Open();
            using var command = connection.CreateCommand();
            // SQL equivalente para testar as transações e a chave única em SQLite.
            command.CommandText = """
                CREATE TABLE Entregas (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    IdCliente INTEGER NOT NULL UNIQUE,
                    NomeCliente TEXT NOT NULL,
                    Entidade TEXT NOT NULL,
                    Unidade TEXT NOT NULL,
                    DataPrevista TEXT NOT NULL,
                    PeriodoHorario TEXT NOT NULL CHECK (PeriodoHorario IN ('Manhã', 'Tarde')),
                    PeriodicidadeDias INTEGER NOT NULL CHECK (PeriodicidadeDias > 0),
                    Observacoes TEXT NULL
                );
                """;
            command.ExecuteNonQuery();
            services.AddDbContext<APIcontext>(options => options.UseSqlite(connection));
            services.AddHttpClient<IStocksApiClient, StocksApiClient>()
                .ConfigurePrimaryHttpMessageHandler(() => Stocks);
        });
    }

    public HttpClient AuthorizedClient()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("X-API-KEY", "test-inbound-key");
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            connection.Dispose();
    }
}

public sealed class StocksHandler : HttpMessageHandler
{
    public bool Fail { get; set; }
    public List<string> RequestedPaths { get; } = [];
    public Dictionary<int, StocksPagina<StocksCliente>> ExportPages { get; } = new()
    {
        [1] = new([new(1, 10, "Hotel Azul", "001"), new(2, 10, "Hotel Verde", "002")], false)
    };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Assert.Equal("test-stocks-key", Assert.Single(request.Headers.GetValues("X-API-KEY")));
        var path = request.RequestUri!.PathAndQuery;
        RequestedPaths.Add(path);
        if (Fail)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        object? body = path switch
        {
            "/api/Cliente/1" => new StocksCliente(1, 10, "Hotel Azul", "001"),
            "/api/Cliente/2" => new StocksCliente(2, 10, "Hotel Verde", "002"),
            "/api/Entidade/10" => new StocksEntidade(10, "Grupo Hotéis", null),
            "/api/Cliente?pageNumber=2&pageSize=25" => new StocksPagina<StocksCliente>([new(2, 10, "Hotel Verde", "002")], false),
            "/api/Cliente?pageNumber=1&pageSize=200" => ExportPages.GetValueOrDefault(1),
            "/api/Cliente?pageNumber=2&pageSize=200" => ExportPages.GetValueOrDefault(2),
            _ => null
        };
        return Task.FromResult(body is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
    }
}

public sealed class ApiTests
{
    private const string Root = "/api/proximas-descargas";

    private static object Request(int id = 1, string periodo = "Manhã", int dias = 7) => new
    {
        idCliente = id, dataPrevista = "2026-09-25", periodoHorario = periodo,
        periodicidadeDias = dias, observacoes = "Entregar na receção"
    };

    [Fact]
    public async Task SwaggerDescribesExcelUpload()
    {
        using var factory = new ApiFactory();
        using var client = factory.AuthorizedClient();
        var document = await client.GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");
        var content = document.GetProperty("paths").GetProperty(Root + "/importar").GetProperty("post")
            .GetProperty("requestBody").GetProperty("content");
        Assert.True(content.TryGetProperty("multipart/form-data", out _));
    }

    [Fact]
    public async Task AuthenticationIsRequired()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Root)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/stocks/clientes/exportar")).StatusCode);
    }

    [Fact]
    public async Task StocksExcelExportsAllPagesAndLeavesScheduleFieldsBlank()
    {
        using var factory = new ApiFactory();
        // Ordem invertida entre páginas para confirmar a ordenação final por ID.
        factory.Stocks.ExportPages[1] = new([new(2, 10, "Hotel Verde", "002")], true);
        factory.Stocks.ExportPages[2] = new([new(1, 10, "Hotel Azul", "001")], false);
        using var client = factory.AuthorizedClient();

        var response = await client.GetAsync("/api/stocks/clientes/exportar");
        response.EnsureSuccessStatusCode();
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("clientes_stocks_proximas_descargas.xlsx", response.Content.Headers.ContentDisposition!.ToString());
        using var workbook = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        var sheet = workbook.Worksheet(1);
        Assert.Equal(3, sheet.LastRowUsed(XLCellsUsedOptions.Contents)!.RowNumber());
        for (var col = 1; col <= 6; col++)
            Assert.Equal(EntregaExcelService.Colunas[col - 1], sheet.Cell(1, col).GetString());
        Assert.Equal(1, sheet.Cell(2, 1).GetValue<int>());
        Assert.Equal("Hotel Azul", sheet.Cell(2, 2).GetString());
        Assert.Equal(2, sheet.Cell(3, 1).GetValue<int>());
        Assert.Equal("Hotel Verde", sheet.Cell(3, 2).GetString());
        Assert.All(sheet.Range(2, 3, 3, 6).Cells(), cell => Assert.True(cell.IsEmpty(XLCellsUsedOptions.Contents)));
        Assert.Contains("/api/Cliente?pageNumber=1&pageSize=200", factory.Stocks.RequestedPaths);
        Assert.Contains("/api/Cliente?pageNumber=2&pageSize=200", factory.Stocks.RequestedPaths);

        // Gerar o ficheiro não deve criar configurações com datas ou períodos fictícios.
        var localData = await client.GetFromJsonAsync<JsonElement>(Root);
        Assert.Equal(0, localData.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task StocksExcelCanBeImportedAfterCompletingScheduleFields()
    {
        using var factory = new ApiFactory();
        using var client = factory.AuthorizedClient();
        var exported = await client.GetByteArrayAsync("/api/stocks/clientes/exportar");
        using var workbook = new XLWorkbook(new MemoryStream(exported));

        // O modelo exige que o utilizador preencha as informações ausentes na StocksAPI.
        using var incomplete = new MultipartFormDataContent();
        incomplete.Add(new ByteArrayContent(exported), "ficheiro", "clientes_stocks.xlsx");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(Root + "/importar", incomplete)).StatusCode);
        var sheet = workbook.Worksheet(1);
        for (var row = 2; row <= 3; row++)
        {
            sheet.Cell(row, 3).Value = new DateTime(2026, 10, 20);
            sheet.Cell(row, 4).Value = "Manhã";
            sheet.Cell(row, 5).Value = 7;
        }
        var imported = await Upload(client, workbook);
        imported.EnsureSuccessStatusCode();
        Assert.Equal(2, (await imported.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("criados").GetInt32());
        Assert.Equal("Hotel Azul", (await client.GetFromJsonAsync<Entrega>(Root + "/1"))!.NomeCliente);
        Assert.Equal("Hotel Verde", (await client.GetFromJsonAsync<Entrega>(Root + "/2"))!.NomeCliente);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("empty")]
    [InlineData("upstream")]
    public async Task StocksExcelDoesNotReturnPartialFileWhenStocksFails(string failure)
    {
        using var factory = new ApiFactory();
        factory.Stocks.ExportPages[1] = failure == "empty"
            ? new([], true)
            : new([new(1, 10, "Hotel Azul", "001")], true);
        factory.Stocks.ExportPages[2] = new([new(1, 10, "Hotel Azul", "001")], false);
        factory.Stocks.Fail = failure == "upstream";
        using var client = factory.AuthorizedClient();

        var response = await client.GetAsync("/api/stocks/clientes/exportar");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task CrudResolvesStocksCustomerAndPreventsDuplicate()
    {
        using var factory = new ApiFactory();
        using var client = factory.AuthorizedClient();
        var create = await client.PostAsJsonAsync(Root, Request());
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.EndsWith(Root + "/1", create.Headers.Location!.ToString());
        var saved = await create.Content.ReadFromJsonAsync<Entrega>();
        Assert.Equal("Grupo Hotéis", saved!.Entidade);
        Assert.Equal("Hotel Azul", saved.Unidade);
        Assert.Contains("/api/Cliente/1", factory.Stocks.RequestedPaths);
        Assert.Contains("/api/Entidade/10", factory.Stocks.RequestedPaths);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Root, Request())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(Root + "/1", Request(dias: 14))).StatusCode);
        Assert.Equal(14, (await client.GetFromJsonAsync<Entrega>(Root + "/1"))!.PeriodicidadeDias);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Root + "/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Root + "/1")).StatusCode);
    }

    [Theory]
    [InlineData("Noite", 7)]
    [InlineData("Manhã", 0)]
    public async Task InvalidCrudDataIsRejected(string periodo, int dias)
    {
        using var factory = new ApiFactory();
        using var client = factory.AuthorizedClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Root, Request(periodo: periodo, dias: dias))).StatusCode);
        Assert.Empty(factory.Stocks.RequestedPaths);
    }

    [Fact]
    public async Task FiltersSortsAndValidatesDateRange()
    {
        using var factory = new ApiFactory();
        using var client = factory.AuthorizedClient();
        (await client.PostAsJsonAsync(Root, Request())).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(Root, Request(2, "Tarde", 14))).EnsureSuccessStatusCode();
        var response = await client.GetFromJsonAsync<JsonElement>(Root + "?periodoHorario=Tarde&dataInicio=2026-09-25&dataFim=2026-09-25&ordenarPor=unidade&descendente=true");
        Assert.Equal(1, response.GetProperty("total").GetInt32());
        Assert.Equal(2, response.GetProperty("items")[0].GetProperty("idCliente").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Root + "?dataInicio=2026-10-01&dataFim=2026-09-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Root + "?ordenarPor=invalid")).StatusCode);
    }

    [Fact]
    public async Task ExportImportRoundTripUpdatesWithoutDuplicateAndIncludesAllRows()
    {
        using var factory = new ApiFactory();
        using var client = factory.AuthorizedClient();
        (await client.PostAsJsonAsync(Root, Request())).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(Root, Request(2))).EnsureSuccessStatusCode();
        var exported = await client.GetByteArrayAsync(Root + "/exportar");
        using var workbook = new XLWorkbook(new MemoryStream(exported));
        Assert.Equal(3, workbook.Worksheet(1).LastRowUsed()!.RowNumber());
        workbook.Worksheet(1).Cell(2, 5).Value = 30;
        var imported = await Upload(client, workbook);
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        var result = await imported.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, result.GetProperty("criados").GetInt32());
        Assert.Equal(2, result.GetProperty("atualizados").GetInt32());
        Assert.Equal(30, (await client.GetFromJsonAsync<Entrega>(Root + "/1"))!.PeriodicidadeDias);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("fraction")]
    [InlineData("header")]
    [InlineData("formula")]
    public async Task InvalidImportDoesNotApplyValidRows(string failure)
    {
        using var factory = new ApiFactory();
        using var client = factory.AuthorizedClient();
        (await client.PostAsJsonAsync(Root, Request())).EnsureSuccessStatusCode();
        using var workbook = Workbook();
        var sheet = workbook.Worksheet(1);
        sheet.Cell(2, 5).Value = 30;
        AddRow(sheet, 3, failure == "duplicate" ? 1 : failure == "unknown" ? 999 : 2);
        if (failure == "fraction") sheet.Cell(3, 5).Value = 1.5;
        if (failure == "header") sheet.Cell(1, 1).Value = "ID";
        if (failure == "formula") sheet.Cell(3, 5).FormulaA1 = "1+1";
        var result = await Upload(client, workbook);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.NotEmpty((await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("erros").EnumerateArray());
        Assert.Equal(7, (await client.GetFromJsonAsync<Entrega>(Root + "/1"))!.PeriodicidadeDias);
        var all = await client.GetFromJsonAsync<JsonElement>(Root);
        Assert.Equal(1, all.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task StocksProxyUsesCorrectPaginationAndMapsRemoteFailure()
    {
        using var factory = new ApiFactory();
        using var client = factory.AuthorizedClient();
        var response = await client.GetAsync("/api/stocks/clientes?pagina=2&tamanhoPagina=25");
        response.EnsureSuccessStatusCode();
        Assert.Contains("/api/Cliente?pageNumber=2&pageSize=25", factory.Stocks.RequestedPaths);
        factory.Stocks.Fail = true;
        Assert.Equal(HttpStatusCode.BadGateway, (await client.PostAsJsonAsync(Root, Request())).StatusCode);
    }

    [Fact]
    public async Task RecurrenceKeepsOriginalBaseDate()
    {
        using var factory = new ApiFactory();
        using var client = factory.AuthorizedClient();
        (await client.PostAsJsonAsync(Root, Request())).EnsureSuccessStatusCode();
        var result = await client.GetFromJsonAsync<JsonElement>(Root + "/1/proxima?referencia=2026-10-16");
        Assert.Equal("2026-10-16", result.GetProperty("proximaData").GetString());
        Assert.Equal(new DateTime(2026, 9, 25), (await client.GetFromJsonAsync<Entrega>(Root + "/1"))!.DataPrevista);
    }

    [Fact]
    public void RecurrenceHandlesFutureBaseAndDateOverflow()
    {
        Assert.Equal(new DateOnly(2026, 10, 20), EntregaService.CalcularProximaData(new(2026, 10, 20), 7, new(2026, 10, 16)));
        Assert.Equal(new DateOnly(2026, 10, 23), EntregaService.CalcularProximaData(new(2026, 9, 25), 7, new(2026, 10, 17)));
        Assert.Null(EntregaService.CalcularProximaData(new(9999, 12, 30), int.MaxValue, new(9999, 12, 31)));
    }

    [Fact]
    public void ExcelReaderSupportsNativeAndUnambiguousTextDates()
    {
        using var workbook = Workbook();
        workbook.Worksheet(1).Cell(2, 3).Value = "25/09/2026";
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        Assert.Equal(new DateOnly(2026, 9, 25), Assert.Single(new EntregaExcelService().Ler(stream)).DataPrevista);
    }

    private static XLWorkbook Workbook()
    {
        var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Próximas Descargas");
        for (var index = 0; index < EntregaExcelService.Colunas.Length; index++)
            sheet.Cell(1, index + 1).Value = EntregaExcelService.Colunas[index];
        AddRow(sheet, 2, 1);
        return workbook;
    }

    private static void AddRow(IXLWorksheet sheet, int row, int id)
    {
        sheet.Cell(row, 1).Value = id;
        sheet.Cell(row, 2).Value = "Hotel " + id;
        sheet.Cell(row, 3).Value = new DateTime(2026, 9, 25);
        sheet.Cell(row, 4).Value = "Manhã";
        sheet.Cell(row, 5).Value = 7;
    }

    private static async Task<HttpResponseMessage> Upload(HttpClient client, XLWorkbook workbook)
    {
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(stream.ToArray()), "ficheiro", "proximas_descargas.xlsx");
        return await client.PostAsync(Root + "/importar", content);
    }
}
