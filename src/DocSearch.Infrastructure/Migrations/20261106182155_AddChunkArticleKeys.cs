using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocSearch.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddChunkArticleKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "article_keys",
                table: "chunks",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.CreateIndex(
                name: "ix_chunks_article_keys",
                table: "chunks",
                column: "article_keys")
                .Annotation("Npgsql:IndexMethod", "gin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_chunks_article_keys",
                table: "chunks");

            migrationBuilder.DropColumn(
                name: "article_keys",
                table: "chunks");
        }
    }
}
