using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phantom.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddShows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Credits_Movies_MovieId",
                table: "Credits");

            migrationBuilder.DropForeignKey(
                name: "FK_Releases_Movies_MovieId",
                table: "Releases");

            migrationBuilder.DropForeignKey(
                name: "FK_ServerDownloads_Movies_MovieId",
                table: "ServerDownloads");

            migrationBuilder.AlterColumn<int>(
                name: "MovieId",
                table: "ServerDownloads",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<int>(
                name: "EpisodeId",
                table: "ServerDownloads",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "MovieId",
                table: "Releases",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<int>(
                name: "EpisodeNumber",
                table: "Releases",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SeasonNumber",
                table: "Releases",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ShowId",
                table: "Releases",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "MovieId",
                table: "Credits",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<int>(
                name: "ShowId",
                table: "Credits",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Shows",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    SortTitle = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalTitle = table.Column<string>(type: "TEXT", nullable: true),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    Overview = table.Column<string>(type: "TEXT", nullable: true),
                    PosterUrl = table.Column<string>(type: "TEXT", nullable: true),
                    BackdropUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Rating = table.Column<double>(type: "REAL", nullable: true),
                    ContentRating = table.Column<string>(type: "TEXT", nullable: true),
                    Network = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: true),
                    EpisodeRuntimeMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    TrailerUrl = table.Column<string>(type: "TEXT", nullable: true),
                    ImdbId = table.Column<string>(type: "TEXT", nullable: true),
                    TmdbId = table.Column<int>(type: "INTEGER", nullable: true),
                    ExternalKey = table.Column<string>(type: "TEXT", nullable: true),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    MetadataRefreshedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Episodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ShowId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    EpisodeNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Overview = table.Column<string>(type: "TEXT", nullable: true),
                    StillUrl = table.Column<string>(type: "TEXT", nullable: true),
                    AirDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    RuntimeMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    TmdbId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Episodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Episodes_Shows_ShowId",
                        column: x => x.ShowId,
                        principalTable: "Shows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GenreShow",
                columns: table => new
                {
                    GenresId = table.Column<int>(type: "INTEGER", nullable: false),
                    ShowsId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenreShow", x => new { x.GenresId, x.ShowsId });
                    table.ForeignKey(
                        name: "FK_GenreShow_Genres_GenresId",
                        column: x => x.GenresId,
                        principalTable: "Genres",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GenreShow_Shows_ShowsId",
                        column: x => x.ShowsId,
                        principalTable: "Shows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShowWatchlist",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    ShowId = table.Column<int>(type: "INTEGER", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShowWatchlist", x => new { x.UserId, x.ShowId });
                    table.ForeignKey(
                        name: "FK_ShowWatchlist_Shows_ShowId",
                        column: x => x.ShowId,
                        principalTable: "Shows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EpisodeWatchStates",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    EpisodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    PositionMs = table.Column<long>(type: "INTEGER", nullable: false),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: false),
                    Watched = table.Column<bool>(type: "INTEGER", nullable: false),
                    ViewCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastViewedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EpisodeWatchStates", x => new { x.UserId, x.EpisodeId });
                    table.ForeignKey(
                        name: "FK_EpisodeWatchStates_Episodes_EpisodeId",
                        column: x => x.EpisodeId,
                        principalTable: "Episodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ServerDownloads_EpisodeId",
                table: "ServerDownloads",
                column: "EpisodeId");

            migrationBuilder.CreateIndex(
                name: "IX_Releases_ShowId_SeasonNumber_EpisodeNumber",
                table: "Releases",
                columns: new[] { "ShowId", "SeasonNumber", "EpisodeNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_Credits_ShowId",
                table: "Credits",
                column: "ShowId");

            migrationBuilder.CreateIndex(
                name: "IX_Episodes_ShowId_SeasonNumber_EpisodeNumber",
                table: "Episodes",
                columns: new[] { "ShowId", "SeasonNumber", "EpisodeNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EpisodeWatchStates_EpisodeId",
                table: "EpisodeWatchStates",
                column: "EpisodeId");

            migrationBuilder.CreateIndex(
                name: "IX_GenreShow_ShowsId",
                table: "GenreShow",
                column: "ShowsId");

            migrationBuilder.CreateIndex(
                name: "IX_Shows_ExternalKey",
                table: "Shows",
                column: "ExternalKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Shows_ImdbId",
                table: "Shows",
                column: "ImdbId");

            migrationBuilder.CreateIndex(
                name: "IX_Shows_SortTitle",
                table: "Shows",
                column: "SortTitle");

            migrationBuilder.CreateIndex(
                name: "IX_Shows_TmdbId",
                table: "Shows",
                column: "TmdbId");

            migrationBuilder.CreateIndex(
                name: "IX_ShowWatchlist_ShowId",
                table: "ShowWatchlist",
                column: "ShowId");

            migrationBuilder.AddForeignKey(
                name: "FK_Credits_Movies_MovieId",
                table: "Credits",
                column: "MovieId",
                principalTable: "Movies",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Credits_Shows_ShowId",
                table: "Credits",
                column: "ShowId",
                principalTable: "Shows",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Releases_Movies_MovieId",
                table: "Releases",
                column: "MovieId",
                principalTable: "Movies",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Releases_Shows_ShowId",
                table: "Releases",
                column: "ShowId",
                principalTable: "Shows",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ServerDownloads_Episodes_EpisodeId",
                table: "ServerDownloads",
                column: "EpisodeId",
                principalTable: "Episodes",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ServerDownloads_Movies_MovieId",
                table: "ServerDownloads",
                column: "MovieId",
                principalTable: "Movies",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Credits_Movies_MovieId",
                table: "Credits");

            migrationBuilder.DropForeignKey(
                name: "FK_Credits_Shows_ShowId",
                table: "Credits");

            migrationBuilder.DropForeignKey(
                name: "FK_Releases_Movies_MovieId",
                table: "Releases");

            migrationBuilder.DropForeignKey(
                name: "FK_Releases_Shows_ShowId",
                table: "Releases");

            migrationBuilder.DropForeignKey(
                name: "FK_ServerDownloads_Episodes_EpisodeId",
                table: "ServerDownloads");

            migrationBuilder.DropForeignKey(
                name: "FK_ServerDownloads_Movies_MovieId",
                table: "ServerDownloads");

            migrationBuilder.DropTable(
                name: "EpisodeWatchStates");

            migrationBuilder.DropTable(
                name: "GenreShow");

            migrationBuilder.DropTable(
                name: "ShowWatchlist");

            migrationBuilder.DropTable(
                name: "Episodes");

            migrationBuilder.DropTable(
                name: "Shows");

            migrationBuilder.DropIndex(
                name: "IX_ServerDownloads_EpisodeId",
                table: "ServerDownloads");

            migrationBuilder.DropIndex(
                name: "IX_Releases_ShowId_SeasonNumber_EpisodeNumber",
                table: "Releases");

            migrationBuilder.DropIndex(
                name: "IX_Credits_ShowId",
                table: "Credits");

            migrationBuilder.DropColumn(
                name: "EpisodeId",
                table: "ServerDownloads");

            migrationBuilder.DropColumn(
                name: "EpisodeNumber",
                table: "Releases");

            migrationBuilder.DropColumn(
                name: "SeasonNumber",
                table: "Releases");

            migrationBuilder.DropColumn(
                name: "ShowId",
                table: "Releases");

            migrationBuilder.DropColumn(
                name: "ShowId",
                table: "Credits");

            migrationBuilder.AlterColumn<int>(
                name: "MovieId",
                table: "ServerDownloads",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "MovieId",
                table: "Releases",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "MovieId",
                table: "Credits",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Credits_Movies_MovieId",
                table: "Credits",
                column: "MovieId",
                principalTable: "Movies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Releases_Movies_MovieId",
                table: "Releases",
                column: "MovieId",
                principalTable: "Movies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ServerDownloads_Movies_MovieId",
                table: "ServerDownloads",
                column: "MovieId",
                principalTable: "Movies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
