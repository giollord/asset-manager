using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class ImportWithoutConfirmation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ReplaceSourceId",
                table: "ImportSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Results",
                table: "ImportSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ResultsDismissed",
                table: "ImportSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReplaceSourceId",
                table: "ImportSessions");

            migrationBuilder.DropColumn(
                name: "Results",
                table: "ImportSessions");

            migrationBuilder.DropColumn(
                name: "ResultsDismissed",
                table: "ImportSessions");
        }
    }
}
