using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildCv.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds the candidates schema: one profile row per account and the ten item tables it owns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Purely additive, and it is the last cheap moment to add it.</b> Nothing existing is altered,
    /// dropped or backfilled — every table here is new — so this is unlike the two migrations in this
    /// chain that destroy data. It ships before there are users precisely because the same change with
    /// real accounts would need every stored resume re-read and folded into a profile, and re-reading a
    /// resume means decrypting every candidate's history in one batch job.
    /// </para>
    /// <para>
    /// <b><c>Down()</c> drops all eleven tables and therefore every profile written since deploy.</b>
    /// That is not the same loss the rest of the chain warns about: a profile is the candidate's own
    /// master data, and no CV depends on it — a Resume is a COPY, taken at generation time, and lives in
    /// its own schema untouched by this. So a rollback loses what candidates typed since the deploy, and
    /// leaves every CV they already made intact.
    /// </para>
    /// <para>
    /// <b>The unique index on <c>OwnerId</c> is filtered on the tombstone</b>, matching every other
    /// unique index here. Without the filter a soft-deleted account could never be re-registered a
    /// profile, because the row that blocks it is one no query can see.
    /// </para>
    /// </remarks>
    public partial class AddCandidateProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "candidates");

            migrationBuilder.CreateTable(
                name: "Profiles",
                schema: "candidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Contact_FullName = table.Column<byte[]>(type: "varbinary(1024)", maxLength: 1024, nullable: false),
                    Contact_Email = table.Column<byte[]>(type: "varbinary(512)", maxLength: 512, nullable: false),
                    Contact_PhoneNumber = table.Column<byte[]>(type: "varbinary(128)", maxLength: 128, nullable: true),
                    Contact_Location = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    Contact_Website = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    Contact_Summary = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    Contact_Profiles = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
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
                    table.PrimaryKey("PK_Profiles", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "Awards",
                schema: "candidates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Title = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Awarder = table.Column<byte[]>(type: "varbinary(768)", maxLength: 768, nullable: true),
                    Date = table.Column<DateOnly>(type: "date", nullable: true),
                    Summary = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    CandidateProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Awards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Awards_Profiles_CandidateProfileId",
                        column: x => x.CandidateProfileId,
                        principalSchema: "candidates",
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Certificates",
                schema: "candidates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Issuer = table.Column<byte[]>(type: "varbinary(768)", maxLength: 768, nullable: false),
                    CredentialId = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    CredentialUrl = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    ValidityPeriod = table.Column<string>(type: "varchar(21)", unicode: false, maxLength: 21, nullable: true),
                    CandidateProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Certificates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Certificates_Profiles_CandidateProfileId",
                        column: x => x.CandidateProfileId,
                        principalSchema: "candidates",
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Educations",
                schema: "candidates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Institution = table.Column<byte[]>(type: "varbinary(768)", maxLength: 768, nullable: false),
                    Degree = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    FieldOfStudy = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    Period = table.Column<string>(type: "varchar(21)", unicode: false, maxLength: 21, nullable: false),
                    Grade = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    Level = table.Column<byte>(type: "tinyint", nullable: true),
                    CandidateProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Educations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Educations_Profiles_CandidateProfileId",
                        column: x => x.CandidateProfileId,
                        principalSchema: "candidates",
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Experiences",
                schema: "candidates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Type = table.Column<byte>(type: "tinyint", nullable: false),
                    Organization = table.Column<byte[]>(type: "varbinary(768)", maxLength: 768, nullable: false),
                    Position = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Period = table.Column<string>(type: "varchar(21)", unicode: false, maxLength: 21, nullable: false),
                    Summary = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    Highlights = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    CandidateProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Experiences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Experiences_Profiles_CandidateProfileId",
                        column: x => x.CandidateProfileId,
                        principalSchema: "candidates",
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Interests",
                schema: "candidates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Keywords = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    CandidateProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Interests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Interests_Profiles_CandidateProfileId",
                        column: x => x.CandidateProfileId,
                        principalSchema: "candidates",
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Languages",
                schema: "candidates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Fluency = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    Level = table.Column<byte>(type: "tinyint", nullable: true),
                    CandidateProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Languages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Languages_Profiles_CandidateProfileId",
                        column: x => x.CandidateProfileId,
                        principalSchema: "candidates",
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                schema: "candidates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Period = table.Column<string>(type: "varchar(21)", unicode: false, maxLength: 21, nullable: false),
                    Description = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    RepositoryUrl = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    LiveDemoUrl = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    Technologies = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Highlights = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    CandidateProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Projects_Profiles_CandidateProfileId",
                        column: x => x.CandidateProfileId,
                        principalSchema: "candidates",
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Publications",
                schema: "candidates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Title = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Publisher = table.Column<byte[]>(type: "varbinary(768)", maxLength: 768, nullable: true),
                    Url = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    ReleaseDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Summary = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    CandidateProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Publications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Publications_Profiles_CandidateProfileId",
                        column: x => x.CandidateProfileId,
                        principalSchema: "candidates",
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "References",
                schema: "candidates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Position = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    Company = table.Column<byte[]>(type: "varbinary(768)", maxLength: 768, nullable: true),
                    Email = table.Column<byte[]>(type: "varbinary(512)", maxLength: 512, nullable: true),
                    PhoneNumber = table.Column<byte[]>(type: "varbinary(128)", maxLength: 128, nullable: true),
                    ReferenceText = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    CandidateProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_References", x => x.Id);
                    table.ForeignKey(
                        name: "FK_References_Profiles_CandidateProfileId",
                        column: x => x.CandidateProfileId,
                        principalSchema: "candidates",
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Skills",
                schema: "candidates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Level = table.Column<byte>(type: "tinyint", nullable: true),
                    YearsOfExperience = table.Column<int>(type: "int", nullable: true),
                    Keywords = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CandidateProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Skills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Skills_Profiles_CandidateProfileId",
                        column: x => x.CandidateProfileId,
                        principalSchema: "candidates",
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Awards_CandidateProfileId",
                schema: "candidates",
                table: "Awards",
                column: "CandidateProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Certificates_CandidateProfileId",
                schema: "candidates",
                table: "Certificates",
                column: "CandidateProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Educations_CandidateProfileId",
                schema: "candidates",
                table: "Educations",
                column: "CandidateProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Experiences_CandidateProfileId",
                schema: "candidates",
                table: "Experiences",
                column: "CandidateProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Interests_CandidateProfileId",
                schema: "candidates",
                table: "Interests",
                column: "CandidateProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Languages_CandidateProfileId",
                schema: "candidates",
                table: "Languages",
                column: "CandidateProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Languages_Name",
                schema: "candidates",
                table: "Languages",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_OwnerId",
                schema: "candidates",
                table: "Profiles",
                column: "OwnerId",
                unique: true,
                filter: "[DeletedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_Seq",
                schema: "candidates",
                table: "Profiles",
                column: "Seq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_CandidateProfileId",
                schema: "candidates",
                table: "Projects",
                column: "CandidateProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Publications_CandidateProfileId",
                schema: "candidates",
                table: "Publications",
                column: "CandidateProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_References_CandidateProfileId",
                schema: "candidates",
                table: "References",
                column: "CandidateProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Skills_CandidateProfileId",
                schema: "candidates",
                table: "Skills",
                column: "CandidateProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Skills_Name",
                schema: "candidates",
                table: "Skills",
                column: "Name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Awards",
                schema: "candidates");

            migrationBuilder.DropTable(
                name: "Certificates",
                schema: "candidates");

            migrationBuilder.DropTable(
                name: "Educations",
                schema: "candidates");

            migrationBuilder.DropTable(
                name: "Experiences",
                schema: "candidates");

            migrationBuilder.DropTable(
                name: "Interests",
                schema: "candidates");

            migrationBuilder.DropTable(
                name: "Languages",
                schema: "candidates");

            migrationBuilder.DropTable(
                name: "Projects",
                schema: "candidates");

            migrationBuilder.DropTable(
                name: "Publications",
                schema: "candidates");

            migrationBuilder.DropTable(
                name: "References",
                schema: "candidates");

            migrationBuilder.DropTable(
                name: "Skills",
                schema: "candidates");

            migrationBuilder.DropTable(
                name: "Profiles",
                schema: "candidates");
        }
    }
}
