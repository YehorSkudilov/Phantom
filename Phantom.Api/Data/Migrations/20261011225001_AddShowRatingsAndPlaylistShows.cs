using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phantom.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddShowRatingsAndPlaylistShows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "MovieId",
                table: "PlaylistItems",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<int>(
                name: "EpisodeId",
                table: "PlaylistItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ShowId",
                table: "PlaylistItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ShowRatings",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    ShowId = table.Column<int>(type: "INTEGER", nullable: false),
                    Rating = table.Column<double>(type: "REAL", nullable: false),
                    RatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShowRatings", x => new { x.UserId, x.ShowId });
                    table.ForeignKey(
                        name: "FK_ShowRatings_Shows_ShowId",
                        column: x => x.ShowId,
                        principalTable: "Shows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistItems_EpisodeId",
                table: "PlaylistItems",
                column: "EpisodeId");

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistItems_ShowId",
                table: "PlaylistItems",
                column: "ShowId");

            migrationBuilder.CreateIndex(
                name: "IX_ShowRatings_ShowId",
                table: "ShowRatings",
                column: "ShowId");

            migrationBuilder.AddForeignKey(
                name: "FK_PlaylistItems_Episodes_EpisodeId",
                table: "PlaylistItems",
                column: "EpisodeId",
                principalTable: "Episodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PlaylistItems_Shows_ShowId",
                table: "PlaylistItems",
                column: "ShowId",
                principalTable: "Shows",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PlaylistItems_Episodes_EpisodeId",
                table: "PlaylistItems");

            migrationBuilder.DropForeignKey(
                name: "FK_PlaylistItems_Shows_ShowId",
                table: "PlaylistItems");

            migrationBuilder.DropTable(
                name: "ShowRatings");

            migrationBuilder.DropIndex(
                name: "IX_PlaylistItems_EpisodeId",
                table: "PlaylistItems");

            migrationBuilder.DropIndex(
                name: "IX_PlaylistItems_ShowId",
                table: "PlaylistItems");

            migrationBuilder.DropColumn(
                name: "EpisodeId",
                table: "PlaylistItems");

            migrationBuilder.DropColumn(
                name: "ShowId",
                table: "PlaylistItems");

            migrationBuilder.AlterColumn<int>(
                name: "MovieId",
                table: "PlaylistItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);
        }
    }
}
