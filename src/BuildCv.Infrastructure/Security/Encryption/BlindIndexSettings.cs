namespace BuildCv.Infrastructure.Security.Encryption;

// Configuration shape, nested under Encryption:
//   "BlindIndex": {
//     "ActiveKeyId": "b1",
//     "Keys": { "b1": "<base64 32 bytes>" }
//   }
// Maps onto the Azure Key Vault provider as Encryption--BlindIndex--Keys--b1.
//
// The pointer is separate from Encryption:ActiveKeyId on purpose. Writes use the active index key;
// reads must try every configured key, so rotation is: add b2 -> deploy -> backfill -> drop b1.
public sealed record BlindIndexSettings
{
    public string ActiveKeyId { get; init; } = string.Empty;

    public Dictionary<string, string> Keys { get; init; } = [];

    // Key ids are safe to surface; the secrets behind them are not.
    public override string ToString() =>
        $"{nameof(BlindIndexSettings)} {{ {nameof(ActiveKeyId)} = {ActiveKeyId}, {nameof(Keys)} = [{string.Join(", ", Keys.Keys)}] }}";
}
