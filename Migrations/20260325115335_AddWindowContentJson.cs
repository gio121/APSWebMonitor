using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApsMonitor.Migrations
{
    /// <inheritdoc />
    public partial class AddWindowContentJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContentJson",
                table: "Windows",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.UpdateData(
                table: "Windows",
                keyColumn: "Id",
                keyValue: 1,
                column: "ContentJson",
                value: "[]");

            migrationBuilder.UpdateData(
                table: "Windows",
                keyColumn: "Id",
                keyValue: 2,
                column: "ContentJson",
                value: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContentJson",
                table: "Windows");
        }
    }
}
