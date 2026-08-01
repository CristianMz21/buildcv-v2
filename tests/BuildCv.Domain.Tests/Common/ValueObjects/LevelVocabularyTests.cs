using BuildCv.Domain.Common.ValueObjects;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Common.ValueObjects;

// The two ordered levels both sides of a score speak. Their numbers are a storage contract and their
// order is a comparison contract, and the two fail differently, so they are pinned separately.
public class LevelVocabularyTests
{
    // Persisted as tinyint on Resumes.Language.Level and jobs.LanguageRequirements.MinimumLevel.
    // Renumbering a member rewrites the meaning of every row already on disk.
    [Theory]
    [InlineData(LanguageProficiency.Basic, 0)]
    [InlineData(LanguageProficiency.Conversational, 1)]
    [InlineData(LanguageProficiency.Professional, 2)]
    [InlineData(LanguageProficiency.Fluent, 3)]
    [InlineData(LanguageProficiency.Native, 4)]
    public void LanguageProficiency_members_keep_their_persisted_numbers(
        LanguageProficiency proficiency, int expected) =>
        ((int)proficiency).Should().Be(expected);

    // Persisted as tinyint on Resumes.Education.Level and jobs.JobPostings.EducationLevel.
    [Theory]
    [InlineData(EducationLevel.HighSchool, 0)]
    [InlineData(EducationLevel.Associate, 1)]
    [InlineData(EducationLevel.Bachelor, 2)]
    [InlineData(EducationLevel.Master, 3)]
    [InlineData(EducationLevel.Doctorate, 4)]
    public void EducationLevel_members_keep_their_persisted_numbers(EducationLevel level, int expected) =>
        ((int)level).Should().Be(expected);

    // The other half, and the one the numbers alone do not state: the members ASCEND, which is what
    // makes `held >= required` the whole comparison PR 3 needs. Enum.GetValues returns members in
    // NUMERIC order, so asserting it against the intended semantic order is a real check — swap two
    // members' numbers and the returned sequence stops matching.
    [Fact]
    public void LanguageProficiency_ascends_from_least_to_most_proficient() =>
        Enum.GetValues<LanguageProficiency>().Should().Equal(
            LanguageProficiency.Basic,
            LanguageProficiency.Conversational,
            LanguageProficiency.Professional,
            LanguageProficiency.Fluent,
            LanguageProficiency.Native);

    [Fact]
    public void EducationLevel_ascends_from_least_to_most_advanced() =>
        Enum.GetValues<EducationLevel>().Should().Equal(
            EducationLevel.HighSchool,
            EducationLevel.Associate,
            EducationLevel.Bachelor,
            EducationLevel.Master,
            EducationLevel.Doctorate);
}
