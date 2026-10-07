using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Convy.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceTorrentStatesWithDownloadStates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DownloadStates",
                columns: table => new
                {
                    Provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ItemRef = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    IsDownloaded = table.Column<bool>(type: "INTEGER", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedDate = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadStates", x => new { x.Provider, x.ItemRef });
                });

            // Carry the torrent baselines over so nothing is reprocessed after the upgrade.
            migrationBuilder.Sql(
                "INSERT INTO DownloadStates (Provider, ItemRef, IsDownloaded, Size, UpdatedDate) " +
                "SELECT 'qbittorrent', InfoHash, IsDownloaded, Size, UpdatedDate FROM TorrentStates;");

            migrationBuilder.DropTable(
                name: "TorrentStates");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TorrentStates",
                columns: table => new
                {
                    InfoHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    IsDownloaded = table.Column<bool>(type: "INTEGER", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedDate = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TorrentStates", x => x.InfoHash);
                });

            migrationBuilder.Sql(
                "INSERT INTO TorrentStates (InfoHash, IsDownloaded, Size, UpdatedDate) " +
                "SELECT ItemRef, IsDownloaded, Size, UpdatedDate FROM DownloadStates WHERE Provider = 'qbittorrent';");

            migrationBuilder.DropTable(
                name: "DownloadStates");
        }
    }
}
