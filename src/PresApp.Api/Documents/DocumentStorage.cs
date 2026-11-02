using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using PresApp.Api.Hosting;

namespace PresApp.Api.Documents;

public sealed class DocumentOptions
{
    public const string Section = "Documents";

    /// <summary>Folder for the uploaded files. Empty = C:\PresApp\documents on Windows, "documents" next to the app elsewhere.</summary>
    public string? Root { get; set; }

    public int MaxFileMb { get; set; } = 25;
}

/// <summary>A file written to the documents folder.</summary>
public sealed record SavedFile(string RelativePath, string ContentType, string Extension, long SizeBytes, string Sha256);

/// <summary>
/// Files of signed documents on the server disk. The database keeps only their description (<c>Document</c> table).
/// Files are named by the document id, never by the user's file name, and are never edited or physically deleted:
/// a removed document is moved to the "_deleted" folder.
/// </summary>
public sealed class DocumentStorage
{
    private const string IncomingFolder = "_incoming";
    private const string DeletedFolder = "_deleted";

    public DocumentStorage(IOptions<DocumentOptions> options, IHostEnvironment env)
    {
        var configured = options.Value.Root;
        Root = Path.GetFullPath(!string.IsNullOrWhiteSpace(configured) ? configured
            : OperatingSystem.IsWindows() ? @"C:\PresApp\documents"
            : Path.Combine(env.ContentRootPath, "documents"));
        MaxBytes = Math.Max(1, options.Value.MaxFileMb) * 1024L * 1024L;
    }

    public string Root { get; }
    public long MaxBytes { get; }

    /// <summary>Copies the upload to a temporary file, checks its type and size, then moves it to yyyy/MM/{id}{ext}.</summary>
    public async Task<SavedFile> SaveAsync(Stream input, Guid id, DateTime nowUtc, CancellationToken ct)
    {
        var incoming = Path.Combine(Root, IncomingFolder);
        Directory.CreateDirectory(incoming);
        var temp = Path.Combine(incoming, $"{id:N}.tmp");
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var head = new byte[16];
            var headLength = 0;
            long size = 0;
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    size += read;
                    if (size > MaxBytes) throw ApiProblem.Invalid($"The file is larger than {MaxBytes / 1024 / 1024} MB.");
                    if (headLength < head.Length)
                    {
                        var take = Math.Min(head.Length - headLength, read);
                        Array.Copy(buffer, 0, head, headLength, take);
                        headLength += take;
                    }
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            if (size == 0) throw ApiProblem.Invalid("The file is empty.");
            var kind = Detect(head.AsSpan(0, headLength))
                       ?? throw ApiProblem.Invalid("Only PDF files and photos (JPG, PNG, HEIC, WebP) can be uploaded.");

            var relative = Path.Combine(nowUtc.ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture),
                nowUtc.ToString("MM", System.Globalization.CultureInfo.InvariantCulture), $"{id}{kind.Extension}");
            var final = FullPath(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            File.Move(temp, final);
            return new SavedFile(relative.Replace('\\', '/'), kind.ContentType, kind.Extension, size, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>Absolute path of a stored file. Refuses anything that would point outside the documents folder.</summary>
    public string FullPath(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSlash = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid document path.");
        return full;
    }

    /// <summary>Removes a file that was written but whose database rows could not be saved.</summary>
    public void Discard(string relative)
    {
        var full = FullPath(relative);
        if (File.Exists(full)) File.Delete(full);
    }

    /// <summary>Moves a removed document's file to _deleted (same sub-path) and returns its new relative path.</summary>
    public string MoveToDeleted(string relative)
    {
        if (relative.StartsWith(DeletedFolder + "/", StringComparison.Ordinal)) return relative;
        var target = DeletedFolder + "/" + relative;
        var from = FullPath(relative);
        var to = FullPath(target);
        if (!File.Exists(from)) return relative;
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(from, to, overwrite: true);
        return target;
    }

    public sealed record FileKind(string ContentType, string Extension);

    /// <summary>Recognises the accepted formats from their first bytes (the browser's content type is not trusted).</summary>
    public static FileKind? Detect(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith("%PDF-"u8)) return new("application/pdf", ".pdf");
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return new("image/jpeg", ".jpg");
        if (head.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return new("image/png", ".png");
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8)) return new("image/webp", ".webp");
        if (head.Length >= 12 && head[4..8].SequenceEqual("ftyp"u8))
        {
            var brand = System.Text.Encoding.ASCII.GetString(head[8..12]);
            if (brand is "heic" or "heix" or "hevc" or "hevx" or "heim" or "heis" or "mif1" or "msf1") return new("image/heic", ".heic");
        }
        return null;
    }
}
