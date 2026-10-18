using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace APIEntradasSaidas.Data.Migrations
{
    /// <inheritdoc />
    public partial class CriarProximasDescargas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Entregas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IdCliente = table.Column<int>(type: "int", nullable: false),
                    NomeCliente = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Entidade = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Unidade = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    DataPrevista = table.Column<DateTime>(type: "date", nullable: false),
                    PeriodoHorario = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: false),
                    PeriodicidadeDias = table.Column<int>(type: "int", nullable: false),
                    Observacoes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Entregas", x => x.Id);
                    table.CheckConstraint("CK_Entregas_PeriodicidadeDias", "[PeriodicidadeDias] > 0");
                    table.CheckConstraint("CK_Entregas_PeriodoHorario", "[PeriodoHorario] IN (N'Manhã', N'Tarde')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Entregas_IdCliente",
                table: "Entregas",
                column: "IdCliente",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Entregas");
        }
    }
}
