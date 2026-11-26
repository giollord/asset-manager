using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanionOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Origin",
                table: "AssetFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Origin",
                table: "AssetFiles");
        }
    }
}
