using System.Reflection;
using System.Xml.Linq;
using BuildCv.Application.Common.Pagination;
using BuildCv.Domain.Resumes;
using BuildCv.Infrastructure.Persistence;
using FluentAssertions;

namespace BuildCv.Api.Tests;

// Domain <- Application <- Infrastructure <- Api, asserted instead of reviewed.
//
// Until this file, the direction rested entirely on what the four .csproj files happen to say and on
// someone noticing when that changed. It lives in the Api test project because that is the only one
// whose reference closure contains all four assemblies; nothing about it is about HTTP.
//
// TWO LEVELS, because neither one alone is the claim.
//
//  - The .csproj tests read what each project DECLARES. That is the level a rogue reference is added
//    at, and it is the only level that can see one before anybody writes code against it.
//  - The reflection tests read what the compiler EMITTED. Measured, and the reason the pair exists:
//    Roslyn writes an assembly reference only for an assembly whose types are actually used, so a
//    PackageReference added to Domain and not yet consumed does not appear in
//    GetReferencedAssemblies() at all. Reflection alone would go green on it and red only later,
//    against whoever wrote the first line that used it.
//
// WHAT NEITHER LEVEL IS DOING IS THE HARD PART, and it is worth being exact about, because an
// architecture test that overstates itself is worse than none.
//
// Every inward-pointing PROJECT reference available today — Domain to Application, Application to
// Infrastructure, Infrastructure to Api — is already CIRCULAR, and MSBuild refuses one outright.
// Measured, not reasoned: adding Domain -> Application fails restore with MSB4006 before any test
// runs. So on the graph as it stands, the project-reference rules here cannot be the first thing to
// fail. What they cover is the graph as it will be — the day composition moves out of Api and the
// Api -> Infrastructure edge disappears, Infrastructure -> Api becomes buildable and nothing else in
// the repository would notice.
//
// The Domain PACKAGE rule has no such backstop and is enforceable only here. It is also the rule these
// tests were controlled against: adding a package to BuildCv.Domain builds clean and reds this file.
public sealed class ArchitectureTests
{
    private const string Prefix = "BuildCv.";

    private static readonly Assembly Domain = typeof(Resume).Assembly;
    private static readonly Assembly Application = typeof(PageRequest).Assembly;
    private static readonly Assembly Infrastructure = typeof(InMemoryResumeRepository).Assembly;
    private static readonly Assembly Api = typeof(Program).Assembly;

    [Fact]
    public void TheDomainReferencesNoOtherBuildCvAssembly()
    {
        BuildCvReferencesOf(Domain).Should().BeEmpty(
            "the Domain is the centre of the dependency graph and depends on nothing in it");
    }

    [Fact]
    public void TheApplicationReferencesOnlyTheDomain()
    {
        BuildCvReferencesOf(Application).Should().BeEquivalentTo(["BuildCv.Domain"],
            "use cases and ports belong to the Application; the adapters that satisfy them do not");
    }

    [Fact]
    public void TheInfrastructureDoesNotReferenceTheApi()
    {
        BuildCvReferencesOf(Infrastructure).Should().NotContain("BuildCv.Api",
            "an adapter that knows about the composition root above it cannot be swapped for another");
    }

    [Fact]
    public void TheApiIsTheOnlyAssemblyNothingElseReferences()
    {
        foreach (var assembly in new[] { Domain, Application, Infrastructure })
            BuildCvReferencesOf(assembly).Should().NotContain("BuildCv.Api");
    }

    // CLAUDE.md states the Domain has zero NuGet packages. This is that sentence, executed.
    //
    // "Third party" is decided by WHERE the assembly resolves from, not by its name: everything in the
    // .NET shared framework loads out of one directory, and everything a package brought in is copied
    // beside the test binary. A name test would have to guess about `Microsoft.*`, which spans both.
    [Fact]
    public void TheDomainReferencesNothingOutsideTheSharedFramework()
    {
        var sharedFramework = Path.GetDirectoryName(typeof(object).Assembly.Location);

        foreach (var reference in Domain.GetReferencedAssemblies())
        {
            var resolved = Assembly.Load(reference);
            Path.GetDirectoryName(resolved.Location).Should().Be(sharedFramework,
                $"BuildCv.Domain uses {reference.Name}, which is not part of the shared framework");
        }
    }

    // THE DECLARATION LEVEL. A reference added to a .csproj and not yet used is invisible to the four
    // tests above — measured — and this is where it is caught.
    //
    // The rules below are the ones worth defending, and no stricter. Infrastructure is asserted not to
    // name the Api rather than to name exactly one project, because it references Domain types directly
    // all through its repositories and configurations; declaring that edge would be redundant, not a
    // violation, and a test that reds on a harmless change teaches people to edit the test.
    [Fact]
    public void TheDomainDeclaresNoProjectReference()
    {
        DeclaredReferences("BuildCv.Domain", "ProjectReference").Should().BeEmpty();
    }

    [Fact]
    public void TheApplicationDeclaresOnlyTheDomain()
    {
        DeclaredReferences("BuildCv.Application", "ProjectReference")
            .Should().BeEquivalentTo(["BuildCv.Domain"]);
    }

    [Fact]
    public void TheInfrastructureDeclaresNoReferenceToTheApi()
    {
        DeclaredReferences("BuildCv.Infrastructure", "ProjectReference")
            .Should().NotContain("BuildCv.Api");
    }

    [Fact]
    public void TheDomainDeclaresNoPackageReference()
    {
        DeclaredReferences("BuildCv.Domain", "PackageReference").Should().BeEmpty(
            "a Domain with no packages is a Domain that cannot be shaped by one");
    }

    private static IEnumerable<string> BuildCvReferencesOf(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal));

    // The include is a path for a ProjectReference and a package id for a PackageReference; taking the
    // file name without its extension turns the first into the project name and leaves the second alone.
    //
    // The backslashes are replaced first because MSBuild writes them regardless of platform, while
    // Path.GetFileNameWithoutExtension follows the RUNNING OS — on Linux it treats
    // `..\BuildCv.Domain\BuildCv.Domain.csproj` as one long file name and hands back a string that
    // matches nothing. CI runs on Linux, so without this the test would fail there and pass locally on
    // Windows, which is the wrong way round for a rule about the repository's shape.
    private static IEnumerable<string> DeclaredReferences(string project, string itemName) =>
        XDocument.Load(Path.Combine(RepositoryRoot(), "src", project, project + ".csproj"))
            .Descendants(itemName)
            .Select(item => Path.GetFileNameWithoutExtension(item.Attribute("Include")!.Value.Replace('\\', '/')));

    // Walked rather than hard-coded as "../../../../..": the number of segments between the test binary
    // and the repository changes with the configuration and the target framework, and a path that is
    // merely wrong would make every assertion above vacuously true. Not finding the solution file is a
    // failure, never a skip.
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BuildCv.slnx")))
            directory = directory.Parent;

        directory.Should().NotBeNull("BuildCv.slnx has to be findable above the test binary");
        return directory!.FullName;
    }
}
