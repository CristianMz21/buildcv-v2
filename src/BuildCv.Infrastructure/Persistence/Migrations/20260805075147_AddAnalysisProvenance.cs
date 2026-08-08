using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildCv.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // ADDITIVE, AND THE Down() IS GENUINELY SAFE — unlike the two migrations CLAUDE.md flags, which is
    // why it is worth saying so here rather than leaving a reader to check.
    //
    // Two nullable columns are added and dropped. Nothing existing is altered, so no stored value is
    // reinterpreted in either direction, and every row that predates this reads back null.
    //
    // What a rollback destroys is the provenance recorded since it was applied, and that loss is
    // self-healing rather than silent: null is defined as "unknown, therefore STALE" everywhere it is
    // read, so a rolled-back deployment re-scores instead of trusting a row it can no longer explain, and
    // re-applying re-records provenance on the next score. The value is derived from the resume and the
    // posting, so nothing a candidate typed is lost.
    public partial class AddAnalysisProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "JobPostingUpdatedAt",
                schema: "scoring",
                table: "Analyses",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResumeUpdatedAt",
                schema: "scoring",
                table: "Analyses",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "JobPostingUpdatedAt",
                schema: "scoring",
                table: "Analyses");

            migrationBuilder.DropColumn(
                name: "ResumeUpdatedAt",
                schema: "scoring",
                table: "Analyses");
        }
    }
}
