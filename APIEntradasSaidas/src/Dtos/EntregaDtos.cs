using System.ComponentModel.DataAnnotations;

namespace APIEntradasSaidas.Dtos;

public sealed class EntregaRequest : IValidatableObject
{
    [Range(1, int.MaxValue)]
    public int IdCliente { get; set; }
    public DateOnly DataPrevista { get; set; }
    [Required, RegularExpression("^(Manhã|Tarde)$")]
    public string PeriodoHorario { get; set; } = string.Empty;
    [Range(1, int.MaxValue)]
    public int PeriodicidadeDias { get; set; }
    [MaxLength(255)]
    public string? Unidade { get; set; }
    [MaxLength(2000)]
    public string? Observacoes { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (DataPrevista < new DateOnly(1900, 1, 1))
            yield return new ValidationResult("Indica uma data igual ou posterior a 1900-01-01.", [nameof(DataPrevista)]);
    }
}

public sealed class EntregaFiltro : IValidatableObject
{
    public string? Entidade { get; set; }
    public string? Unidade { get; set; }
    public DateOnly? DataInicio { get; set; }
    public DateOnly? DataFim { get; set; }
    [RegularExpression("^(Manhã|Tarde)$")]
    public string? PeriodoHorario { get; set; }
    [RegularExpression("^(entidade|unidade|dataPrevista|periodoHorario|idCliente|nomeCliente)$")]
    public string OrdenarPor { get; set; } = "dataPrevista";
    public bool Descendente { get; set; }
    [Range(1, 1000000)]
    public int Pagina { get; set; } = 1;
    [Range(1, 500)]
    public int TamanhoPagina { get; set; } = 50;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (DataInicio > DataFim)
            yield return new ValidationResult("DataInicio não pode ser posterior a DataFim.", [nameof(DataInicio)]);
    }
}

public sealed record ImportacaoLinha(int Linha, int IdCliente, string NomeCliente, DateOnly DataPrevista,
    string PeriodoHorario, int PeriodicidadeDias, string? Observacoes);
public sealed record ImportacaoErro(int Linha, string Mensagem);
public sealed record ImportacaoResultado(int Criados, int Atualizados);
public sealed class ValidacaoException(List<ImportacaoErro> erros) : Exception("Os dados contêm erros de validação.")
{
    public List<ImportacaoErro> Erros { get; } = erros;
}

public sealed class ConflitoException(string message) : Exception(message);
