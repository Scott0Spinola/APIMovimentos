using System.Net;
using System.Net.Http.Json;

namespace APIEntradasSaidas.Integrations;

public sealed class StocksApiOptions
{
    public string BaseUrl { get; set; } = "http://localhost:5275/";
    public string ApiKey { get; set; } = string.Empty;
}

public sealed record StocksCliente(int Id, int IdEntidade, string Nome, string Referencia);
public sealed record StocksEntidade(int Id, string? NomeSocial, string? DesignacaoComercial);
public sealed record StocksPagina<T>(List<T> Items, bool HasNextPage);

public interface IStocksApiClient
{
    Task<StocksCliente?> GetClienteAsync(int id, CancellationToken cancellationToken);
    Task<StocksEntidade?> GetEntidadeAsync(int id, CancellationToken cancellationToken);
    Task<StocksPagina<StocksCliente>> GetClientesAsync(int pagina, int tamanho, CancellationToken cancellationToken);
}

// Contratos e rotas correspondem aos controllers da StocksAPI.
public sealed class StocksApiClient(HttpClient httpClient) : IStocksApiClient
{
    public Task<StocksCliente?> GetClienteAsync(int id, CancellationToken cancellationToken) =>
        GetAsync<StocksCliente>($"api/Cliente/{id}", cancellationToken);

    public Task<StocksEntidade?> GetEntidadeAsync(int id, CancellationToken cancellationToken) =>
        GetAsync<StocksEntidade>($"api/Entidade/{id}", cancellationToken);

    public async Task<StocksPagina<StocksCliente>> GetClientesAsync(int pagina, int tamanho, CancellationToken cancellationToken) =>
        await GetAsync<StocksPagina<StocksCliente>>($"api/Cliente?pageNumber={pagina}&pageSize={tamanho}", cancellationToken)
        ?? throw new HttpRequestException("A StocksAPI devolveu uma página vazia.");

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(path, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return default;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }
}
