using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace ApsMonitor.Migrations
{
    /// <inheritdoc />
    public partial class AddTrdpConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "Events",
                keyColumn: "Id",
                keyValue: 1);

            migrationBuilder.DeleteData(
                table: "Signals",
                keyColumn: "Id",
                keyValue: 1);

            migrationBuilder.DeleteData(
                table: "Signals",
                keyColumn: "Id",
                keyValue: 2);

            migrationBuilder.DeleteData(
                table: "Signals",
                keyColumn: "Id",
                keyValue: 3);

            migrationBuilder.DeleteData(
                table: "Signals",
                keyColumn: "Id",
                keyValue: 4);

            migrationBuilder.DeleteData(
                table: "Signals",
                keyColumn: "Id",
                keyValue: 5);

            migrationBuilder.DeleteData(
                table: "Signals",
                keyColumn: "Id",
                keyValue: 6);

            migrationBuilder.DeleteData(
                table: "Signals",
                keyColumn: "Id",
                keyValue: 7);

            migrationBuilder.DeleteData(
                table: "Signals",
                keyColumn: "Id",
                keyValue: 8);

            migrationBuilder.DeleteData(
                table: "Windows",
                keyColumn: "Id",
                keyValue: 1);

            migrationBuilder.DeleteData(
                table: "Windows",
                keyColumn: "Id",
                keyValue: 2);

            migrationBuilder.AddColumn<string>(
                name: "AllowedRolesJson",
                table: "Windows",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "FailuresConfigJson",
                table: "Windows",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "PermitirMantenimiento",
                table: "Windows",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "BitEsAlarma",
                table: "Signals",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsAscii",
                table: "Signals",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "NodoDescripcion",
                table: "Signals",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "NodoNumero",
                table: "Signals",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "Commands",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Nombre = table.Column<string>(type: "TEXT", nullable: false),
                    CommandValue = table.Column<string>(type: "TEXT", nullable: false),
                    RequiereConfirmacion = table.Column<bool>(type: "INTEGER", nullable: false),
                    Estilo = table.Column<string>(type: "TEXT", nullable: false),
                    Tipo = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Commands", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TrdpConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Nombre = table.Column<string>(type: "TEXT", nullable: false),
                    Descripcion = table.Column<string>(type: "TEXT", nullable: false),
                    UltimaModificacion = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UseDynamicMapping = table.Column<bool>(type: "INTEGER", nullable: false),
                    ControlFrameJson = table.Column<string>(type: "TEXT", nullable: false),
                    CommsDatasetsJson = table.Column<string>(type: "TEXT", nullable: false),
                    NetworksJson = table.Column<string>(type: "TEXT", nullable: false),
                    TcmsOkJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrdpConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    Nombre = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Commands");

            migrationBuilder.DropTable(
                name: "TrdpConfigs");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropColumn(
                name: "AllowedRolesJson",
                table: "Windows");

            migrationBuilder.DropColumn(
                name: "FailuresConfigJson",
                table: "Windows");

            migrationBuilder.DropColumn(
                name: "PermitirMantenimiento",
                table: "Windows");

            migrationBuilder.DropColumn(
                name: "BitEsAlarma",
                table: "Signals");

            migrationBuilder.DropColumn(
                name: "IsAscii",
                table: "Signals");

            migrationBuilder.DropColumn(
                name: "NodoDescripcion",
                table: "Signals");

            migrationBuilder.DropColumn(
                name: "NodoNumero",
                table: "Signals");

            migrationBuilder.InsertData(
                table: "Events",
                columns: new[] { "Id", "Estado", "Fecha", "Mensaje" },
                values: new object[] { 1, "OK", new DateTime(2023, 3, 20, 12, 13, 14, 0, DateTimeKind.Unspecified), "Arrancar Inversor" });

            migrationBuilder.InsertData(
                table: "Signals",
                columns: new[] { "Id", "BitTextoActivo", "BitTextoInactivo", "BytePosicion", "DescripcionEn", "DescripcionEs", "Escala", "Formato", "Nombre", "Offset", "Tag", "TipoVariable", "Unidad", "ValorActual", "ValorInicial" },
                values: new object[,]
                {
                    { 1, "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]", "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]", 0, null, null, 0.10000000000000001, "0.01", "Tensión Batería", 0.0, "V_BAT", "UINT16", "V", 100.91, 0.0 },
                    { 2, "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]", "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]", 2, null, null, 0.10000000000000001, "0.01", "Corriente Carga", 0.0, "I_CARGA", "UINT16", "A", 23.120000000000001, 0.0 },
                    { 3, "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]", "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]", 4, null, null, 0.10000000000000001, "0.01", "Temp. Transformador", -40.0, "T_TRANS", "UINT16", "°C", 26.390000000000001, 0.0 },
                    { 4, "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]", "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]", 6, null, null, 1.0, "0", "Estado Inversor", 0.0, "EST_INV", "UINT8", "", 1.0, 0.0 },
                    { 5, "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]", "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]", 7, null, null, 1.0, "0", "Estado Rectificador", 0.0, "EST_RECT", "UINT8", "", 0.0, 0.0 },
                    { 6, "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]", "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]", 8, null, null, 1.0, "0", "Alarma Temperatura", 0.0, "ALARM_TEMP", "UINT8", "", 1.0, 0.0 },
                    { 7, "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]", "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]", 9, null, null, 0.01, "0.01", "Potencia Salida", 0.0, "P_SALIDA", "UINT16", "kW", 42.899999999999999, 0.0 },
                    { 8, "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]", "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]", 11, null, null, 0.01, "0.01", "Frecuencia Red", 0.0, "F_RED", "UINT16", "Hz", 26.809999999999999, 0.0 }
                });

            migrationBuilder.InsertData(
                table: "Windows",
                columns: new[] { "Id", "Categoria", "ContentJson", "Descripcion", "IsActive", "Nombre", "Tipo" },
                values: new object[,]
                {
                    { 1, "Control", "[]", "Panel de control principal del sistema APS", true, "Panel Principal APS", "Normal" },
                    { 2, "Sinópticos", "[]", "Diagrama eléctrico simplificado del sistema APS", true, "Sinóptico Eléctrico APS", "Sinóptico" }
                });
        }
    }
}
