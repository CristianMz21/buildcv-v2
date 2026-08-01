using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildCv.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLanguageAndEducationLevels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "Level",
                schema: "resumes",
                table: "Languages",
                type: "tinyint",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "EducationLevel",
                schema: "jobs",
                table: "JobPostings",
                type: "tinyint",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "Level",
                schema: "resumes",
                table: "Educations",
                type: "tinyint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LanguageRequirements",
                schema: "jobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    MinimumLevel = table.Column<byte>(type: "tinyint", nullable: false),
                    JobPostingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LanguageRequirements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LanguageRequirements_JobPostings_JobPostingId",
                        column: x => x.JobPostingId,
                        principalSchema: "jobs",
                        principalTable: "JobPostings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LanguageRequirements_JobPostingId",
                schema: "jobs",
                table: "LanguageRequirements",
                column: "JobPostingId");

            migrationBuilder.CreateIndex(
                name: "IX_LanguageRequirements_Name",
                schema: "jobs",
                table: "LanguageRequirements",
                column: "Name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LanguageRequirements",
                schema: "jobs");

            migrationBuilder.DropColumn(
                name: "Level",
                schema: "resumes",
                table: "Languages");

            migrationBuilder.DropColumn(
                name: "EducationLevel",
                schema: "jobs",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "Level",
                schema: "resumes",
                table: "Educations");
        }
    }
}
