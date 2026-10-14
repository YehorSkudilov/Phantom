using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phantom.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLanguageAndCountries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Countries",
                table: "Shows",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalLanguage",
                table: "Shows",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Countries",
                table: "Movies",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalLanguage",
                table: "Movies",
                type: "TEXT",
                nullable: true);

            // Titles' details now include the language and countries they were made in (for the Language and Country
            // filters). Every title is queued for a fresh lookup, once, so the library gets them without anyone pressing
            // Refresh metadata.
            migrationBuilder.Sql("UPDATE Movies SET MetadataRefreshedAt = NULL;");
            migrationBuilder.Sql("UPDATE Shows SET MetadataRefreshedAt = NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Countries",
                table: "Shows");

            migrationBuilder.DropColumn(
                name: "OriginalLanguage",
                table: "Shows");

            migrationBuilder.DropColumn(
                name: "Countries",
                table: "Movies");

            migrationBuilder.DropColumn(
                name: "OriginalLanguage",
                table: "Movies");
        }
    }
}
