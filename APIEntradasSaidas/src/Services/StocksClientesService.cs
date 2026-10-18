using APIEntradasSaidas.Dtos;
using APIEntradasSaidas.Integrations;

namespace APIEntradasSaidas.Services;

/// <summary>
/// Obtém os clientes da StocksAPI e prepara um Excel para configurar as próximas descargas.
/// Esta operação apenas consulta dados externos e não grava configurações na BD local.
/// </summary>
public sealed class StocksClientesService(IStocksApiClient stocks, EntregaExcelService excel)
{
    /// <summary>
    /// Percorre todas as páginas para exportar os clientes no mesmo modelo aceite pela importação.
    /// </summary>
    public async Task<byte[]> ExportarAsync(CancellationToken ct)
    {
        // O tamanho fixo é independente da página atualmente selecionada no BO ou Swagger.
        const int tamanhoPagina = 200;
        var clientes = new List<StocksCliente>();
        var ids = new HashSet<int>();

        for (var pagina = 1; ; pagina++)
        {
            // Cada chamada usa o HttpClient configurado com o endereço e a chave da StocksAPI.
            var resultado = await stocks.GetClientesAsync(pagina, tamanhoPagina, ct);

            // Uma página vazia com indicação de continuação é uma resposta inconsistente.
            // Interrompe para não ficar a pedir páginas indefinidamente.
            if (resultado.Items.Count == 0 && resultado.HasNextPage)
                throw new HttpRequestException("A StocksAPI devolveu uma página vazia com continuação.");

            foreach (var cliente in resultado.Items)
            {
                // Exige IDs positivos, únicos e nomes que respeitem o modelo de importação.
                // Também deteta páginas repetidas pela API externa.
                if (cliente.Id <= 0 || !ids.Add(cliente.Id) ||
                    string.IsNullOrWhiteSpace(cliente.Nome) || cliente.Nome.Length > 255)
                    throw new HttpRequestException("A StocksAPI devolveu clientes com IDs ou nomes inválidos ou repetidos.");

                clientes.Add(cliente);
                // Garante que o modelo produzido respeita o limite de linhas do importador.
                // Rejeita o pedido em vez de devolver um ficheiro com clientes em falta.
                if (clientes.Count > EntregaExcelService.MaxLinhas)
                    throw new ValidacaoException([new(0,
                        $"A exportação de clientes admite até {EntregaExcelService.MaxLinhas} linhas.")]);
            }

            // Só termina quando a StocksAPI indicar que não existem mais páginas.
            if (!resultado.HasNextPage)
                break;
        }

        // Os clientes são ordenados por ID para facilitar a comparação entre exportações.
        // O serviço Excel preenche os IDs e nomes, deixando os campos de agendamento vazios.
        return excel.ExportarClientes(clientes.OrderBy(cliente => cliente.Id));
    }
}
