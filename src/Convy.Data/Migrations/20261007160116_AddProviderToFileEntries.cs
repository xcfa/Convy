using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Convy.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderToFileEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_FileEntries",
                table: "FileEntries");

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "FileEntries",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "qbittorrent"); // every file linked so far came from qBittorrent

            migrationBuilder.AddPrimaryKey(
                name: "PK_FileEntries",
                table: "FileEntries",
                columns: new[] { "Provider", "InfoHash", "FilePath" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_FileEntries",
                table: "FileEntries");

            // Without the provider column only qBittorrent rows fit the old key.
            migrationBuilder.Sql("DELETE FROM FileEntries WHERE Provider <> 'qbittorrent';");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "FileEntries");

            migrationBuilder.AddPrimaryKey(
                name: "PK_FileEntries",
                table: "FileEntries",
                columns: new[] { "InfoHash", "FilePath" });
        }
    }
}
