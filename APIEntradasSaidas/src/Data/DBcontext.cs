using APIEntradasSaidas.Models;
using Microsoft.EntityFrameworkCore;

namespace APIEntradasSaidas.Data;

public class APIcontext(DbContextOptions<APIcontext> options) : DbContext(options)
{
    public DbSet<Entrega> Entrega => Set<Entrega>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Entrega>(entity =>
        {
            entity.ToTable("Entregas", table =>
            {
                table.HasCheckConstraint("CK_Entregas_PeriodoHorario", "[PeriodoHorario] IN (N'Manhã', N'Tarde')");
                table.HasCheckConstraint("CK_Entregas_PeriodicidadeDias", "[PeriodicidadeDias] > 0");
            });
            entity.HasKey(entrega => entrega.Id);
            entity.HasIndex(entrega => entrega.IdCliente).IsUnique();
            entity.Property(entrega => entrega.NomeCliente).HasMaxLength(255).IsRequired();
            entity.Property(entrega => entrega.Entidade).HasMaxLength(255).IsRequired();
            entity.Property(entrega => entrega.Unidade).HasMaxLength(255).IsRequired();
            entity.Property(entrega => entrega.DataPrevista).HasColumnType("date");
            entity.Property(entrega => entrega.PeriodoHorario).HasMaxLength(5).IsRequired();
            entity.Property(entrega => entrega.Observacoes).HasMaxLength(2000);
        });
    }
}
