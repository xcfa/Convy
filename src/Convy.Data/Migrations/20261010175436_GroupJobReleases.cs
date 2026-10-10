using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Convy.Data.Migrations
{
    /// <inheritdoc />
    public partial class GroupJobReleases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GroupId",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PlacedFilesJson",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            // Every existing job had one release: it is its own group, so j_<id> stays valid.
            migrationBuilder.Sql("UPDATE \"Jobs\" SET \"GroupId\" = \"Id\";");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_GroupId",
                table: "Jobs",
                column: "GroupId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Jobs_GroupId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "PlacedFilesJson",
                table: "Jobs");
        }
    }
}
