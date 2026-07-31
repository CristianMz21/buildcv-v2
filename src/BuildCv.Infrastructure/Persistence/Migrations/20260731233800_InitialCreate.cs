using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildCv.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "identity");

            migrationBuilder.EnsureSchema(
                name: "scoring");

            migrationBuilder.EnsureSchema(
                name: "resumes");

            migrationBuilder.EnsureSchema(
                name: "jobs");

            migrationBuilder.EnsureSchema(
                name: "orgs");

            migrationBuilder.CreateTable(
                name: "Accounts",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<byte[]>(type: "varbinary(512)", maxLength: 512, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Role = table.Column<byte>(type: "tinyint", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EmailVerifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastLoginAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FailedLoginCount = table.Column<int>(type: "int", nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EmailHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    Seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "Analyses",
                schema: "scoring",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SkillsScore = table.Column<double>(type: "float", nullable: false),
                    ExperienceScore = table.Column<double>(type: "float", nullable: false),
                    EducationScore = table.Column<double>(type: "float", nullable: false),
                    CertificationsScore = table.Column<double>(type: "float", nullable: false),
                    ProjectsScore = table.Column<double>(type: "float", nullable: false),
                    Weights = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: false),
                    ResumeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobPostingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScoredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Recommendations = table.Column<string>(type: "nvarchar(max)", nullable: false),
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
                    table.PrimaryKey("PK_Analyses", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "JobPostings",
                schema: "jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", maxLength: 5000, nullable: true),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CompanyName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClosesAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
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
                    table.PrimaryKey("PK_JobPostings", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "Organizations",
                schema: "orgs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Slug = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
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
                    table.PrimaryKey("PK_Organizations", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "Resumes",
                schema: "resumes",
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
                    table.PrimaryKey("PK_Resumes", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "RefreshTokens",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Token = table.Column<byte[]>(type: "varbinary(1024)", maxLength: 1024, nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    Seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TokenHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshTokens", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_RefreshTokens_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "identity",
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobRequirements",
                schema: "jobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Skill = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Priority = table.Column<byte>(type: "tinyint", nullable: false),
                    Weight = table.Column<double>(type: "float", nullable: false),
                    JobPostingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobRequirements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobRequirements_JobPostings_JobPostingId",
                        column: x => x.JobPostingId,
                        principalSchema: "jobs",
                        principalTable: "JobPostings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Responsibilities",
                schema: "jobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    JobPostingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Responsibilities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Responsibilities_JobPostings_JobPostingId",
                        column: x => x.JobPostingId,
                        principalSchema: "jobs",
                        principalTable: "JobPostings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Memberships",
                schema: "orgs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<byte>(type: "tinyint", nullable: false),
                    JoinedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Memberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Memberships_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "orgs",
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Awards",
                schema: "resumes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Title = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Awarder = table.Column<byte[]>(type: "varbinary(768)", maxLength: 768, nullable: true),
                    Date = table.Column<DateOnly>(type: "date", nullable: true),
                    Summary = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    ResumeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Awards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Awards_Resumes_ResumeId",
                        column: x => x.ResumeId,
                        principalSchema: "resumes",
                        principalTable: "Resumes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Certificates",
                schema: "resumes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Issuer = table.Column<byte[]>(type: "varbinary(768)", maxLength: 768, nullable: false),
                    CredentialId = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    CredentialUrl = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    ValidityPeriod = table.Column<string>(type: "varchar(21)", unicode: false, maxLength: 21, nullable: true),
                    ResumeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Certificates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Certificates_Resumes_ResumeId",
                        column: x => x.ResumeId,
                        principalSchema: "resumes",
                        principalTable: "Resumes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Educations",
                schema: "resumes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Institution = table.Column<byte[]>(type: "varbinary(768)", maxLength: 768, nullable: false),
                    Degree = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    FieldOfStudy = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    Period = table.Column<string>(type: "varchar(21)", unicode: false, maxLength: 21, nullable: false),
                    Grade = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    ResumeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Educations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Educations_Resumes_ResumeId",
                        column: x => x.ResumeId,
                        principalSchema: "resumes",
                        principalTable: "Resumes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Experiences",
                schema: "resumes",
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
                    ResumeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Experiences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Experiences_Resumes_ResumeId",
                        column: x => x.ResumeId,
                        principalSchema: "resumes",
                        principalTable: "Resumes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Interests",
                schema: "resumes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Keywords = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    ResumeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Interests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Interests_Resumes_ResumeId",
                        column: x => x.ResumeId,
                        principalSchema: "resumes",
                        principalTable: "Resumes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Languages",
                schema: "resumes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Fluency = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ResumeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Languages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Languages_Resumes_ResumeId",
                        column: x => x.ResumeId,
                        principalSchema: "resumes",
                        principalTable: "Resumes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                schema: "resumes",
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
                    ResumeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Projects_Resumes_ResumeId",
                        column: x => x.ResumeId,
                        principalSchema: "resumes",
                        principalTable: "Resumes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Publications",
                schema: "resumes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Title = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Publisher = table.Column<byte[]>(type: "varbinary(768)", maxLength: 768, nullable: true),
                    Url = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    ReleaseDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Summary = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    ResumeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Publications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Publications_Resumes_ResumeId",
                        column: x => x.ResumeId,
                        principalSchema: "resumes",
                        principalTable: "Resumes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "References",
                schema: "resumes",
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
                    ResumeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_References", x => x.Id);
                    table.ForeignKey(
                        name: "FK_References_Resumes_ResumeId",
                        column: x => x.ResumeId,
                        principalSchema: "resumes",
                        principalTable: "Resumes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Skills",
                schema: "resumes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Level = table.Column<byte>(type: "tinyint", nullable: true),
                    YearsOfExperience = table.Column<int>(type: "int", nullable: true),
                    Keywords = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ResumeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Skills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Skills_Resumes_ResumeId",
                        column: x => x.ResumeId,
                        principalSchema: "resumes",
                        principalTable: "Resumes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_EmailHash",
                schema: "identity",
                table: "Accounts",
                column: "EmailHash",
                unique: true,
                filter: "[DeletedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_Seq",
                schema: "identity",
                table: "Accounts",
                column: "Seq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_Analyses_ResumeId_Seq",
                schema: "scoring",
                table: "Analyses",
                columns: new[] { "ResumeId", "Seq" });

            migrationBuilder.CreateIndex(
                name: "IX_Analyses_ScoredAt",
                schema: "scoring",
                table: "Analyses",
                column: "ScoredAt");

            migrationBuilder.CreateIndex(
                name: "IX_Analyses_Seq",
                schema: "scoring",
                table: "Analyses",
                column: "Seq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_Awards_ResumeId",
                schema: "resumes",
                table: "Awards",
                column: "ResumeId");

            migrationBuilder.CreateIndex(
                name: "IX_Certificates_ResumeId",
                schema: "resumes",
                table: "Certificates",
                column: "ResumeId");

            migrationBuilder.CreateIndex(
                name: "IX_Educations_ResumeId",
                schema: "resumes",
                table: "Educations",
                column: "ResumeId");

            migrationBuilder.CreateIndex(
                name: "IX_Experiences_ResumeId",
                schema: "resumes",
                table: "Experiences",
                column: "ResumeId");

            migrationBuilder.CreateIndex(
                name: "IX_Interests_ResumeId",
                schema: "resumes",
                table: "Interests",
                column: "ResumeId");

            migrationBuilder.CreateIndex(
                name: "IX_JobPostings_CompanyId_Seq",
                schema: "jobs",
                table: "JobPostings",
                columns: new[] { "CompanyId", "Seq" });

            migrationBuilder.CreateIndex(
                name: "IX_JobPostings_OwnerId_Seq",
                schema: "jobs",
                table: "JobPostings",
                columns: new[] { "OwnerId", "Seq" });

            migrationBuilder.CreateIndex(
                name: "IX_JobPostings_Seq",
                schema: "jobs",
                table: "JobPostings",
                column: "Seq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_JobPostings_Status",
                schema: "jobs",
                table: "JobPostings",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_JobRequirements_JobPostingId",
                schema: "jobs",
                table: "JobRequirements",
                column: "JobPostingId");

            migrationBuilder.CreateIndex(
                name: "IX_JobRequirements_Skill",
                schema: "jobs",
                table: "JobRequirements",
                column: "Skill");

            migrationBuilder.CreateIndex(
                name: "IX_Languages_Name",
                schema: "resumes",
                table: "Languages",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Languages_ResumeId",
                schema: "resumes",
                table: "Languages",
                column: "ResumeId");

            migrationBuilder.CreateIndex(
                name: "IX_Memberships_OrganizationId_AccountId",
                schema: "orgs",
                table: "Memberships",
                columns: new[] { "OrganizationId", "AccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Organizations_Seq",
                schema: "orgs",
                table: "Organizations",
                column: "Seq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_Organizations_Slug",
                schema: "orgs",
                table: "Organizations",
                column: "Slug",
                unique: true,
                filter: "[DeletedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_ResumeId",
                schema: "resumes",
                table: "Projects",
                column: "ResumeId");

            migrationBuilder.CreateIndex(
                name: "IX_Publications_ResumeId",
                schema: "resumes",
                table: "Publications",
                column: "ResumeId");

            migrationBuilder.CreateIndex(
                name: "IX_References_ResumeId",
                schema: "resumes",
                table: "References",
                column: "ResumeId");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_AccountId",
                schema: "identity",
                table: "RefreshTokens",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_ExpiresAt",
                schema: "identity",
                table: "RefreshTokens",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_Seq",
                schema: "identity",
                table: "RefreshTokens",
                column: "Seq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_TokenHash",
                schema: "identity",
                table: "RefreshTokens",
                column: "TokenHash",
                unique: true,
                filter: "[DeletedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Responsibilities_JobPostingId",
                schema: "jobs",
                table: "Responsibilities",
                column: "JobPostingId");

            migrationBuilder.CreateIndex(
                name: "IX_Resumes_OwnerId_Seq",
                schema: "resumes",
                table: "Resumes",
                columns: new[] { "OwnerId", "Seq" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_Resumes_Seq",
                schema: "resumes",
                table: "Resumes",
                column: "Seq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_Skills_Name",
                schema: "resumes",
                table: "Skills",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Skills_ResumeId",
                schema: "resumes",
                table: "Skills",
                column: "ResumeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Analyses",
                schema: "scoring");

            migrationBuilder.DropTable(
                name: "Awards",
                schema: "resumes");

            migrationBuilder.DropTable(
                name: "Certificates",
                schema: "resumes");

            migrationBuilder.DropTable(
                name: "Educations",
                schema: "resumes");

            migrationBuilder.DropTable(
                name: "Experiences",
                schema: "resumes");

            migrationBuilder.DropTable(
                name: "Interests",
                schema: "resumes");

            migrationBuilder.DropTable(
                name: "JobRequirements",
                schema: "jobs");

            migrationBuilder.DropTable(
                name: "Languages",
                schema: "resumes");

            migrationBuilder.DropTable(
                name: "Memberships",
                schema: "orgs");

            migrationBuilder.DropTable(
                name: "Projects",
                schema: "resumes");

            migrationBuilder.DropTable(
                name: "Publications",
                schema: "resumes");

            migrationBuilder.DropTable(
                name: "References",
                schema: "resumes");

            migrationBuilder.DropTable(
                name: "RefreshTokens",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "Responsibilities",
                schema: "jobs");

            migrationBuilder.DropTable(
                name: "Skills",
                schema: "resumes");

            migrationBuilder.DropTable(
                name: "Organizations",
                schema: "orgs");

            migrationBuilder.DropTable(
                name: "Accounts",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "JobPostings",
                schema: "jobs");

            migrationBuilder.DropTable(
                name: "Resumes",
                schema: "resumes");
        }
    }
}
