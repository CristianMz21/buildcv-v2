namespace BuildCv.Application.Candidates;

using BuildCv.Application.Common.Abstractions;
using BuildCv.Application.Common.Repositories;
using BuildCv.Application.Resumes;
using BuildCv.Domain.Candidates;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

public sealed record AddEducationCommand(
    AccountId RequesterId,
    string Institution,
    string? Degree,
    string? FieldOfStudy,
    DateOnly Start,
    DateOnly? End,
    string? Grade,
    EducationLevel? Level,
    int? ReplacingItemId = null) : ICommand<Result<CandidateProfile>>;

public sealed class AddEducationHandler(ICandidateProfileRepository profileRepository)
    : ICommandHandler<AddEducationCommand, Result<CandidateProfile>>
{
    public Task<Result<CandidateProfile>> Handle(
        AddEducationCommand command, CancellationToken cancellationToken = default) =>
        CandidateProfileItemWrite.Execute(
            profileRepository,
            command.RequesterId,
            ResumeSection.Educations,
            command.ReplacingItemId,
            () =>
            {
                var education = new Education(
                    OrganizationName.Create(command.Institution),
                    command.Degree,
                    command.FieldOfStudy,
                    DateRange.Create(command.Start, command.End),
                    command.Grade,
                    command.Level);
                return profile => profile.AddEducation(education);
            },
            cancellationToken);
}
