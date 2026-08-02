using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildCv.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSectionScoringAndRecommendations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Recommendations",
                schema: "scoring",
                table: "Analyses");

            migrationBuilder.AddColumn<double>(
                name: "LanguagesScore",
                schema: "scoring",
                table: "Analyses",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.CreateTable(
                name: "Recommendations",
                schema: "scoring",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Section = table.Column<byte>(type: "tinyint", nullable: false),
                    Priority = table.Column<byte>(type: "tinyint", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    Message = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Impact = table.Column<double>(type: "float", nullable: false),
                    AnalysisId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Recommendations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Recommendations_Analyses_AnalysisId",
                        column: x => x.AnalysisId,
                        principalSchema: "scoring",
                        principalTable: "Analyses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Recommendations_AnalysisId",
                schema: "scoring",
                table: "Recommendations",
                column: "AnalysisId");

            migrationBuilder.CreateIndex(
                name: "IX_Recommendations_Section_Priority",
                schema: "scoring",
                table: "Recommendations",
                columns: new[] { "Section", "Priority" });
        }

        /// <inheritdoc />
        /// <remarks>
        /// THIS MIGRATION IS FORWARD-ONLY IN PRACTICE, and the reason is DATA LOSS, not a schema
        /// problem. Down() restores the schema faithfully and still destroys information that cannot
        /// be recovered by re-running Up():
        ///
        ///   - Every generated recommendation. Section, Priority, Kind, Message and Impact live only
        ///     in the scoring.Recommendations table this drops. They are derived, so re-scoring
        ///     regenerates advice — but under TODAY'S resume and posting, not the ones the analysis
        ///     was taken against, so a score history stops being explainable by the advice beside it.
        ///   - Every Analysis.LanguagesScore. The pre-chain five-section model has no column for it.
        ///
        /// The scaffolded default for the restored Recommendations column was the empty string, and
        /// that was worse than data loss — it was silent CORRUPTION OF ROWS THIS CHAIN NEVER TOUCHED.
        /// The pre-chain mapping read that column through StringListConverter -> JsonListCodec.
        /// ToStringList -> JsonSerializer.Deserialize&lt;string[]&gt;, and "" is not JSON: it throws
        /// JsonException ("The input does not contain any JSON tokens"), which surfaces as a load
        /// failure on EVERY Analysis row, including every row written before this chain existed.
        /// Measured against the converter at a7cb736 rather than inferred from the `?? []` beside it,
        /// which only covers a literal JSON `null`.
        ///
        /// '[]' is the narrowest honest default: the same pre-chain converter parses it to an empty
        /// list, so a rollback leaves every row readable and merely empty of advice.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Recommendations",
                schema: "scoring");

            migrationBuilder.DropColumn(
                name: "LanguagesScore",
                schema: "scoring",
                table: "Analyses");

            migrationBuilder.AddColumn<string>(
                name: "Recommendations",
                schema: "scoring",
                table: "Analyses",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");
        }
    }
}
