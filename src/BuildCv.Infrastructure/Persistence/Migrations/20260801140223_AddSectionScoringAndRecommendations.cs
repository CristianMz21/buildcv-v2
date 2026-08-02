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
                defaultValue: "");
        }
    }
}
