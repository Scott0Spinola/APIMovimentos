using System.ComponentModel.DataAnnotations;

namespace APIEntradasSaidas.Models;

public class Entrega
{
    public int Id { get; set; }
    [Range(1, int.MaxValue)]
    public int IdCliente { get; set; }
    [Required, MaxLength(255)]
    public string NomeCliente { get; set; } = string.Empty;
    [MaxLength(255)]
    public string Entidade{ get; set; } = string.Empty;
    [Required, MaxLength(255)]
    public string Unidade { get; set; } = string.Empty;
    public DateTime DataPrevista { get; set; }
    [Required, RegularExpression("^(Manhã|Tarde)$")]
    public string PeriodoHorario { get; set; } = string.Empty;
    [Range(1, int.MaxValue)]
    public int PeriodicidadeDias { get; set; }
    [MaxLength(2000)]
    public string? Observacoes { get; set; }

}
