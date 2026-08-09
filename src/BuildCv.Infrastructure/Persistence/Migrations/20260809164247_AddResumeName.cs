using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildCv.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds the CV's own name.
    /// </summary>
    /// <remarks>
    /// SAFE IN BOTH DIRECTIONS, unlike the two migrations CLAUDE.md warns about. Up adds a NULLABLE
    /// column, so no existing row is rewritten and every CV that predates this loads unchanged with no
    /// name. Down drops it, which loses only names — a value nothing scores, nothing indexes and
    /// nothing else derives from.
    ///
    /// varbinary(max) because the column is encrypted. There is no HasMaxLength: a length here would
    /// bound the ciphertext rather than the text, and the 120-character rule lives on
    /// <c>Resume.NameMaxLength</c> where it is product policy rather than a truncation guard.
    /// </remarks>
    public partial class AddResumeName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "Name",
                schema: "resumes",
                table: "Resumes",
                type: "varbinary(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Name",
                schema: "resumes",
                table: "Resumes");
        }
    }
}
