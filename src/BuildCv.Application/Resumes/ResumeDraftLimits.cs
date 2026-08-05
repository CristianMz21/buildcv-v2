namespace BuildCv.Application.Resumes;

// How many items each section of an imported draft may carry.
//
// WHAT THESE DO AND DO NOT PROTECT — read this before treating them as a DoS control.
//
// Nothing in this repository sets Kestrel's MaxRequestBodySize (Program.cs configures Kestrel only to
// drop the Server header), so the limit actually in force is the framework default: 30,000,000 bytes,
// about 28.6 MB of JSON per request. These caps do not change that and are not a substitute for it. A
// 28 MB body of free text is accepted today by POST /resumes and every per-section route as well —
// Summary, Description, Grade and ReferenceText are unbounded strings in the Domain. PR 2 introduces
// the upload plumbing and is where a request-size limit belongs.
//
// What the caps DO bound is the part that outlives the request: the number of domain objects and
// value-object constructions one accepted body produces. Every item below becomes an owned EF entity
// that is loaded EAGERLY on every later read of that resume — the fan-out CLAUDE.md's AsSplitQuery
// note is about — so an unbounded import is not one expensive request, it is a resume that is
// expensive to read forever.
//
// THE NUMBERS ARE "A REAL CV CANNOT EXCEED THIS", NOT "THE DATABASE CANNOT STORE THIS". Each is
// roughly an order of magnitude above the largest plausible real value, so a cap firing is evidence
// of a runaway extraction or an attack rather than of an unusually accomplished candidate:
//
//   Experiences   50   a 45-year career that changed employer every single year
//   Educations    20   degrees, diplomas and bootcamps together
//   Skills       200   the most padded real CV lists a few dozen
//   Projects      50
//   Certificates  50
//   Languages     20   nobody claims twenty languages
//   Awards        50
//   Publications 200   the one section a genuine academic CV runs long in, hence the higher cap
//   Interests     25
//   References    20
//   Profiles      20   GitHub, LinkedIn, a personal site — twenty is already absurd
//   TextItems     50   per-item highlights, technologies and keywords
//
// Over-cap is reported as a field error on the COLLECTION, and the collection is then not walked at
// all: refusing to build 100,000 value objects is the work the cap exists to do. The other
// collections are still validated, so an over-cap section does not hide the errors beside it.
public static class ResumeDraftLimits
{
    public const int Experiences = 50;
    public const int Educations = 20;
    public const int Skills = 200;
    public const int Projects = 50;
    public const int Certificates = 50;
    public const int Languages = 20;
    public const int Awards = 50;
    public const int Publications = 200;
    public const int Interests = 25;
    public const int References = 20;
    public const int Profiles = 20;

    // Highlights, technologies and keywords, per owning item.
    public const int TextItems = 50;
}
