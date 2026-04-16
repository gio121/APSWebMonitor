using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace ApsMonitor.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Mensaje = table.Column<string>(type: "TEXT", nullable: false),
                    Fecha = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Estado = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Signals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Nombre = table.Column<string>(type: "TEXT", nullable: false),
                    Tag = table.Column<string>(type: "TEXT", nullable: false),
                    DescripcionEs = table.Column<string>(type: "TEXT", nullable: true),
                    DescripcionEn = table.Column<string>(type: "TEXT", nullable: true),
                    Unidad = table.Column<string>(type: "TEXT", nullable: true),
                    Formato = table.Column<string>(type: "TEXT", nullable: false),
                    ValorInicial = table.Column<double>(type: "REAL", nullable: false),
                    TipoVariable = table.Column<string>(type: "TEXT", nullable: false),
                    BytePosicion = table.Column<int>(type: "INTEGER", nullable: false),
                    Escala = table.Column<double>(type: "REAL", nullable: false),
                    Offset = table.Column<double>(type: "REAL", nullable: false),
                    ValorActual = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Signals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Windows",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Nombre = table.Column<string>(type: "TEXT", nullable: false),
                    Descripcion = table.Column<string>(type: "TEXT", nullable: false),
                    Categoria = table.Column<string>(type: "TEXT", nullable: false),
                    Tipo = table.Column<string>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Windows", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "Events",
                columns: new[] { "Id", "Estado", "Fecha", "Mensaje" },
                values: new object[] { 1, "OK", new DateTime(2023, 3, 20, 12, 13, 14, 0, DateTimeKind.Unspecified), "Arrancar Inversor" });

            migrationBuilder.InsertData(
                table: "Signals",
                columns: new[] { "Id", "BytePosicion", "DescripcionEn", "DescripcionEs", "Escala", "Formato", "Nombre", "Offset", "Tag", "TipoVariable", "Unidad", "ValorActual", "ValorInicial" },
                values: new object[,]
                {
                    { 1, 0, null, null, 0.10000000000000001, "0.01", "Tensión Batería", 0.0, "V_BAT", "UINT16", "V", 100.91, 0.0 },
                    { 2, 2, null, null, 0.10000000000000001, "0.01", "Corriente Carga", 0.0, "I_CARGA", "UINT16", "A", 23.120000000000001, 0.0 },
                    { 3, 4, null, null, 0.10000000000000001, "0.01", "Temp. Transformador", -40.0, "T_TRANS", "UINT16", "°C", 26.390000000000001, 0.0 },
                    { 4, 6, null, null, 1.0, "0", "Estado Inversor", 0.0, "EST_INV", "UINT8", "", 1.0, 0.0 },
                    { 5, 7, null, null, 1.0, "0", "Estado Rectificador", 0.0, "EST_RECT", "UINT8", "", 0.0, 0.0 },
                    { 6, 8, null, null, 1.0, "0", "Alarma Temperatura", 0.0, "ALARM_TEMP", "UINT8", "", 1.0, 0.0 },
                    { 7, 9, null, null, 0.01, "0.01", "Potencia Salida", 0.0, "P_SALIDA", "UINT16", "kW", 42.899999999999999, 0.0 },
                    { 8, 11, null, null, 0.01, "0.01", "Frecuencia Red", 0.0, "F_RED", "UINT16", "Hz", 26.809999999999999, 0.0 }
                });

            migrationBuilder.InsertData(
                table: "Windows",
                columns: new[] { "Id", "Categoria", "Descripcion", "IsActive", "Nombre", "Tipo" },
                values: new object[,]
                {
                    { 1, "Control", "Panel de control principal del sistema APS", true, "Panel Principal APS", "Normal" },
                    { 2, "Sinópticos", "Diagrama eléctrico simplificado del sistema APS", true, "Sinóptico Eléctrico APS", "Sinóptico" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Events");

            migrationBuilder.DropTable(
                name: "Signals");

            migrationBuilder.DropTable(
                name: "Windows");
        }
    }
}