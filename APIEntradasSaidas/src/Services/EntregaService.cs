using APIEntradasSaidas.Data;
using APIEntradasSaidas.Dtos;
using APIEntradasSaidas.Integrations;
using APIEntradasSaidas.Models;
using Microsoft.EntityFrameworkCore;

namespace APIEntradasSaidas.Services;

/// <summary>
/// Gere as configurações de descarga na BD e consulta a StocksAPI para validar os clientes.
/// O construtor recebe o contexto da BD e o cliente HTTP por injeção de dependências.
/// </summary>
public sealed class EntregaService(APIcontext context, IStocksApiClient stocks)
{
    /// <summary>
    /// Prepara uma consulta com filtros e ordenação. A consulta só é executada quando
    /// o controller pede a contagem ou materializa os resultados com ToListAsync.
    /// </summary>
    public IQueryable<Entrega> Consultar(EntregaFiltro filtro)
    {
        // Como esta consulta serve para leitura, não é necessário o EF acompanhar alterações.
        var query = context.Entrega.AsNoTracking();

        // Os filtros de texto procuram correspondências parciais nos nomes guardados.
        if (!string.IsNullOrWhiteSpace(filtro.Entidade))
            query = query.Where(x => x.Entidade.Contains(filtro.Entidade));
        if (!string.IsNullOrWhiteSpace(filtro.Unidade))
            query = query.Where(x => x.Unidade.Contains(filtro.Unidade));

        // Converte o início para DateTime à meia-noite, incluindo essa data na pesquisa.
        if (filtro.DataInicio is { } inicio)
        {
            var start = inicio.ToDateTime(TimeOnly.MinValue);
            query = query.Where(x => x.DataPrevista >= start);
        }
        // O limite final também é inclusivo: DataPrevista é guardada como uma data sem hora.
        if (filtro.DataFim is { } fim)
        {
            var end = fim.ToDateTime(TimeOnly.MinValue);
            query = query.Where(x => x.DataPrevista <= end);
        }
        // O período é comparado com o valor completo: Manhã ou Tarde.
        if (filtro.PeriodoHorario is not null)
            query = query.Where(x => x.PeriodoHorario == filtro.PeriodoHorario);

        // Escolhe uma expressão de ordenação conhecida, traduzida pelo EF para SQL.
        // O DTO valida os nomes permitidos; a ordenação predefinida usa a data prevista.
        var sorted = (filtro.OrdenarPor, filtro.Descendente) switch
        {
            ("entidade", false) => query.OrderBy(x => x.Entidade),
            ("entidade", true) => query.OrderByDescending(x => x.Entidade),
            ("unidade", false) => query.OrderBy(x => x.Unidade),
            ("unidade", true) => query.OrderByDescending(x => x.Unidade),
            ("periodoHorario", false) => query.OrderBy(x => x.PeriodoHorario),
            ("periodoHorario", true) => query.OrderByDescending(x => x.PeriodoHorario),
            ("idCliente", false) => query.OrderBy(x => x.IdCliente),
            ("idCliente", true) => query.OrderByDescending(x => x.IdCliente),
            ("nomeCliente", false) => query.OrderBy(x => x.NomeCliente),
            ("nomeCliente", true) => query.OrderByDescending(x => x.NomeCliente),
            (_, true) => query.OrderByDescending(x => x.DataPrevista),
            _ => query.OrderBy(x => x.DataPrevista)
        };
        // Desempata pelo ID para manter a ordem estável entre páginas com valores iguais.
        return sorted.ThenBy(x => x.IdCliente);
    }

    /// <summary>
    /// Obtém a configuração de um cliente sem acompanhar alterações; devolve null se não existir.
    /// </summary>
    public Task<Entrega?> GetAsync(int idCliente, CancellationToken ct) =>
        context.Entrega.AsNoTracking().SingleOrDefaultAsync(x => x.IdCliente == idCliente, ct);

    /// <summary>
    /// Cria uma configuração depois de verificar duplicados e validar o cliente na StocksAPI.
    /// </summary>
    public async Task<Entrega> CriarAsync(EntregaRequest request, CancellationToken ct)
    {
        // Cada cliente só pode ter uma configuração. O índice único na BD também
        // protege contra pedidos simultâneos que ultrapassem esta verificação inicial.
        if (await context.Entrega.AnyAsync(x => x.IdCliente == request.IdCliente, ct))
            throw new ConflitoException("Já existe uma próxima descarga para este cliente. Usa PUT para a atualizar.");

        // Preenche a nova entidade com os dados externos e a configuração recebida.
        var entrega = new Entrega { IdCliente = request.IdCliente };
        await AplicarAsync(entrega, request, ct);

        // Add prepara a inserção; só SaveChangesAsync grava efetivamente na BD.
        context.Entrega.Add(entrega);
        await context.SaveChangesAsync(ct);
        return entrega;
    }

    /// <summary>
    /// Atualiza uma configuração existente, mantendo a associação ao mesmo ID Cliente.
    /// </summary>
    public async Task<Entrega?> AtualizarAsync(int idCliente, EntregaRequest request, CancellationToken ct)
    {
        // Evita atualizar um cliente diferente daquele indicado no endereço do pedido.
        if (idCliente != request.IdCliente)
            throw new ValidacaoException([new(0, "ID Cliente do corpo deve coincidir com o da rota.")]);

        // A entidade é acompanhada pelo EF porque os seus campos vão ser alterados.
        var entrega = await context.Entrega.SingleOrDefaultAsync(x => x.IdCliente == idCliente, ct);
        if (entrega is null)
            return null;

        // Volta a validar os dados externos antes de aplicar e gravar a atualização.
        await AplicarAsync(entrega, request, ct);
        await context.SaveChangesAsync(ct);
        return entrega;
    }

    /// <summary>
    /// Elimina apenas a configuração local da descarga; o cliente permanece na StocksAPI.
    /// </summary>
    public async Task<bool> ApagarAsync(int idCliente, CancellationToken ct)
    {
        // O valor false permite ao controller responder 404 quando não existe configuração.
        var entrega = await context.Entrega.SingleOrDefaultAsync(x => x.IdCliente == idCliente, ct);
        if (entrega is null)
            return false;

        // Marca a entidade para eliminação e confirma a operação na BD.
        context.Entrega.Remove(entrega);
        await context.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Partilha entre criação e atualização a validação externa e o preenchimento da entidade.
    /// Este método altera o objeto em memória; quem o chamou é responsável por gravar.
    /// </summary>
    private async Task AplicarAsync(Entrega entrega, EntregaRequest request, CancellationToken ct)
    {
        // Confirma que o cliente existe e obtém o identificador da entidade a que pertence.
        var cliente = await stocks.GetClienteAsync(request.IdCliente, ct)
            ?? throw new ValidacaoException([new(0, $"Cliente {request.IdCliente} não existe na StocksAPI.")]);

        // Obtém a entidade para guardar o respetivo nome na configuração local.
        var entidade = await stocks.GetEntidadeAsync(cliente.IdEntidade, ct)
            ?? throw new ValidacaoException([new(0, $"Entidade {cliente.IdEntidade} do cliente não existe na StocksAPI.")]);

        // Dá prioridade ao nome social; na sua ausência, usa a designação comercial.
        // Se não for indicada uma unidade, adota o nome do cliente como valor inicial.
        entrega.NomeCliente = cliente.Nome;
        entrega.Entidade = entidade.NomeSocial ?? entidade.DesignacaoComercial ?? string.Empty;
        entrega.Unidade = string.IsNullOrWhiteSpace(request.Unidade) ? cliente.Nome : request.Unidade.Trim();
        // Guarda a data base sem hora e os parâmetros usados para calcular as recorrências.
        // Trim remove espaços nas extremidades das observações, quando existem.
        entrega.DataPrevista = request.DataPrevista.ToDateTime(TimeOnly.MinValue);
        entrega.PeriodoHorario = request.PeriodoHorario;
        entrega.PeriodicidadeDias = request.PeriodicidadeDias;
        entrega.Observacoes = request.Observacoes?.Trim();
    }

    /// <summary>
    /// Recebe linhas já validadas pelo serviço Excel, confirma os clientes na StocksAPI
    /// e cria ou atualiza as configurações numa única gravação.
    /// </summary>
    public async Task<ImportacaoResultado> ImportarAsync(List<ImportacaoLinha> linhas, CancellationToken ct)
    {
        // Um ficheiro só com cabeçalhos não exige chamadas externas nem alterações na BD.
        if (linhas.Count == 0)
            return new(0, 0);

        // Resolver todos os clientes antes de alterar qualquer entidade da base de dados.
        var clientes = new Dictionary<int, StocksCliente>();
        var entidades = new Dictionary<int, StocksEntidade>();
        var errors = new List<ImportacaoErro>();
        foreach (var linha in linhas)
        {
            // Acumula erros com o número da linha para o utilizador poder corrigir o Excel.
            // Um cliente inexistente impede validar a entidade dessa linha.
            var cliente = await stocks.GetClienteAsync(linha.IdCliente, ct);
            if (cliente is null)
            {
                errors.Add(new(linha.Linha, $"Cliente {linha.IdCliente} não existe na StocksAPI."));
                continue;
            }
            // Guarda os clientes por ID e reutiliza entidades já obtidas para outros clientes.
            clientes[cliente.Id] = cliente;
            if (!entidades.ContainsKey(cliente.IdEntidade))
            {
                var entidade = await stocks.GetEntidadeAsync(cliente.IdEntidade, ct);
                if (entidade is null)
                    errors.Add(new(linha.Linha, $"Entidade {cliente.IdEntidade} não existe na StocksAPI."));
                else
                    entidades[entidade.Id] = entidade;
            }
        }
        // Se alguma referência externa for inválida, interrompe antes de alterar os registos locais.
        if (errors.Count > 0)
            throw new ValidacaoException(errors);

        // Carrega numa consulta as configurações dos IDs presentes no ficheiro.
        // Os registos que não aparecem no Excel ficam fora desta operação.
        var ids = linhas.Select(x => x.IdCliente).ToArray();
        var existentes = await context.Entrega.Where(x => ids.Contains(x.IdCliente)).ToDictionaryAsync(x => x.IdCliente, ct);
        var criados = 0;
        var atualizados = 0;
        foreach (var linha in linhas)
        {
            // Os dados externos já foram validados na primeira passagem pelas linhas.
            var cliente = clientes[linha.IdCliente];
            var entidade = entidades[cliente.IdEntidade];

            // Um ID sem configuração gera uma inserção; um ID existente gera uma atualização.
            // A unidade de um registo existente é preservada porque não existe no Excel.
            if (!existentes.TryGetValue(linha.IdCliente, out var entrega))
            {
                entrega = new Entrega { IdCliente = linha.IdCliente, Unidade = cliente.Nome };
                context.Entrega.Add(entrega);
                criados++;
            }
            else
                atualizados++;
            // Copia os campos editáveis do Excel e atualiza o nome da entidade pela StocksAPI.
            // O nome do cliente apresentado no ficheiro pode ser diferente do nome externo.
            entrega.NomeCliente = linha.NomeCliente;
            entrega.Entidade = entidade.NomeSocial ?? entidade.DesignacaoComercial ?? string.Empty;
            entrega.DataPrevista = linha.DataPrevista.ToDateTime(TimeOnly.MinValue);
            entrega.PeriodoHorario = linha.PeriodoHorario;
            entrega.PeriodicidadeDias = linha.PeriodicidadeDias;
            entrega.Observacoes = linha.Observacoes;
        }
        // Um único SaveChanges usa uma transação: ou se aplicam todas as linhas ou nenhuma.
        await context.SaveChangesAsync(ct);
        return new(criados, atualizados);
    }

    /// <summary>
    /// Calcula a primeira ocorrência igual ou posterior à referência, a partir da data base.
    /// Devolve null se a próxima ocorrência ultrapassar o limite de DateOnly (ano 9999).
    /// </summary>
    public static DateOnly? CalcularProximaData(DateOnly baseDate, int periodicidadeDias, DateOnly referencia)
    {
        // Um intervalo nulo ou negativo não define uma recorrência válida.
        if (periodicidadeDias <= 0)
            throw new ArgumentOutOfRangeException(nameof(periodicidadeDias));

        // Se a data base ainda não passou, ela própria é a próxima ocorrência.
        if (baseDate >= referencia)
            return baseDate;

        // Arredonda para cima o número de ciclos necessários para alcançar a referência.
        // Exemplo: passaram 8 dias e o intervalo é 7; são necessários 2 ciclos (14 dias).
        // O cálculo usa long para evitar overflow nos valores intermédios.
        var dias = referencia.DayNumber - baseDate.DayNumber;
        var ciclos = ((long)dias + periodicidadeDias - 1) / periodicidadeDias;
        var nextDay = baseDate.DayNumber + ciclos * periodicidadeDias;
        // Converte o dia calculado para uma data apenas se estiver dentro do limite suportado.
        return nextDay <= DateOnly.MaxValue.DayNumber ? DateOnly.FromDayNumber((int)nextDay) : null;
    }
}
