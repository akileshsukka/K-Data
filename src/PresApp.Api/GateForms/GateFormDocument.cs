using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PresApp.Domain.GateForms;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;

namespace PresApp.Api.GateForms;

/// <summary>
/// A field of the gate form: its marker in the Word template and what is printed when it has no value. <paramref name="OnlyFor"/>:
/// the one form that has the field (null: both forms).
/// </summary>
public sealed record GateFormMarker(string Name, string Label, bool PerUnit, string Blank, GateFormKind? OnlyFor = null)
{
    public bool AppliesTo(GateFormKind kind) => OnlyFor is null || OnlyFor == kind;
}

/// <summary>
/// The Word side of the gate forms. A template is the client's form with markers (<c>{{RECORD_NO}}</c>…) where the values go;
/// everything else (fonts, colours, tables, logo) is copied as it is.
/// <list type="bullet">
/// <item>The body is repeated once per asset, each copy starting on a new page.</item>
/// <item>Headers and footers are shared by every page, so they may only hold the unit's markers (<c>{{UNIT}}</c>, <c>{{SOURCE}}</c>,
/// <c>{{DOCUMENTS_NO}}</c>).</item>
/// <item>A field of one form only (<c>{{DOCUMENTS_NO}}</c>: electrical) is refused in a template of the other form.</item>
/// <item>A marker may be split by Word into several pieces of text (spelling marks, partial formatting): it is still found,
/// and the value takes the formatting of its first character.</item>
/// <item>A field without a value prints the blank line of the client form.</item>
/// </list>
/// </summary>
public static partial class GateFormDocument
{
    public const string Unit = "UNIT";
    public const string Source = "SOURCE";
    public const string RecordNo = "RECORD_NO";
    public const string Date = "DATE";
    public const string Section = "SECTION";
    public const string WorkPack = "WORKPACK";
    public const string Description = "DESCRIPTION";
    public const string DocumentsNo = "DOCUMENTS_NO";

    public static readonly IReadOnlyList<GateFormMarker> Markers =
    [
        new(Unit, "Unit number", true, ""),
        new(Source, "Source document of the unit", true, ""),
        new(RecordNo, "Record No.", false, new string('_', 26)),
        new(Date, "Date", false, new string('_', 30)),
        new(Section, "System / circuit / section (tag or line number)", false, new string('_', 18)),
        new(WorkPack, "Work pack / P&ID rev.", false, new string('_', 18)),
        new(Description, "Tag description", false, new string('_', 18)),
        new(DocumentsNo, "Documents number of the unit", true, new string('_', 22), GateFormKind.Electrical),
    ];

    private static readonly Dictionary<string, GateFormMarker> ByName = Markers.ToDictionary(m => m.Name);

    [GeneratedRegex(@"\{\{\s*([A-Za-z_]+)\s*\}\}")]
    private static partial Regex MarkerRegex();

    private const string W14 = "http://schemas.microsoft.com/office/word/2010/wordml";

    // ------------------------------------------------------------------ check of an uploaded template

    /// <summary>What a template holds. <see cref="Errors"/> not empty = it cannot be used.</summary>
    public sealed record Inspection(List<string> Fields, List<string> Errors, List<string> Notes);

    /// <summary>Checks a template of the <paramref name="kind"/> form: a field of the other form only is an error.</summary>
    public static Inspection Inspect(byte[] content, GateFormKind kind)
    {
        var errors = new List<string>();
        var notes = new List<string>();
        var bodyMarkers = new List<string>();
        var sharedMarkers = new List<string>();
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var doc = WordprocessingDocument.Open(stream, false);
            var body = doc.MainDocumentPart?.Document?.Body ?? throw new InvalidDataException();
            Collect(body, bodyMarkers, errors);
            foreach (var root in SharedRoots(doc.MainDocumentPart!)) Collect(root, sharedMarkers, errors);
        }
        catch (Exception e) when (e is InvalidDataException or OpenXmlPackageException or FileFormatException or IOException)
        {
            return new Inspection([], ["The file is not a Word document (.docx)."], []);
        }

        foreach (var name in bodyMarkers.Concat(sharedMarkers).Distinct().Where(n => !ByName.ContainsKey(n)))
            errors.Add($"{{{{{name}}}}} is not a known field. Known fields: {string.Join(", ", Markers.Where(m => m.AppliesTo(kind)).Select(m => $"{{{{{m.Name}}}}}"))}.");
        foreach (var name in bodyMarkers.Concat(sharedMarkers).Distinct().Where(n => ByName.TryGetValue(n, out var m) && !m.AppliesTo(kind)))
            errors.Add($"{{{{{name}}}}} is a field of the {ByName[name].OnlyFor!.Value.ToString().ToLowerInvariant()} form only, not of this one.");
        foreach (var name in sharedMarkers.Distinct().Where(n => ByName.TryGetValue(n, out var m) && !m.PerUnit))
            errors.Add($"{{{{{name}}}}} is in a header or footer, which every page shares: only {string.Join(", ", Markers.Where(m => m.PerUnit && m.AppliesTo(kind)).Select(m => $"{{{{{m.Name}}}}}"))} can go there.");
        if (!bodyMarkers.Contains(Section))
            errors.Add($"{{{{{Section}}}}} is missing: it is where the tag or line number of each asset goes.");

        var all = bodyMarkers.Concat(sharedMarkers).ToHashSet();
        foreach (var m in Markers.Where(m => m.Name != Section && m.AppliesTo(kind) && !all.Contains(m.Name)))
            notes.Add($"No {{{{{m.Name}}}}}: {m.Label} is not printed by the app.");

        var fields = Markers.Select(m => m.Name).Where(all.Contains).ToList();
        return new Inspection(fields, errors, notes);
    }

    /// <summary>Markers of every paragraph under <paramref name="root"/>; text with "{{" or "}}" that is not a whole marker is reported.</summary>
    private static void Collect(OpenXmlElement root, List<string> found, List<string> errors)
    {
        foreach (var p in Paragraphs(root))
        {
            var text = string.Concat(OwnTexts(p).Select(t => t.Text));
            var matches = MarkerRegex().Matches(text);
            found.AddRange(matches.Select(m => m.Groups[1].Value.ToUpperInvariant()));
            var rest = MarkerRegex().Replace(text, "");
            if (rest.Contains("{{", StringComparison.Ordinal) || rest.Contains("}}", StringComparison.Ordinal))
                errors.Add($"A marker is incomplete or misspelt in \"{Shorten(text)}\". Write it as {{{{NAME}}}}, with the two braces on each side.");
        }
    }

    private static string Shorten(string s) => s.Length <= 80 ? s : s[..77] + "…";

    // ------------------------------------------------------------------ filling

    /// <summary>
    /// The filled document: one copy of the template body per page, in order. <paramref name="unit"/> holds the values shared by
    /// every page (unit, source, documents number); each page holds its own. A missing or empty value prints the field's blank line.
    /// </summary>
    public static byte[] Fill(byte[] template, IReadOnlyDictionary<string, string?> unit, IReadOnlyList<IReadOnlyDictionary<string, string?>> pages)
    {
        if (pages.Count == 0) throw new ArgumentException("At least one page is needed.", nameof(pages));
        using var stream = new MemoryStream();
        stream.Write(template);
        using (var doc = WordprocessingDocument.Open(stream, true))
        {
            var main = doc.MainDocumentPart!;
            var body = main.Document.Body!;
            var finalSection = body.Elements<SectionProperties>().LastOrDefault();
            var original = body.ChildElements.Where(e => e is not SectionProperties).ToList();
            foreach (var e in original) e.Remove();

            // Pages before the last lose their trailing empty paragraphs: invisible, and they could push the next page break
            // onto a blank page.
            var trimmed = original.ToList();
            while (trimmed.Count > 1 && trimmed[^1] is Paragraph p && IsEmpty(p)) trimmed.RemoveAt(trimmed.Count - 1);

            for (var i = 0; i < pages.Count; i++)
            {
                var values = new Dictionary<string, string?>(unit);
                foreach (var (k, v) in pages[i]) values[k] = v;
                var copy = (i == pages.Count - 1 ? original : trimmed).Select(e => e.CloneNode(true)).ToList();
                foreach (var e in copy)
                {
                    StripIds(e);
                    Replace(e, values);
                }
                if (i > 0) StartOnNewPage(copy);
                foreach (var e in copy)
                {
                    if (finalSection is null) body.Append(e);
                    else body.InsertBefore(e, finalSection);
                }
            }
            RenumberDrawings(body);
            main.Document.Save();

            foreach (var part in main.HeaderParts.Cast<OpenXmlPart>().Concat(main.FooterParts))
            {
                if (part.RootElement is null) continue;
                Replace(part.RootElement, unit);
                part.RootElement.Save();
            }
        }
        return stream.ToArray();
    }

    private static IEnumerable<OpenXmlElement> SharedRoots(MainDocumentPart main) =>
        main.HeaderParts.Select(h => (OpenXmlElement?)h.Header).Concat(main.FooterParts.Select(f => (OpenXmlElement?)f.Footer)).OfType<OpenXmlElement>();

    private static IEnumerable<Paragraph> Paragraphs(OpenXmlElement root) =>
        root is Paragraph p ? [p, .. root.Descendants<Paragraph>()] : root.Descendants<Paragraph>();

    /// <summary>Text of the paragraph itself, not of paragraphs nested in it (text boxes).</summary>
    private static List<Text> OwnTexts(Paragraph p) =>
        p.Descendants<Text>().Where(t => t.Ancestors<Paragraph>().First() == p).ToList();

    private static void Replace(OpenXmlElement root, IReadOnlyDictionary<string, string?> values)
    {
        foreach (var p in Paragraphs(root).ToList())
        {
            var texts = OwnTexts(p);
            if (texts.Count == 0) continue;
            var starts = new int[texts.Count];
            var full = new StringBuilder();
            for (var k = 0; k < texts.Count; k++)
            {
                starts[k] = full.Length;
                full.Append(texts[k].Text);
            }
            // From the last marker back, so the positions of the earlier ones do not move.
            foreach (var m in MarkerRegex().Matches(full.ToString()).Reverse())
            {
                var name = m.Groups[1].Value.ToUpperInvariant();
                if (!ByName.TryGetValue(name, out var marker)) continue;
                var value = values.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? OneLine(v) : marker.Blank;
                int from = m.Index, to = m.Index + m.Length;
                var first = true;
                for (var k = 0; k < texts.Count; k++)
                {
                    var s = texts[k].Text;
                    int ts = starts[k], te = ts + s.Length;
                    if (te <= from || ts >= to) continue;
                    int a = Math.Max(from, ts) - ts, b = Math.Min(to, te) - ts;
                    texts[k].Text = s[..a] + (first ? value : "") + s[b..];
                    texts[k].Space = SpaceProcessingModeValues.Preserve;
                    first = false;
                }
            }
        }
    }

    private static string OneLine(string s) => string.Join(" ", s.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static bool IsEmpty(Paragraph p) =>
        !p.Descendants<Run>().Any() && p.ParagraphProperties?.SectionProperties is null;

    private static void StartOnNewPage(List<OpenXmlElement> copy)
    {
        if (copy.FirstOrDefault() is Paragraph p)
        {
            p.ParagraphProperties ??= new ParagraphProperties();
            p.ParagraphProperties.PageBreakBefore = new PageBreakBefore();
        }
        else
        {
            copy.Insert(0, new Paragraph(new Run(new Break { Type = BreakValues.Page })));
        }
    }

    /// <summary>Paragraph ids and bookmarks would repeat on every page; Word does not need them.</summary>
    private static void StripIds(OpenXmlElement root)
    {
        foreach (var e in root.Descendants().Prepend(root))
        {
            var ids = e.GetAttributes().Where(a => a.NamespaceUri == W14 && a.LocalName is "paraId" or "textId").ToList();
            foreach (var a in ids) e.RemoveAttribute(a.LocalName, a.NamespaceUri);
        }
        foreach (var b in root.Descendants<BookmarkStart>().ToList()) b.Remove();
        foreach (var b in root.Descendants<BookmarkEnd>().ToList()) b.Remove();
    }

    /// <summary>Pictures in the body need distinct ids once the body is repeated.</summary>
    private static void RenumberDrawings(Body body)
    {
        uint id = 1000;
        foreach (var d in body.Descendants<DW.DocProperties>()) d.Id = id++;
    }
}
