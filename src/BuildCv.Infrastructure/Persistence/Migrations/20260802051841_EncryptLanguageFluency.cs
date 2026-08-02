using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildCv.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EncryptLanguageFluency : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// DROP AND ADD, NOT ALTER, AND THE DIFFERENCE IS CORRUPTION VERSUS DATA LOSS.
        ///
        /// The scaffolded operation was <c>AlterColumn nvarchar(50) -&gt; varbinary(max)</c>. SQL
        /// Server permits it — nvarchar to varbinary is an implicit conversion — and it leaves every
        /// pre-existing value in the column as its raw UTF-16 bytes. Those bytes are not an AES-GCM
        /// envelope, and <c>AesGcmFieldEncryptor.Decrypt</c> rejects them on the version byte before it
        /// ever reaches the key ring. Fluency is an eagerly-loaded owned property, so the throw is not
        /// scoped to the field: THE WHOLE RESUME STOPS LOADING, for every candidate who ever typed a
        /// fluency. Unlike the value itself, that is not recoverable by re-entering anything.
        /// <c>MigrationRollbackTests</c> runs the plaintext through the real encryptor rather than
        /// asserting this from the column type.
        ///
        /// <c>Language.Fluency</c> predates this chain — it ships in
        /// <c>20260731233800_InitialCreate</c> and is on main — so real rows are affected. Encrypting
        /// them in place is not available to a migration: the key ring lives in configuration and SQL
        /// Server has no AES-GCM, so there is no expression this file could write.
        ///
        /// So the column is dropped and re-added empty. Every stored fluency is LOST, and every resume
        /// still loads. Fluency is display-only free text that no scoring path reads — PR #16 made
        /// <c>Level</c> the scoring input and forbade the engine from reading this — so what is lost is
        /// a line of prose a candidate can retype, against a row that would otherwise be unreadable.
        ///
        /// Down() is the same trade mirrored, for the same reason pointing the other way: an envelope
        /// reinterpreted as nvarchar is mojibake at best, and the shortest envelope this encryptor can
        /// emit is 62 bytes of overhead before a single plaintext byte, past what nvarchar(50) holds.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Fluency",
                schema: "resumes",
                table: "Languages");

            migrationBuilder.AddColumn<byte[]>(
                name: "Fluency",
                schema: "resumes",
                table: "Languages",
                type: "varbinary(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Fluency",
                schema: "resumes",
                table: "Languages");

            migrationBuilder.AddColumn<string>(
                name: "Fluency",
                schema: "resumes",
                table: "Languages",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);
        }
    }
}
