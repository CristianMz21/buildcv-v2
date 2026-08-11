using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildCv.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalSignIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "PasswordHash",
                schema: "identity",
                table: "Accounts",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256);

            migrationBuilder.AddColumn<string>(
                name: "ExternalProvider",
                schema: "identity",
                table: "Accounts",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "ExternalSubject",
                schema: "identity",
                table: "Accounts",
                type: "varbinary(max)",
                nullable: true);
        }

        /// <summary>
        /// FORWARD-ONLY ONCE ANY ACCOUNT HAS SIGNED IN WITH A PROVIDER, and it refuses rather than
        /// pretending otherwise.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The scaffolded rollback set <c>PasswordHash</c> back to <c>NOT NULL DEFAULT ''</c>. That is
        /// the same shape as the <c>Analyses.Recommendations DEFAULT ''</c> this repository already
        /// caught once, and here it is worse: <c>PasswordConverter</c> reads the column through
        /// <c>Password.Create</c>, which starts with <c>ArgumentException.ThrowIfNullOrWhiteSpace</c>.
        /// So an empty string is not "an account that cannot log in" — it is a row that <b>throws on
        /// read</b>, and the account stops loading at all. Checked against the converter that reads the
        /// column rather than against the column type, which is the lesson from last time.
        /// </para>
        /// <para>
        /// There is no faithful restore available. A password-less account genuinely has no value to
        /// put in a NOT NULL column, so the only options are to destroy those accounts, to write a
        /// credential-shaped lie, or to stop. Deleting somebody's CVs during a rollback they did not
        /// know was destructive is the worst of the three, and a lie is the one that surfaces later
        /// somewhere confusing.
        /// </para>
        /// <para>
        /// So this throws, and the operator decides in daylight. If no account has used a provider, the
        /// three statements below are safe to run by hand and the rollback is clean.
        /// </para>
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql(
                """
                IF EXISTS (SELECT 1 FROM identity.Accounts WHERE PasswordHash IS NULL)
                    THROW 50000,
                        'Cannot roll back AddExternalSignIn: accounts exist that sign in with an external provider and have no password. Restoring NOT NULL would write an empty hash, which Password.Create refuses on read, so those accounts would stop loading entirely. Decide what happens to them first -- see the remarks on this migration.',
                        1;
                """);

            migrationBuilder.DropColumn(
                name: "ExternalProvider",
                schema: "identity",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "ExternalSubject",
                schema: "identity",
                table: "Accounts");

            migrationBuilder.AlterColumn<string>(
                name: "PasswordHash",
                schema: "identity",
                table: "Accounts",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256,
                oldNullable: true);
        }
    }
}
