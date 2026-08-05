using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildCv.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // ADDITIVE, AND THE Down() IS SAFE IN THE SENSE THAT MATTERS — it is worth saying which sense,
    // because CLAUDE.md flags two migrations in this chain that are not.
    //
    // Two new tables in a new schema. Nothing existing is touched: no column is altered, no stored value
    // is reinterpreted, and no row anywhere else in the database changes meaning in either direction. A
    // rollback drops exactly what this created and leaves the rest of the model as it found it.
    //
    // WHAT A ROLLBACK DESTROYS is every readability report written since it was applied, and that is a
    // real loss rather than a self-healing one: a report is a fact about the CV AS IT STOOD when it was
    // taken, so re-running the endpoint afterwards produces a report about today's resume and not about
    // the one the old row described. What is NOT lost is anything a candidate typed — every column here
    // is a score, a weight, a timestamp or advice this engine generated — and the current answer is
    // always one request away, because a readability report is a pure function of the resume and the
    // date. So the cost of rolling back is the history, not the feature and not the data.
    //
    // The empty `readability` schema survives a rollback. EnsureSchema has no counterpart in
    // MigrationBuilder, and dropping a schema by hand in Down() would fail whenever anything else had
    // since been created in it.
    public partial class AddReadabilityReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "readability");

            migrationBuilder.CreateTable(
                name: "Reports",
                schema: "readability",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompletenessScore = table.Column<double>(type: "float", nullable: false),
                    ContactScore = table.Column<double>(type: "float", nullable: false),
                    AchievementsScore = table.Column<double>(type: "float", nullable: false),
                    ChronologyScore = table.Column<double>(type: "float", nullable: false),
                    AtsParseabilityScore = table.Column<double>(type: "float", nullable: false),
                    Weights = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: false),
                    ResumeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EvaluatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    Seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reports", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "Recommendations",
                schema: "readability",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Section = table.Column<byte>(type: "tinyint", nullable: false),
                    Priority = table.Column<byte>(type: "tinyint", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    Message = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Impact = table.Column<double>(type: "float", nullable: false),
                    ReadabilityReportId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Recommendations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Recommendations_Reports_ReadabilityReportId",
                        column: x => x.ReadabilityReportId,
                        principalSchema: "readability",
                        principalTable: "Reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Recommendations_ReadabilityReportId",
                schema: "readability",
                table: "Recommendations",
                column: "ReadabilityReportId");

            migrationBuilder.CreateIndex(
                name: "IX_Recommendations_Section_Priority",
                schema: "readability",
                table: "Recommendations",
                columns: new[] { "Section", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_Reports_EvaluatedAt",
                schema: "readability",
                table: "Reports",
                column: "EvaluatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_ResumeId_Seq",
                schema: "readability",
                table: "Reports",
                columns: new[] { "ResumeId", "Seq" });

            migrationBuilder.CreateIndex(
                name: "IX_Reports_Seq",
                schema: "readability",
                table: "Reports",
                column: "Seq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Recommendations",
                schema: "readability");

            migrationBuilder.DropTable(
                name: "Reports",
                schema: "readability");
        }
    }
}
