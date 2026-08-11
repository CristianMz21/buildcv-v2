namespace BuildCv.Infrastructure.Persistence.Conventions;

// Shadow names shared by every owned child table.
//
// Child rows get a surrogate int identity rather than EF's default composite (ownerFk, ordinal) key.
// The ordinal in that default is assigned by position in the collection, so reordering a resume's
// experiences rewrites the key of every row after the move — and any future row that referenced one
// would follow the wrong entry.
internal static class ChildTable
{
    public const string Key = "Id";

    public const string ResumeForeignKey = "ResumeId";
    public const string JobPostingForeignKey = "JobPostingId";
    public const string OrganizationForeignKey = "OrganizationId";
    public const string AnalysisForeignKey = "AnalysisId";
    public const string ReadabilityReportForeignKey = "ReadabilityReportId";
    public const string CandidateProfileForeignKey = "CandidateProfileId";
}
