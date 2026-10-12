using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phantom.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RefetchMetadataForKeywords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Titles' details now come with TMDB's keywords (which give the Erotic genre). Every title is queued for a
            // fresh lookup, once, so the library gets them without anyone pressing Refresh metadata.
            migrationBuilder.Sql("UPDATE Movies SET MetadataRefreshedAt = NULL;");
            migrationBuilder.Sql("UPDATE Shows SET MetadataRefreshedAt = NULL;");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
