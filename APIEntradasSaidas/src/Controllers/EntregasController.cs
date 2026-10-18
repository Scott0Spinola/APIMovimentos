using APIEntradasSaidas.Dtos;
using APIEntradasSaidas.Integrations;
using APIEntradasSaidas.Models;
using APIEntradasSaidas.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace APIEntradasSaidas.Controllers;

/// <summary>
/// Disponibiliza os endpoints das próximas descargas e delega as regras nos serviços.
/// ApiController ativa a validação automática dos DTOs; Route define o prefixo das rotas.
/// As dependências do construtor são fornecidas pela injeção de dependências do ASP.NET Core.
/// </summary>
[ApiController]
[Route("api/proximas-descargas")]
public sealed class EntregasController(EntregaService service, EntregaExcelService excel, TimeProvider clock) : ControllerBase
{
    /// <summary>
    /// GET /api/proximas-descargas: consulta configurações com filtros e paginação na query string.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Consultar([FromQuery] EntregaFiltro filtro, CancellationToken ct)
    {
        // O serviço prepara a consulta filtrada e ordenada, ainda sem carregar os registos.
        var query = service.Consultar(filtro);

        // Conta todos os resultados filtrados antes de limitar a consulta à página pedida.
        var total = await query.CountAsync(ct);

        // Skip ignora as páginas anteriores e Take limita quantos registos são lidos da BD.
        var items = await query.Skip((filtro.Pagina - 1) * filtro.TamanhoPagina).Take(filtro.TamanhoPagina).ToListAsync(ct);

        // Responde 200 com os registos e os dados necessários para construir a paginação no BO.
        return Ok(new { items, total, filtro.Pagina, filtro.TamanhoPagina });
    }

    /// <summary>
    /// GET /api/proximas-descargas/{idCliente}: obtém a configuração de um cliente.
    /// A rota só aceita um identificador inteiro superior a zero.
    /// </summary>
    [HttpGet("{idCliente:int:min(1)}")]
    public async Task<ActionResult<Entrega>> Get(int idCliente, CancellationToken ct)
    {
        // A ausência de configuração devolve 404; uma configuração encontrada devolve 200.
        var entrega = await service.GetAsync(idCliente, ct);
        return entrega is null ? NotFound() : Ok(entrega);
    }

    /// <summary>
    /// GET /api/proximas-descargas/{idCliente}/proxima: calcula a próxima ocorrência.
    /// O parâmetro referencia é opcional e usa o formato yyyy-MM-dd.
    /// </summary>
    [HttpGet("{idCliente:int:min(1)}/proxima")]
    public async Task<IActionResult> Proxima(int idCliente, [FromQuery] DateOnly? referencia, CancellationToken ct)
    {
        // Só é possível calcular uma ocorrência quando o cliente já tem configuração local.
        var entrega = await service.GetAsync(idCliente, ct);
        if (entrega is null)
            return NotFound();
        // Obtém a data atual em Lisboa a partir do relógio injetado, independentemente
        // do fuso horário do servidor. TimeProvider também permite controlar o relógio em testes.
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
        // Usa a referência fornecida ou o dia atual. O cálculo preserva a data base guardada na BD.
        var proximaData = EntregaService.CalcularProximaData(DateOnly.FromDateTime(entrega.DataPrevista),
            entrega.PeriodicidadeDias, referencia ?? today);
        // Devolve a data calculada com o período e os parâmetros que explicam o agendamento.
        return Ok(new { entrega.IdCliente, entrega.NomeCliente, proximaData, entrega.PeriodoHorario,
            entrega.PeriodicidadeDias, entrega.Observacoes, dataBase = DateOnly.FromDateTime(entrega.DataPrevista) });
    }

    /// <summary>
    /// POST /api/proximas-descargas: recebe o JSON de uma nova configuração.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<Entrega>> Criar([FromBody] EntregaRequest request, CancellationToken ct)
    {
        // O serviço valida o cliente externo, impede duplicados e grava a configuração.
        var entrega = await service.CriarAsync(request, ct);

        // Responde 201 Created com o registo e o cabeçalho Location para o consultar pelo GET.
        return CreatedAtAction(nameof(Get), new { idCliente = entrega.IdCliente }, entrega);
    }

    /// <summary>
    /// PUT /api/proximas-descargas/{idCliente}: recebe o JSON para atualizar uma configuração.
    /// </summary>
    [HttpPut("{idCliente:int:min(1)}")]
    public async Task<ActionResult<Entrega>> Atualizar(int idCliente, [FromBody] EntregaRequest request, CancellationToken ct)
    {
        // O serviço verifica se os IDs da rota e do corpo coincidem e aplica a atualização.
        // Devolve 404 se o registo não existir ou 200 com os dados atualizados.
        var entrega = await service.AtualizarAsync(idCliente, request, ct);
        return entrega is null ? NotFound() : Ok(entrega);
    }

    /// <summary>
    /// DELETE /api/proximas-descargas/{idCliente}: elimina a configuração local.
    /// Responde 204 sem corpo quando elimina o registo, ou 404 se este não existir.
    /// </summary>
    [HttpDelete("{idCliente:int:min(1)}")]
    public async Task<IActionResult> Apagar(int idCliente, CancellationToken ct) =>
        await service.ApagarAsync(idCliente, ct) ? NoContent() : NotFound();

    /// <summary>
    /// GET /api/proximas-descargas/exportar: descarrega toda a definição em formato .xlsx.
    /// </summary>
    [HttpGet("exportar")]
    public async Task<IActionResult> Exportar(CancellationToken ct)
    {
        // Um filtro vazio obtém todos os registos; não se aplica Skip/Take da consulta paginada.
        var entregas = await service.Consultar(new()).ToListAsync(ct);

        // Gera os bytes do Excel e define o tipo de conteúdo e o nome sugerido para o download.
        return File(excel.Exportar(entregas), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "proximas_descargas.xlsx");
    }

    /// <summary>
    /// POST /api/proximas-descargas/importar: recebe um upload no campo ficheiro.
    /// O formato multipart/form-data permite enviar o conteúdo binário do Excel.
    /// </summary>
    [HttpPost("importar")]
    [Consumes("multipart/form-data")]
    // Reserva uma margem de 64 KB para os campos e delimitadores do formulário,
    // além do limite de 5 MB aplicado ao próprio ficheiro dentro do método.
    [RequestSizeLimit(EntregaExcelService.MaxBytes + 65536)]
    public async Task<ActionResult<ImportacaoResultado>> Importar(IFormFile ficheiro, CancellationToken ct)
    {
        // Rejeita ficheiros vazios, demasiado grandes ou com uma extensão diferente de .xlsx.
        // O conteúdo real ainda vai ser validado pelo serviço Excel.
        if (ficheiro.Length == 0 || ficheiro.Length > EntregaExcelService.MaxBytes ||
            !string.Equals(Path.GetExtension(ficheiro.FileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new ValidacaoException([new(0, "Envia um ficheiro .xlsx com conteúdo e até 5 MB.")]);

        // Copia o upload para memória e reposiciona a leitura para o início do ficheiro.
        using var stream = new MemoryStream();
        await ficheiro.CopyToAsync(stream, ct);
        stream.Position = 0;
        // Primeiro valida a estrutura e todas as linhas; só depois permite consultar
        // os clientes externos e gravar a importação numa única transação.
        var linhas = excel.Ler(stream);

        // Responde 200 com a quantidade de configurações criadas e atualizadas.
        return Ok(await service.ImportarAsync(linhas, ct));
    }
}

/// <summary>
/// Permite ao BO consultar clientes da StocksAPI através da nova API.
/// O cliente HTTP recebe do servidor a chave necessária para contactar a StocksAPI.
/// </summary>
[ApiController]
[Route("api/stocks/clientes")]
public sealed class StocksClientesController(IStocksApiClient stocks, StocksClientesService service) : ControllerBase
{
    /// <summary>
    /// GET /api/stocks/clientes: encaminha a paginação para a StocksAPI e devolve a página recebida.
    /// FromQuery lê os valores da query string; Range limita os valores aceites automaticamente.
    /// CancellationToken permite propagar o cancelamento do pedido à chamada externa.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Clientes(
        [FromQuery, System.ComponentModel.DataAnnotations.Range(1, 1000000)] int pagina = 1,
        [FromQuery, System.ComponentModel.DataAnnotations.Range(1, 500)] int tamanhoPagina = 50,
        CancellationToken ct = default) => Ok(await stocks.GetClientesAsync(pagina, tamanhoPagina, ct));

    /// <summary>
    /// GET /api/stocks/clientes/exportar: obtém todos os clientes da StocksAPI e gera um .xlsx.
    /// Preenche ID Cliente e Nome do Cliente; os campos de agendamento ficam por definir.
    /// </summary>
    [HttpGet("exportar")]
    [Produces("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    public async Task<IActionResult> Exportar(CancellationToken ct)
    {
        // O serviço percorre todas as páginas externas antes de produzir o documento.
        var ficheiro = await service.ExportarAsync(ct);

        // Define o nome do modelo para o utilizador o descarregar, preencher e reimportar.
        return File(ficheiro, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "clientes_stocks_proximas_descargas.xlsx");
    }
}
