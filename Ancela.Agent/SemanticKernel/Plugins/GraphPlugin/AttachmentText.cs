using System.IO.Compression;
using System.Text;

namespace Ancela.Agent.SemanticKernel.Plugins.GraphPlugin;

/// <summary>
/// Turns an attachment's bytes into text for read_attachment. A text file is read as it is, a gzip
/// file is unpacked first, and a .zip's text files are read one after another. Anything else (a PDF,
/// an image, a Word file) is refused: read as text it would reach the model as noise.
/// </summary>
public static class AttachmentText
{
    /// <summary>The most text returned for one attachment, across all its files: read_email's body cap.</summary>
    public const int TextCap = GraphClient.EmailBodyCap;

    /// <summary>The most files read out of one .zip.</summary>
    public const int ZipEntryCap = 20;

    private const string WhatItReads = "read_attachment reads text files (.txt, .csv, .xml, .json, .ics and the like), also inside a .zip or .gz, not PDFs, images or Office documents.";

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static ReadOnlySpan<byte> GzipSignature => [0x1F, 0x8B];
    // "PK": a zip starts with a file's header, or an empty one with the end of its directory.
    private static ReadOnlySpan<byte> ZipSignature => [0x50, 0x4B];

    public static AttachmentContentModel Read(string name, byte[] content)
    {
        // A .docx or .xlsx is a zip too, but of XML parts that would read as noise, so only a file
        // named .zip is opened as one.
        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && content.AsSpan().StartsWith(ZipSignature))
            return ReadZip(name, content);

        // gzip has a fixed signature, so it is recognized whatever the file is called. Only as much
        // is unpacked as the text cap needs, however large the whole file would be.
        var gzip = content.AsSpan().StartsWith(GzipSignature);
        var fileName = gzip && name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name;
        var file = ReadText(fileName, () => gzip
                ? new GZipStream(new MemoryStream(content), CompressionMode.Decompress)
                : new MemoryStream(content), TextCap)
            ?? throw new InvalidOperationException($"{name} is not a text file, so Ancela can't read it. {WhatItReads}");

        return new AttachmentContentModel { Name = name, Files = [file], NotRead = [] };
    }

    private static AttachmentContentModel ReadZip(string name, byte[] content)
    {
        using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
        var entries = zip.Entries.Where(e => !e.FullName.EndsWith('/')).ToArray();
        var files = new List<AttachmentFileModel>();
        var notRead = new List<string>();

        // One cap for the whole attachment, so a .zip of many files costs no more than one file.
        var budget = TextCap;
        foreach (var entry in entries.Take(ZipEntryCap))
        {
            if (budget == 0)
                notRead.Add($"{entry.FullName}: over the text limit");
            else if (ReadText(entry.FullName, entry.Open, budget) is { } file)
            {
                files.Add(file);
                budget -= file.Text.Length;
            }
            else
                notRead.Add($"{entry.FullName}: not a text file");
        }
        if (entries.Length > ZipEntryCap)
            notRead.Add($"{entries.Length - ZipEntryCap} more files: only the first {ZipEntryCap} are read");

        if (files.Count == 0 && entries.Length > 0)
            throw new InvalidOperationException($"None of the files in {name} is text, so Ancela can't read them. {WhatItReads}");

        return new AttachmentContentModel { Name = name, Files = [.. files], NotRead = [.. notRead] };
    }

    /// <summary>
    /// Up to <paramref name="cap"/> characters of the stream as text, or null if it isn't text. A byte
    /// order mark picks UTF-16 or UTF-32; otherwise it is UTF-8, or Latin-1 for the odd Windows export
    /// that isn't UTF-8.
    /// </summary>
    private static AttachmentFileModel? ReadText(string name, Func<Stream> open, int cap)
    {
        foreach (var encoding in new[] { StrictUtf8, Encoding.Latin1 })
        {
            var buffer = new char[cap + 1];
            int read;
            try
            {
                using var reader = new StreamReader(open(), encoding, detectEncodingFromByteOrderMarks: true);
                read = reader.ReadBlock(buffer, 0, buffer.Length);
            }
            catch (DecoderFallbackException)
            {
                continue; // not UTF-8; Latin-1 reads any byte
            }

            var length = Math.Min(read, cap);
            if (read > cap && char.IsHighSurrogate(buffer[length - 1]))
                length--; // don't split an emoji's surrogate pair
            if (!LooksLikeText(buffer.AsSpan(0, length)))
                return null;

            return new AttachmentFileModel
            {
                Name = name,
                Text = new string(buffer, 0, length).Replace("\r\n", "\n"),
                Truncated = read > cap,
            };
        }
        return null;
    }

    // Text has tabs and line breaks but no NULs and next to no other control characters; a binary
    // file read as Latin-1 is full of them.
    private static bool LooksLikeText(ReadOnlySpan<char> text)
    {
        var control = 0;
        foreach (var c in text)
        {
            if (c == '\0')
                return false;
            if (c < ' ' && c is not ('\t' or '\n' or '\r' or '\f'))
                control++;
        }
        return control * 100 <= text.Length;
    }
}
