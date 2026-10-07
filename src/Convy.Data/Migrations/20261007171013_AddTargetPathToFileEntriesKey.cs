using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Convy.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTargetPathToFileEntriesKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_FileEntries",
                table: "FileEntries");

            migrationBuilder.AddPrimaryKey(
                name: "PK_FileEntries",
                table: "FileEntries",
                columns: new[] { "Provider", "InfoHash", "FilePath", "TargetPath" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The old key allows one row per file: keep the first link of each.
            migrationBuilder.Sql(
                "DELETE FROM FileEntries WHERE rowid NOT IN " +
                "(SELECT MIN(rowid) FROM FileEntries GROUP BY Provider, InfoHash, FilePath);");

            migrationBuilder.DropPrimaryKey(
                name: "PK_FileEntries",
                table: "FileEntries");

            migrationBuilder.AddPrimaryKey(
                name: "PK_FileEntries",
                table: "FileEntries",
                columns: new[] { "Provider", "InfoHash", "FilePath" });
        }
    }
}
