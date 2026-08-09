namespace BuildCv.Application.Resumes;

/// <summary>
/// Which of a resume's ten collections a request is addressing.
/// </summary>
/// <remarks>
/// AN ADDRESSING CONCERN, NOT A DOMAIN INVARIANT, which is why it lives in Application rather than
/// beside <c>SectionType</c> in Domain. Nothing persists it: the numbers behind these members reach no
/// column, so unlike every enum in Domain they carry no append-only obligation and are free to be
/// reordered. It exists so one handler can serve ten routes without ten copies of the same ownership
/// check, and so the compiler can prove that switch covers every collection the aggregate has.
/// </remarks>
public enum ResumeSection
{
    Experiences,
    Educations,
    Skills,
    Projects,
    Certificates,
    Languages,
    Awards,
    Publications,
    Interests,
    References
}
