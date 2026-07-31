using BuildCv.Domain.Common.ValueObjects;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BuildCv.Infrastructure.Persistence.Converters;

// A slug is a public URL segment — it is published on purpose, so encrypting it would be theatre
// while breaking the one lookup it exists for.
internal sealed class SlugConverter() : ValueConverter<Slug, string>(
    slug => slug.Value,
    value => Slug.Create(value))
{
    public const int MaxLength = 100;
}
