using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phantom.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddServerDownloadForDevice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ForDevice",
                table: "ServerDownloads",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ForDevice",
                table: "ServerDownloads");
        }
    }
}
