using System.Globalization;
using System.IO.Compression;
using APIEntradasSaidas.Dtos;
using APIEntradasSaidas.Integrations;
using APIEntradasSaidas.Models;
using ClosedXML.Excel;

namespace APIEntradasSaidas.Services;

/// <summary>
/// Gera o modelo Excel e valida o conteúdo importado antes de o enviar ao serviço de entregas.
/// Usa ClosedXML para trabalhar com os ficheiros em memória, sem necessitar do Excel instalado.
/// </summary>
public sealed class EntregaExcelService
{
    // Limites usados para controlar o volume do ficheiro e o número de linhas importadas.
    public const int MaxLinhas = 10000;
    public const long MaxBytes = 5 * 1024 * 1024;

    // Este contrato é partilhado pela exportação e importação: nomes e ordem devem coincidir.
    public static readonly string[] Colunas =
        ["ID Cliente", "Nome do Cliente", "Próxima Descarga", "Período", "Periodicidade (dias)", "Observações"];

    /// <summary>
    /// Produz o conteúdo de um .xlsx com os registos recebidos e as seis colunas do modelo.
    /// </summary>
    public byte[] Exportar(IEnumerable<Entrega> entregas)
    {
        // As configurações guardadas já têm todos os campos obrigatórios preenchidos.
        return ExportarLinhas(entregas.Select(entrega => new LinhaExcel(entrega.IdCliente,
            entrega.NomeCliente, entrega.DataPrevista, entrega.PeriodoHorario,
            entrega.PeriodicidadeDias, entrega.Observacoes)));
    }

    /// <summary>
    /// Preenche o modelo com os IDs e nomes obtidos da StocksAPI.
    /// Os campos de agendamento ficam vazios para o utilizador definir antes da importação.
    /// </summary>
    public byte[] ExportarClientes(IEnumerable<StocksCliente> clientes)
    {
        // O contrato de clientes da StocksAPI não fornece data, período nem periodicidade.
        // Não atribui valores fictícios nem cria configurações na BD ao gerar o modelo.
        return ExportarLinhas(clientes.Select(cliente =>
            new LinhaExcel(cliente.Id, cliente.Nome, null, null, null, null)));
    }

    // Representa uma linha do Excel, incluindo modelos que ainda não têm agendamento.
    private sealed record LinhaExcel(int IdCliente, string NomeCliente, DateTime? DataPrevista,
        string? PeriodoHorario, int? PeriodicidadeDias, string? Observacoes);

    /// <summary>
    /// Partilha os cabeçalhos, a formatação e as validações entre os dois tipos de exportação.
    /// </summary>
    private static byte[] ExportarLinhas(IEnumerable<LinhaExcel> linhas)
    {
        // Cria o documento e uma única folha, preenchendo a primeira linha com os cabeçalhos.
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Próximas Descargas");
        for (var column = 0; column < Colunas.Length; column++)
            sheet.Cell(1, column + 1).Value = Colunas[column];

        // Cada cliente ocupa uma linha, a partir da segunda; a data mantém o tipo Excel.
        // Campos ainda não configurados permanecem vazios, incluindo a data e a periodicidade.
        var row = 2;
        foreach (var entrega in linhas)
        {
            sheet.Cell(row, 1).Value = entrega.IdCliente;
            sheet.Cell(row, 2).Value = entrega.NomeCliente;
            if (entrega.DataPrevista is { } data)
                sheet.Cell(row, 3).Value = data;
            sheet.Cell(row, 4).Value = entrega.PeriodoHorario ?? string.Empty;
            if (entrega.PeriodicidadeDias is { } dias)
                sheet.Cell(row, 5).Value = dias;
            sheet.Cell(row, 6).Value = entrega.Observacoes ?? string.Empty;
            row++;
        }

        // Aplica o formato visual da data e destaca os cabeçalhos para facilitar a leitura.
        sheet.Column(3).Style.DateFormat.Format = "dd/mm/yyyy";
        sheet.Range(1, 1, 1, 6).Style.Font.Bold = true;
        sheet.Range(1, 1, 1, 6).Style.Fill.BackgroundColor = XLColor.FromHtml("#4F81BD");
        sheet.Range(1, 1, 1, 6).Style.Font.FontColor = XLColor.White;
        // Mantém os cabeçalhos visíveis ao deslocar a folha e permite filtrar os registos.
        sheet.SheetView.FreezeRows(1);
        sheet.Range(1, 1, Math.Max(row - 1, 1), 6).SetAutoFilter();
        // Reserva mais espaço para os nomes dos clientes e para as observações.
        sheet.Columns(1, 6).Width = 24;
        sheet.Column(2).Width = 40;
        sheet.Column(6).Width = 60;
        // Prepara também linhas para novas entradas com uma lista de períodos e um limite
        // de inteiros positivos. Estas ajudas no Excel não substituem a validação na importação.
        var lastInputRow = Math.Min(Math.Max(row + 100, 1000), MaxLinhas + 1);
        sheet.Range(2, 4, lastInputRow, 4).CreateDataValidation().List("\"Manhã,Tarde\"", true);
        sheet.Range(2, 5, lastInputRow, 5).CreateDataValidation().WholeNumber.Between(1, int.MaxValue);
        // Serializa o documento para bytes, que o controller envia como ficheiro para download.
        // As declarações using libertam os recursos ao sair do método.
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Lê o ficheiro e apresenta erros compreensíveis quando o seu conteúdo não é válido.
    /// O stream recebido deve permitir reposicionar a leitura; o controller fornece um MemoryStream.
    /// </summary>
    public List<ImportacaoLinha> Ler(Stream stream)
    {
        // Delega a leitura e a validação detalhada para o método interno.
        try
        {
            return LerWorkbook(stream);
        }
        // Preserva os erros de validação com as respetivas linhas do Excel.
        catch (ValidacaoException) { throw; }

        // Converte outras falhas de leitura numa mensagem de ficheiro inválido.
        // Cancelamentos e falta de memória continuam a propagar-se sem esta conversão.
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            throw Erro("Não foi possível ler o ficheiro. Envia um ficheiro .xlsx válido, sem proteção por palavra-passe.");
        }
    }

    /// <summary>
    /// Verifica a estrutura do documento e todas as linhas antes de devolver dados para gravação.
    /// </summary>
    private static List<ImportacaoLinha> LerWorkbook(Stream stream)
    {
        // Um .xlsx é um ZIP: limitar também o conteúdo descomprimido antes de abrir.
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
        {
            if (archive.Entries.Count > 1000 || archive.Entries.Sum(entry => entry.Length) > 30 * 1024 * 1024)
                throw Erro("O conteúdo descomprimido do ficheiro é demasiado grande.");
        }
        // Reinicia a leitura depois de inspecionar o ZIP e exige uma única folha de dados.
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        if (workbook.Worksheets.Count != 1)
            throw Erro("O ficheiro deve conter uma única folha de dados.");

        // Considera células com conteúdo, ignorando linhas ou colunas apenas formatadas.
        var sheet = workbook.Worksheet(1);
        var lastRow = sheet.LastRowUsed(XLCellsUsedOptions.Contents)?.RowNumber() ?? 0;
        var lastColumn = sheet.LastColumnUsed(XLCellsUsedOptions.Contents)?.ColumnNumber() ?? 0;
        // Rejeita colunas adicionais, nomes alterados ou uma ordem diferente da exportação.
        if (lastColumn != 6 || Colunas.Where((header, index) => sheet.Cell(1, index + 1).GetString() != header).Any())
            throw Erro("As colunas devem ser, por esta ordem: " + string.Join(" | ", Colunas));
        // Soma uma linha ao limite porque a primeira está reservada para os cabeçalhos.
        if (lastRow > MaxLinhas + 1)
            throw Erro($"O ficheiro admite no máximo {MaxLinhas} linhas de dados.");

        // Acumula os erros e as linhas válidas separadamente; o conjunto de IDs deteta duplicados.
        var errors = new List<ImportacaoErro>();
        var result = new List<ImportacaoLinha>();
        var ids = new HashSet<int>();
        for (var row = 2; row <= lastRow; row++)
        {
            // Lê as seis células da linha; uma linha totalmente vazia não representa um registo.
            var cells = Enumerable.Range(1, 6).Select(col => sheet.Cell(row, col)).ToArray();
            if (cells.All(cell => cell.IsEmpty(XLCellsUsedOptions.Contents)))
                continue;
            // Exige valores introduzidos no ficheiro, evitando depender do cálculo de fórmulas.
            if (cells.Any(cell => cell.HasFormula))
            {
                errors.Add(new(row, "Não são permitidas fórmulas nas células de dados."));
                continue;
            }

            // Guarda a contagem atual para saber no fim se esta linha teve algum erro.
            var errorCount = errors.Count;

            // Valida o ID como inteiro positivo e garante que só aparece uma vez no ficheiro.
            // A existência do cliente na StocksAPI é verificada depois, pelo EntregaService.
            if (!TryPositiveInteger(cells[0], out var id))
                errors.Add(new(row, "ID Cliente deve ser um inteiro superior a 0 (Cliente.Id da StocksAPI)."));
            else if (!ids.Add(id))
                errors.Add(new(row, $"O ID Cliente {id} aparece mais de uma vez."));

            // O nome é obrigatório e deve caber no tamanho definido para a coluna da BD.
            var name = cells[1].GetString().Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 255)
                errors.Add(new(row, "Nome do Cliente é obrigatório e admite até 255 caracteres."));

            // Aceita datas nativas do Excel ou texto nos formatos definidos em TryDate.
            if (!TryDate(cells[2], out var date))
                errors.Add(new(row, "Próxima Descarga deve ser uma data Excel ou texto dd/MM/yyyy ou yyyy-MM-dd, a partir de 1900."));

            // Só os dois períodos previstos pelo cliente são permitidos.
            var period = cells[3].GetString().Trim();
            if (period is not ("Manhã" or "Tarde"))
                errors.Add(new(row, "Período deve ser Manhã ou Tarde."));

            // A periodicidade exige dias inteiros positivos; valores decimais são inválidos.
            if (!TryPositiveInteger(cells[4], out var interval))
                errors.Add(new(row, "Periodicidade (dias) deve ser um inteiro superior a 0."));

            // As observações são opcionais, mas continuam sujeitas ao limite da BD.
            var notes = cells[5].GetString().Trim();
            if (notes.Length > 2000)
                errors.Add(new(row, "Observações admite até 2000 caracteres."));

            // Só transforma a linha num DTO se todas as suas células passaram na validação.
            if (errors.Count == errorCount)
                result.Add(new(row, id, name, date, period, interval, string.IsNullOrEmpty(notes) ? null : notes));
        }
        // Qualquer erro invalida a importação completa, mesmo que existam outras linhas válidas.
        if (errors.Count > 0)
            throw new ValidacaoException(errors);
        // Exportar e reimportar uma tabela vazia é uma operação válida sem alterações.
        return result;
    }

    /// <summary>
    /// Aceita um inteiro positivo guardado como número ou texto, sem arredondar decimais.
    /// </summary>
    private static bool TryPositiveInteger(IXLCell cell, out int number)
    {
        number = 0;

        // Para células numéricas, verifica o intervalo de int e a ausência de parte decimal.
        if (cell.DataType == XLDataType.Number)
        {
            var value = cell.GetDouble();
            if (value < 1 || value > int.MaxValue || value != Math.Truncate(value))
                return false;
            number = (int)value;
            return true;
        }
        // Para texto, aceita apenas algarismos depois de remover espaços nas extremidades.
        // A cultura invariável evita interpretações diferentes de separadores entre máquinas.
        return cell.DataType == XLDataType.Text &&
            int.TryParse(cell.GetString().Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;
    }

    /// <summary>
    /// Converte uma célula para uma data sem hora, rejeitando valores anteriores a 1900.
    /// </summary>
    private static bool TryDate(IXLCell cell, out DateOnly date)
    {
        date = default;

        // Datas Excel também podem estar guardadas como números de série.
        if (cell.DataType is XLDataType.DateTime or XLDataType.Number && cell.TryGetValue<DateTime>(out var value))
        {
            // Uma fração horária não pertence à data base: o período é indicado noutra coluna.
            if (value.TimeOfDay != TimeSpan.Zero)
                return false;
            date = DateOnly.FromDateTime(value);
        }
        // Para texto, exige um dos formatos explícitos para evitar ambiguidade entre dia e mês.
        else if (!DateOnly.TryParseExact(cell.GetString().Trim(), ["dd/MM/yyyy", "yyyy-MM-dd"],
                     CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return false;
        // Mantém a mesma data mínima exigida pelo DTO dos pedidos JSON.
        return date >= new DateOnly(1900, 1, 1);
    }

    // A linha 0 identifica um problema do ficheiro ou dos cabeçalhos, sem uma linha de dados específica.
    private static ValidacaoException Erro(string message) => new([new(0, message)]);
}
