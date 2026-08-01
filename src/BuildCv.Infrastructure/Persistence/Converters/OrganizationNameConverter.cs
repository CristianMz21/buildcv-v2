using BuildCv.Domain.Common.ValueObjects;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BuildCv.Infrastructure.Persistence.Converters;

// The PLAINTEXT mapping for OrganizationName, used only where the organization is the subject of the
// row rather than a fact about a candidate: Organizations.Name and JobPostings.CompanyName. Both are
// published on the posting itself.
//
// Deliberately not registered in ConfigureConventions. The same type is CONFIDENTIAL on every resume
// entry (Experience.Organization, Education.Institution, Certificate.Issuer, ...), where it says
// where a named person works or studied. A model-wide default would make plaintext the thing you get
// by forgetting, which is the wrong direction for the failure to fall.
internal sealed class OrganizationNameConverter() : ValueConverter<OrganizationName, string>(
    name => name.Value,
    value => OrganizationName.Create(value))
{
    public const int MaxLength = 150;
}
