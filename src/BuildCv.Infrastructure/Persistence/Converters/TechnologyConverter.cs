using BuildCv.Domain.Common.ValueObjects;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BuildCv.Infrastructure.Persistence.Converters;

// Technology is ANALYTICAL data, never encrypted: "which skills appear most often against which
// requirements" is the question this product exists to answer, and a query cannot reach through an
// envelope. It is a skill name, not a fact about a person.
//
// Registered as a model-wide conversion in BuildCvDbContext.ConfigureConventions, because every
// Technology in the model is stored the same way — Skill.Name, JobRequirement.Skill, and the
// elements of Project.Technologies.
internal sealed class TechnologyConverter() : ValueConverter<Technology, string>(
    technology => technology.Name,
    name => Technology.Create(name))
{
    public const int MaxLength = 100;
}
