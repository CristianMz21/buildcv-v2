using BuildCv.Application.Common.Services;
using BuildCv.Domain.Common.ValueObjects;
using BuildCv.Domain.Identity;
using BuildCv.Domain.Resumes;

namespace BuildCv.Application.Tests.Fakes;

// The protector as the import handler sees it: something that hands out tokens and refuses the ones it
// did not issue, refuses the ones issued to somebody else, and refuses the ones a test has expired.
//
// It models the three REFUSALS rather than the cryptography, which is the split these tests want. What
// makes a signature unforgeable is ImportEvidenceProtectorTests' business against the real key ring;
// what this file has to get right is that the handler treats each refusal the way the contract says —
// so an "unissued" token here stands in for a forged one, and the fake never has to be trusted about
// HMAC.
public sealed class FakeImportEvidenceProtector : IImportEvidenceProtector
{
    private readonly Dictionary<string, (AccountId Account, ImportSignals Signals)> _issued =
        new(StringComparer.Ordinal);

    private readonly HashSet<string> _expired = new(StringComparer.Ordinal);
    private int _minted;

    // Every token a test hands the handler that did not come from Protect is refused, so this is also
    // the forgery case: there is no shape a caller can guess that this will accept.
    public const string ForgedToken = "not-a-token-this-fake-issued";

    public int UnprotectCallCount { get; private set; }

    public string Protect(ImportSignals signals, AccountId accountId)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(accountId);

        var token = $"evidence-{++_minted}";
        _issued[token] = (accountId, signals);
        return token;
    }

    public void Expire(string token) => _expired.Add(token);

    public Result<ImportSignals> Unprotect(string token, AccountId accountId)
    {
        UnprotectCallCount++;

        if (!_issued.TryGetValue(token, out var issued) || issued.Account != accountId)
            return Result<ImportSignals>.Failure(IImportEvidenceProtector.InvalidTokenError);

        return _expired.Contains(token)
            ? Result<ImportSignals>.Failure(IImportEvidenceProtector.ExpiredTokenError)
            : Result<ImportSignals>.Success(issued.Signals);
    }
}
