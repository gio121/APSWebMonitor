using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApsMonitor.Migrations
{
    /// <inheritdoc />
    public partial class AddBitTexts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BitTextoActivo",
                table: "Signals",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "BitTextoInactivo",
                table: "Signals",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.UpdateData(
                table: "Signals",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "BitTextoActivo", "BitTextoInactivo" },
                values: new object[] { "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]", "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]" });

            migrationBuilder.UpdateData(
                table: "Signals",
                keyColumn: "Id",
                keyValue: 2,
                columns: new[] { "BitTextoActivo", "BitTextoInactivo" },
                values: new object[] { "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]", "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]" });

            migrationBuilder.UpdateData(
                table: "Signals",
                keyColumn: "Id",
                keyValue: 3,
                columns: new[] { "BitTextoActivo", "BitTextoInactivo" },
                values: new object[] { "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]", "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]" });

            migrationBuilder.UpdateData(
                table: "Signals",
                keyColumn: "Id",
                keyValue: 4,
                columns: new[] { "BitTextoActivo", "BitTextoInactivo" },
                values: new object[] { "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]", "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]" });

            migrationBuilder.UpdateData(
                table: "Signals",
                keyColumn: "Id",
                keyValue: 5,
                columns: new[] { "BitTextoActivo", "BitTextoInactivo" },
                values: new object[] { "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]", "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]" });

            migrationBuilder.UpdateData(
                table: "Signals",
                keyColumn: "Id",
                keyValue: 6,
                columns: new[] { "BitTextoActivo", "BitTextoInactivo" },
                values: new object[] { "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]", "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]" });

            migrationBuilder.UpdateData(
                table: "Signals",
                keyColumn: "Id",
                keyValue: 7,
                columns: new[] { "BitTextoActivo", "BitTextoInactivo" },
                values: new object[] { "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]", "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]" });

            migrationBuilder.UpdateData(
                table: "Signals",
                keyColumn: "Id",
                keyValue: 8,
                columns: new[] { "BitTextoActivo", "BitTextoInactivo" },
                values: new object[] { "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]", "[null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BitTextoActivo",
                table: "Signals");

            migrationBuilder.DropColumn(
                name: "BitTextoInactivo",
                table: "Signals");
        }
    }
}
