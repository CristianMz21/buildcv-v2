namespace BuildCv.Infrastructure.Documents;

/// <summary>
/// Magic-byte sniffing for the upload adapters. The declared content type picks the adapter; these
/// bytes are what decide whether the file is allowed to be treated as that format, because the
/// declaration is client-supplied and costs an attacker nothing.
/// </summary>
/// <remarks>
/// Requires a seekable stream: it peeks at the head and rewinds, so the adapter that called it can
/// still parse from the start. <see cref="DocumentTextExtractor"/> buffers non-seekable input before
/// any adapter runs.
/// </remarks>
internal static class MagicBytes
{
    public static readonly byte[] Pdf = "%PDF-"u8.ToArray();

    // The local-file-header signature every ZIP archive starts with, DOCX included — a DOCX is an OPC
    // package inside a ZIP, so this prefix only proves "some ZIP" and the OPC check must follow.
    public static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04];

    public static bool StartsWith(Stream content, ReadOnlySpan<byte> prefix)
    {
        content.Position = 0;
        // Sized to the prefix, not a fixed 8: a fixed buffer plus head[..prefix.Length] would silently
        // throw for any prefix longer than it, coupling this method's correctness to a caller
        // constraint that lives elsewhere. Prefixes here are tiny (<= 5 bytes), so the stack cost is nil.
        Span<byte> head = stackalloc byte[prefix.Length];
        var read = content.ReadAtLeast(head, prefix.Length, throwOnEndOfStream: false);
        content.Position = 0;
        return read >= prefix.Length && head.SequenceEqual(prefix);
    }
}
