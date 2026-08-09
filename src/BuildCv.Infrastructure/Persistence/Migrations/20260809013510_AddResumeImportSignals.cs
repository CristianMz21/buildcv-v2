using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildCv.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The four columns of the optional owned <c>ImportSignals</c> value: what the document a resume was
    /// imported from looked like to a parser.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PURELY ADDITIVE AND SAFELY REVERSIBLE, unlike the two migrations <c>CLAUDE.md</c> warns about.
    /// Every column is nullable, so every row already on disk reads back as a null <c>ImportSignals</c> —
    /// which is exactly right, because those resumes really were created before any document evidence
    /// existed. No default is fabricated: a non-null default would claim each of them came from a
    /// document, and the readability engine would then weight an ATS-parseability section it has no
    /// evidence for.
    /// </para>
    /// <para>
    /// <c>Down()</c> drops the four columns, which discards the signals of every resume imported after
    /// this deploy. That is a real loss and a small one: nothing a candidate typed is in these columns,
    /// no other aggregate references them, and re-importing the document regenerates them. The only
    /// visible consequence of a rollback is that ATS-parseability renormalizes back out of those
    /// candidates' readability reports — the behaviour every report had before this change.
    /// </para>
    /// </remarks>
    public partial class AddResumeImportSignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "Import_ColumnLayout",
                schema: "resumes",
                table: "Resumes",
                type: "tinyint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Import_HadTextLayer",
                schema: "resumes",
                table: "Resumes",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Import_PageCount",
                schema: "resumes",
                table: "Resumes",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Import_Warnings",
                schema: "resumes",
                table: "Resumes",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Import_ColumnLayout",
                schema: "resumes",
                table: "Resumes");

            migrationBuilder.DropColumn(
                name: "Import_HadTextLayer",
                schema: "resumes",
                table: "Resumes");

            migrationBuilder.DropColumn(
                name: "Import_PageCount",
                schema: "resumes",
                table: "Resumes");

            migrationBuilder.DropColumn(
                name: "Import_Warnings",
                schema: "resumes",
                table: "Resumes");
        }
    }
}
