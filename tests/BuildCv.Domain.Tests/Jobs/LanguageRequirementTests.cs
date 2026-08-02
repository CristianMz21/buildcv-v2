using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Exceptions;
using BuildCv.Domain.Jobs;
using FluentAssertions;

namespace BuildCv.Domain.Tests.Jobs;

public class LanguageRequirementTests
{
    [Fact]
    public void Create_trims_surrounding_whitespace() =>
        LanguageRequirement.Create("  English  ", LanguageProficiency.Professional)
            .Name.Should().Be("English");

    [Fact]
    public void Create_keeps_the_minimum_level_it_was_given() =>
        LanguageRequirement.Create("English", LanguageProficiency.Professional)
            .MinimumLevel.Should().Be(LanguageProficiency.Professional);

    // FormC, mirroring Technology.Create. "Español" typed as n + U+0303 and as U+00F1 is one word to a
    // reader and two different strings to any ordinal comparison, so without this the duplicate guard
    // on JobPosting would happily let both onto the same posting.
    [Fact]
    public void Create_normalizes_decomposed_characters_to_their_composed_form()
    {
        var decomposed = LanguageRequirement.Create("Espan\u0303ol", LanguageProficiency.Native);
        var composed = LanguageRequirement.Create("Espa\u00F1ol", LanguageProficiency.Native);

        decomposed.Name.Should().Be("Espa\u00F1ol");
        decomposed.Name.Should().HaveLength(7, "the combining tilde must have been folded into the n");
        decomposed.Should().Be(composed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_a_blank_name(string? name)
    {
        var act = () => LanguageRequirement.Create(name!, LanguageProficiency.Basic);

        act.Should().Throw<ArgumentException>();
    }

    // Trim only strips the ends, so an interior control character survives it. This is the case that
    // matters: a tab or a newline smuggled into a name would break every downstream display and would
    // make two visually identical names compare unequal.
    [Theory]
    [InlineData("Eng\tlish")]
    [InlineData("Eng\nlish")]
    [InlineData("Eng\u0000lish")]
    public void Create_rejects_control_characters(string name)
    {
        var act = () => LanguageRequirement.Create(name, LanguageProficiency.Basic);

        act.Should().Throw<InvalidJobPostingException>();
    }

    [Fact]
    public void Create_rejects_an_overlong_name()
    {
        var act = () => LanguageRequirement.Create(new string('a', 101), LanguageProficiency.Basic);

        act.Should().Throw<InvalidJobPostingException>();
    }

    // The name is stored AS TYPED, and record equality is ordinal — so two spellings that differ only
    // in case are two different values here. That is exactly why matching is OrdinalIgnoreCase at the
    // call site (JobPosting's duplicate guard, and the engine in PR 3) rather than being folded into
    // the value. Case-fold it here instead and a posting stops reading back the way it was written.
    [Fact]
    public void Create_preserves_case_which_is_why_matching_is_ordinal_ignore_case()
    {
        var upper = LanguageRequirement.Create("ENGLISH", LanguageProficiency.Professional);
        var lower = LanguageRequirement.Create("english", LanguageProficiency.Professional);

        upper.Name.Should().Be("ENGLISH");
        upper.Should().NotBe(lower);
        upper.Name.Equals(lower.Name, StringComparison.OrdinalIgnoreCase).Should().BeTrue();
    }
}
