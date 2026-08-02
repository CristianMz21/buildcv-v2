using BuildCv.Api.Contracts;
using BuildCv.Application.Resumes;
using FluentAssertions;

namespace BuildCv.Api.Tests;

// ProjectedList exists for ONE property and this is the test for it: reading Count must not project
// anything. That is what lets ResumeDraftValidator.ForEachCapped refuse an over-cap section having
// allocated zero draft objects. With the previous `.Select(...).ToList()` in ImportResumeRequest.ToDraft,
// a ten-million-element array was copied in full — doubling an already unbounded object graph — before
// the cap was consulted at all.
public sealed class ProjectedListTests
{
    [Fact]
    public void Count_ProjectsNoElement()
    {
        var projected = 0;
        var list = new ProjectedList<int, string>([1, 2, 3], value =>
        {
            projected++;
            return value.ToString();
        });

        list.Count.Should().Be(3);
        projected.Should().Be(0, "answering Count must not build a single element");
    }

    [Fact]
    public void Indexer_ProjectsOnlyTheElementRead()
    {
        var projected = 0;
        var list = new ProjectedList<int, string>([1, 2, 3], value =>
        {
            projected++;
            return value.ToString();
        });

        list[1].Should().Be("2");
        projected.Should().Be(1);
    }

    [Fact]
    public void Enumeration_ProjectsEveryElementInOrder()
    {
        var projected = 0;
        var list = new ProjectedList<int, string>([1, 2, 3], value =>
        {
            projected++;
            return value.ToString();
        });

        list.Should().Equal("1", "2", "3");
        projected.Should().Be(3);
    }

    [Fact]
    public void OrNull_MapsANullSourceToNull() =>
        ProjectedList.OrNull<int, string>(null, value => value.ToString()).Should().BeNull();

    // Pins the USE, not just the type. Every test above would stay green if ImportResumeRequest.ToDraft
    // went back to `.Select(...).ToList()` — the amplification would return and nothing would say so.
    // Asserting the concrete type is the cheapest way to make that revert visible; a materialized copy is
    // a List<T>, never this.
    [Fact]
    public void ToDraft_ReturnsLazilyProjectedCollections()
    {
        var draft = new ImportResumeRequest(
            Contact: new ImportContactRequest(Profiles: [new ImportProfileRequest("GitHub")]),
            Experiences: [new ImportExperienceRequest()],
            Skills: [new ImportSkillRequest()]).ToDraft();

        draft.Experiences.Should().BeOfType<ProjectedList<ImportExperienceRequest?, ExperienceDraft?>>();
        draft.Skills.Should().BeOfType<ProjectedList<ImportSkillRequest?, SkillDraft?>>();
        draft.Contact!.Profiles.Should().BeOfType<ProjectedList<ImportProfileRequest?, ProfileDraft?>>();
    }
}
