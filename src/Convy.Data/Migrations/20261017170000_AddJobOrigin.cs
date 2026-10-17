using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Convy.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJobOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InfoUrl",
                table: "SearchResults",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginQuery",
                table: "Jobs",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginUrl",
                table: "Jobs",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InfoUrl",
                table: "SearchResults");

            migrationBuilder.DropColumn(
                name: "OriginQuery",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "OriginUrl",
                table: "Jobs");
        }
    }
}
