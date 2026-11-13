using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocSearch.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReindexJobActiveCollectionIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_reindex_jobs_collection_id",
                table: "reindex_jobs");

            migrationBuilder.CreateIndex(
                name: "ix_reindex_jobs_active_collection",
                table: "reindex_jobs",
                column: "collection_id",
                unique: true,
                filter: "status IN ('Queued', 'Running')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_reindex_jobs_active_collection",
                table: "reindex_jobs");

            migrationBuilder.CreateIndex(
                name: "IX_reindex_jobs_collection_id",
                table: "reindex_jobs",
                column: "collection_id");
        }
    }
}
