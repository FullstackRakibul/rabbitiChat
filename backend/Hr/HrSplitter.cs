// মানব সম্পদ — splitting a `search` library item into chunks (docs/HR_MODULE_PROMPT.md §3.2).
//
// A procedure such as ProcessFinalSettelment is 1,200 lines; it cannot be sent
// whole, so it is cut into chunks that are embedded and retrieved one by one.
// For SQL the cut follows the procedure's own structure:
//
//   1. Commented-out code goes first. A whole line starting with `--` that holds
//      a SQL keyword is dead code, and so is a /* … */ block. The model must not
//      be able to quote an old formula — "Ager Formula", the `* 14` service
//      benefit — as if it ran. A comment line that does nothing but label such
//      a block ("--Ager Formula") goes with it; it would otherwise label nothing.
//      Section comments (`--//… Start`) and short notes (`--NO Service Benefit
//      for DEATH`) stay.
//   2. Then the text is split on the section comments, and inside a section on
//      blank lines, into chunks of at most 1,500 characters.
//   3. Every chunk keeps its file name, its section and its ORIGINAL line range,
//      so a citation like `ProcessFinalSettelment.sql §Service Benefit L686-711`
//      points at the lines HR sees in the file.
//
// Procedure files are reference text. Nothing here parses them into queries or
// runs them; the "keywords" are only how a commented-out line is recognised.

using System.Text;
using System.Text.RegularExpressions;
using RabbitIC.Api.MinimalApis;

namespace RabbitIC.Api.Hr;

/// <summary>One chunk: its text, the section it belongs to, and its lines in the original.</summary>
public sealed record HrChunk(string Text, string? Section, int LineFrom, int LineTo, string SourceRef);

public static class HrSplitter
{
    public const int MaxChunkChars = 1500;

    public static IReadOnlyList<HrChunk> Split(string name, string content)
    {
        var text = (content ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        return name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
            ? SplitSql(name, text)
            : SplitText(name, text);
    }

    /// <summary>"File §Section L1-9" — the one format citations and the UI parse back.</summary>
    public static string SourceRef(string name, string? section, int from, int to) =>
        string.IsNullOrWhiteSpace(section) ? $"{name} L{from}-{to}" : $"{name} §{section} L{from}-{to}";

    public static readonly Regex SourceRefPattern =
        new(@"^(?<name>.+?)(?: §(?<section>.+?))? L(?<from>\d+)-(?<to>\d+)$", RegexOptions.Compiled);

    // ───────────────────────── SQL ─────────────────────────

    /// <summary>§3.2's list, plus ELSE: a commented `--	ELSE` is as dead as the IF above it.</summary>
    private static readonly Regex SqlKeyword = new(
        @"\b(SET|SELECT|IF|ELSE|BEGIN|END|FROM|WHERE|DECLARE|INSERT|DELETE|UPDATE|EXEC)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>`--//Gratutity Calculation Start`, `--// Death Benefit start  ------`.</summary>
    private static readonly Regex SectionComment = new(@"^\s*--\s*//\s*(?<body>.*?)[\s-]*$", RegexOptions.Compiled);

    private static readonly Regex SectionEdge = new(@"\s+(?<edge>start|star|end)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TrailingCalculation = new(@"\s+(calculation|calculate)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private enum Marker { None, Start, End }

    private static IReadOnlyList<HrChunk> SplitSql(string name, string text)
    {
        var lines = text.Split('\n');
        var kept = RemoveCommentedCode(lines);

        // ---- segments: one per section, plus the stretches outside any section ----
        var segments = new List<(string? Section, List<int> Lines)>();
        string? section = null;
        var current = new List<int>();

        void Close()
        {
            if (current.Count > 0) segments.Add((section, current));
            current = new List<int>();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            if (!kept[i]) continue;
            var (marker, sectionName) = Classify(lines[i]);

            if (marker == Marker.Start)
            {
                Close();
                section = sectionName;
                current.Add(i);
            }
            else if (marker == Marker.End)
            {
                current.Add(i);
                Close();
                section = null;
            }
            else
            {
                current.Add(i);
            }
        }
        Close();

        // ---- chunks: paragraphs (blank-line separated) packed up to the limit ----
        var chunks = new List<HrChunk>();
        foreach (var (sec, segment) in segments)
        {
            foreach (var piece in Pack(Paragraphs(lines, segment), lines))
            {
                // A piece that is nothing but section comments says nothing on its own.
                if (piece.All(i => string.IsNullOrWhiteSpace(lines[i]) || Classify(lines[i]).Marker != Marker.None)) continue;
                chunks.Add(Build(name, sec, lines, piece));
            }
        }
        return chunks;
    }

    /// <summary>Which lines survive step 1. Removed lines keep their numbers; they are simply not used.</summary>
    private static bool[] RemoveCommentedCode(string[] lines)
    {
        var kept = new bool[lines.Length];
        var codeComment = new bool[lines.Length];
        var inBlock = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // /* … */ blocks, which may open and close on different lines.
            var visible = new StringBuilder();
            var pos = 0;
            while (pos < line.Length)
            {
                if (inBlock)
                {
                    var close = line.IndexOf("*/", pos, StringComparison.Ordinal);
                    if (close < 0) break;   // the block runs on past this line
                    inBlock = false;
                    pos = close + 2;
                    continue;
                }

                var open = line.IndexOf("/*", pos, StringComparison.Ordinal);
                var dash = line.IndexOf("--", pos, StringComparison.Ordinal);
                if (open < 0 || (dash >= 0 && dash < open))
                {
                    visible.Append(line, pos, line.Length - pos);
                    break;
                }
                visible.Append(line, pos, open - pos);
                inBlock = true;
                pos = open + 2;
            }

            var rest = visible.ToString();
            var hadText = !string.IsNullOrWhiteSpace(line);
            if (hadText && string.IsNullOrWhiteSpace(rest)) continue;          // the line was all block comment

            var trimmed = rest.TrimStart();
            if (trimmed.StartsWith("--", StringComparison.Ordinal)
                && !SectionComment.IsMatch(trimmed)
                && SqlKeyword.IsMatch(trimmed[2..]))
            {
                codeComment[i] = true;                                         // commented-out code
                continue;
            }

            kept[i] = true;
            if (rest.Length != line.Length) lines[i] = rest.TrimEnd();         // a block comment ended mid-line
        }

        // A comment line that only labels the dead code right below it goes too.
        for (var i = 0; i + 1 < lines.Length; i++)
        {
            if (!kept[i] || !codeComment[i + 1]) continue;
            var t = lines[i].TrimStart();
            if (t.StartsWith("--", StringComparison.Ordinal) && !SectionComment.IsMatch(t)) kept[i] = false;
        }

        return kept;
    }

    /// <summary>
    /// "--//Service Benefit Calculation Start" → (Start, "Service Benefit"). A
    /// `--//` comment that neither starts nor ends a section is a note and stays
    /// in the text.
    /// </summary>
    private static (Marker Marker, string? Name) Classify(string line)
    {
        var m = SectionComment.Match(line);
        if (!m.Success) return (Marker.None, null);

        var body = m.Groups["body"].Value.Trim();
        var edge = SectionEdge.Match(body);
        if (!edge.Success) return (Marker.None, null);

        var name = TrailingCalculation.Replace(body[..edge.Index], "").Trim(' ', '-');
        var marker = edge.Groups["edge"].Value.StartsWith("e", StringComparison.OrdinalIgnoreCase) ? Marker.End : Marker.Start;
        return (marker, name.Length == 0 ? null : name);
    }

    /// <summary>A segment's lines grouped into paragraphs at blank lines.</summary>
    private static List<List<int>> Paragraphs(string[] lines, List<int> segment)
    {
        var paragraphs = new List<List<int>>();
        var current = new List<int>();
        foreach (var i in segment)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                if (current.Count > 0) { paragraphs.Add(current); current = new List<int>(); }
                continue;
            }
            current.Add(i);
        }
        if (current.Count > 0) paragraphs.Add(current);
        return paragraphs;
    }

    /// <summary>Packs paragraphs into pieces of at most MaxChunkChars; an oversized paragraph is cut by line.</summary>
    private static List<List<int>> Pack(List<List<int>> paragraphs, string[] lines)
    {
        var pieces = new List<List<int>>();
        var current = new List<int>();
        var length = 0;

        void Flush()
        {
            if (current.Count > 0) pieces.Add(current);
            current = new List<int>();
            length = 0;
        }

        foreach (var paragraph in paragraphs)
        {
            var size = paragraph.Sum(i => lines[i].Length + 1);
            if (length > 0 && length + 1 + size > MaxChunkChars) Flush();

            if (size <= MaxChunkChars)
            {
                current.AddRange(paragraph);
                length += size + (length > 0 ? 1 : 0);
                continue;
            }

            // Too long for one chunk on its own: line by line.
            foreach (var i in paragraph)
            {
                if (length > 0 && length + lines[i].Length + 1 > MaxChunkChars) Flush();
                current.Add(i);
                length += lines[i].Length + 1;
            }
        }
        Flush();
        return pieces;
    }

    private static HrChunk Build(string name, string? section, string[] lines, List<int> piece)
    {
        // Text: the piece's lines, one blank line where a paragraph break was.
        var sb = new StringBuilder();
        for (var k = 0; k < piece.Count; k++)
        {
            if (k > 0) sb.Append('\n');
            if (k > 0 && piece[k] > piece[k - 1] + 1 && HasBlankBetween(lines, piece[k - 1], piece[k])) sb.Append('\n');
            sb.Append(lines[piece[k]]);
        }

        var body = sb.ToString();
        // A single line longer than the limit is the one case left; cut it hard.
        if (body.Length > MaxChunkChars) body = body[..MaxChunkChars];

        var from = piece.First(i => !string.IsNullOrWhiteSpace(lines[i])) + 1;
        var to = piece.Last(i => !string.IsNullOrWhiteSpace(lines[i])) + 1;
        return new HrChunk(body, section, from, to, SourceRef(name, section, from, to));
    }

    private static bool HasBlankBetween(string[] lines, int a, int b)
    {
        for (var i = a + 1; i < b; i++) if (string.IsNullOrWhiteSpace(lines[i])) return true;
        return false;
    }

    // ───────────────────────── other text ─────────────────────────

    private static readonly Regex MarkdownHeading = new(@"^\s{0,3}#{1,6}\s+(?<title>.+?)\s*#*\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Split as documents are (DocumentApis.ChunkText: ~800 characters, ~100
    /// overlap), then each piece is located in the original to recover its line
    /// range, and labelled with the nearest heading above it.
    /// </summary>
    private static IReadOnlyList<HrChunk> SplitText(string name, string text)
    {
        var chunks = new List<HrChunk>();
        var cursor = 0;
        foreach (var piece in DocumentApis.ChunkText(text))
        {
            var at = text.IndexOf(piece, cursor, StringComparison.Ordinal);
            if (at < 0) at = text.IndexOf(piece, StringComparison.Ordinal);
            if (at < 0) at = cursor;
            cursor = at + 1;

            var from = LineOf(text, at);
            var to = LineOf(text, at + Math.Max(piece.Length - 1, 0));
            var section = HeadingAbove(text, at);
            chunks.Add(new HrChunk(piece, section, from, to, SourceRef(name, section, from, to)));
        }
        return chunks;
    }

    private static int LineOf(string text, int offset)
    {
        var line = 1;
        for (var i = 0; i < offset && i < text.Length; i++) if (text[i] == '\n') line++;
        return line;
    }

    private static string? HeadingAbove(string text, int offset)
    {
        var lines = text[..Math.Min(offset + 1, text.Length)].Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var m = MarkdownHeading.Match(lines[i]);
            if (m.Success) return m.Groups["title"].Value.Trim();
        }
        return null;
    }
}
