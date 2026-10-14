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
using System.Text.Json;
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
        if (name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) return SplitSql(name, text);
        if (IsJson(name, text)) return SplitJson(name, text);
        if (name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return SplitCsv(name, text);
        return SplitText(name, text);
    }

    /// <summary>
    /// A .json item, or pasted text shaped like JSON (it starts with { or [ and
    /// ends with } or ]). Such text must parse; <see cref="Validate"/> says why not.
    /// </summary>
    public static bool IsJson(string name, string content)
    {
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return true;
        var t = content.Trim();
        return t.Length > 1 && (t[0] == '{' || t[0] == '[') && (t[^1] == '}' || t[^1] == ']');
    }

    /// <summary>Null when the content can be split as its format says; otherwise the reason, for the person who added it.</summary>
    public static string? Validate(string name, string content)
    {
        if (!IsJson(name, content)) return null;
        try
        {
            using var _ = JsonDocument.Parse(content);
            return null;
        }
        catch (JsonException e)
        {
            var reason = e.Message.Split(" LineNumber:")[0].Split(" Path:")[0].TrimEnd('.', ' ');
            return $"Invalid JSON at line {(e.LineNumber ?? 0) + 1}, column {(e.BytePositionInLine ?? 0) + 1}: {reason}.";
        }
    }

    // ───────────────────────── JSON ─────────────────────────

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // Bengali stays readable
    };

    private sealed record JsonUnit(string Path, string Text, int LineFrom, int LineTo);

    /// <summary>
    /// Along the structure: each element of a top-level array, or each top-level
    /// property, is a unit; units are packed up to MaxChunkChars, and a unit too
    /// big on its own is split by its own children. Every chunk carries its JSON
    /// path ("$[3]", "$[3]-$[5]", "$.categories") and its lines in the original.
    /// </summary>
    private static IReadOnlyList<HrChunk> SplitJson(string name, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < bytes.Length; i++) if (bytes[i] == (byte)'\n') lineStarts.Add(i + 1);
        int LineAt(long offset)
        {
            var idx = lineStarts.BinarySearch((int)offset);
            return (idx >= 0 ? idx : ~idx - 1) + 1;
        }

        using var doc = JsonDocument.Parse(bytes);
        var ranges = TopLevelRanges(bytes);
        var units = new List<JsonUnit>();
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var el in root.EnumerateArray())
            {
                var (from, to) = i < ranges.Count ? ranges[i] : (0, bytes.Length);
                units.AddRange(Units($"$[{i}]", el, null, LineAt(from), LineAt(Math.Max(to - 1, from))));
                i++;
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            var i = 0;
            foreach (var prop in root.EnumerateObject())
            {
                var (from, to) = i < ranges.Count ? ranges[i] : (0, bytes.Length);
                units.AddRange(Units(PropertyPath("$", prop.Name), prop.Value, prop.Name, LineAt(from), LineAt(Math.Max(to - 1, from))));
                i++;
            }
        }
        else
        {
            units.Add(new JsonUnit("$", JsonSerializer.Serialize(root, Pretty), 1, lineStarts.Count));
        }

        // Pack neighbouring units into chunks of at most MaxChunkChars.
        var chunks = new List<HrChunk>();
        var group = new List<JsonUnit>();
        var length = 0;
        void Flush()
        {
            if (group.Count == 0) return;
            var path = group.Count == 1 ? group[0].Path : $"{group[0].Path}-{group[^1].Path}";
            var body = string.Join("\n", group.Select(u => u.Text));
            var (from, to) = (group.Min(u => u.LineFrom), group.Max(u => u.LineTo));
            chunks.Add(new HrChunk(body, path, from, to, $"{name} {path}"));
            group.Clear();
            length = 0;
        }
        foreach (var u in units)
        {
            if (length > 0 && length + 1 + u.Text.Length > MaxChunkChars) Flush();
            group.Add(u);
            length += u.Text.Length + (length > 0 ? 1 : 0);
        }
        Flush();
        return chunks;
    }

    /// <summary>One unit, or — when it is too big — its children as units, recursively; a huge scalar is cut.</summary>
    private static IEnumerable<JsonUnit> Units(string path, JsonElement el, string? propertyName, int from, int to)
    {
        var text = JsonSerializer.Serialize(el, Pretty);
        if (propertyName is not null) text = $"{JsonSerializer.Serialize(propertyName, Pretty)}: {text}";
        if (text.Length <= MaxChunkChars)
        {
            yield return new JsonUnit(path, text, from, to);
            yield break;
        }

        if (el.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var child in el.EnumerateArray())
                foreach (var u in Units($"{path}[{i++}]", child, null, from, to)) yield return u;
        }
        else if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
                foreach (var u in Units(PropertyPath(path, prop.Name), prop.Value, prop.Name, from, to)) yield return u;
        }
        else
        {
            for (var start = 0; start < text.Length; start += MaxChunkChars)
                yield return new JsonUnit(path, text.Substring(start, Math.Min(MaxChunkChars, text.Length - start)), from, to);
        }
    }

    private static string PropertyPath(string parent, string name) =>
        Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$") ? $"{parent}.{name}" : $"{parent}['{name.Replace("'", "\\'")}']";

    /// <summary>Byte ranges of the root's direct children (array elements, or properties from name to value end).</summary>
    private static List<(long From, long To)> TopLevelRanges(byte[] bytes)
    {
        var ranges = new List<(long, long)>();
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        long start = -1;
        while (reader.Read())
        {
            var depth = reader.CurrentDepth;
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName when depth == 1:
                    start = reader.TokenStartIndex;
                    break;
                case JsonTokenType.StartObject or JsonTokenType.StartArray when depth == 1:
                    if (start < 0) start = reader.TokenStartIndex;
                    break;
                case JsonTokenType.EndObject or JsonTokenType.EndArray when depth == 1:
                    ranges.Add((start, reader.BytesConsumed));
                    start = -1;
                    break;
                case JsonTokenType.String or JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False or JsonTokenType.Null when depth == 1:
                    ranges.Add((start < 0 ? reader.TokenStartIndex : start, reader.BytesConsumed));
                    start = -1;
                    break;
            }
        }
        return ranges;
    }

    // ───────────────────────── CSV ─────────────────────────

    /// <summary>
    /// Rows grouped into chunks with the header repeated at the top of each, so
    /// every chunk can be read on its own; the SourceRef carries the data-row
    /// range ("emp-types.csv rows 1-40"). Quoted fields may span lines.
    /// </summary>
    private static IReadOnlyList<HrChunk> SplitCsv(string name, string text)
    {
        // Records, honouring quotes, each with the lines it occupies.
        var records = new List<(string Text, int From, int To)>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        int line = 1, recordStart = 1;
        foreach (var ch in text)
        {
            if (ch == '"') inQuotes = !inQuotes;
            if (ch == '\n' && !inQuotes)
            {
                if (sb.ToString().Trim().Length > 0) records.Add((sb.ToString(), recordStart, line));
                sb.Clear();
                line++;
                recordStart = line;
                continue;
            }
            if (ch == '\n') line++;
            sb.Append(ch);
        }
        if (sb.ToString().Trim().Length > 0) records.Add((sb.ToString(), recordStart, line));
        if (records.Count == 0) return Array.Empty<HrChunk>();

        var header = records[0].Text;
        if (records.Count == 1) return new[] { new HrChunk(header, null, 1, 1, $"{name} header") };

        var chunks = new List<HrChunk>();
        var rows = new List<int>();
        var length = header.Length;
        void Flush()
        {
            if (rows.Count == 0) return;
            var body = header + "\n" + string.Join("\n", rows.Select(r => records[r].Text));
            var label = $"rows {rows[0]}-{rows[^1]}";
            chunks.Add(new HrChunk(body, label, records[rows[0]].From, records[rows[^1]].To, $"{name} {label}"));
            rows.Clear();
            length = header.Length;
        }
        for (var r = 1; r < records.Count; r++)
        {
            if (rows.Count > 0 && length + 1 + records[r].Text.Length > MaxChunkChars) Flush();
            rows.Add(r);
            length += 1 + records[r].Text.Length;
        }
        Flush();
        return chunks;
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
    /// Section by section — a markdown heading, which is also how a PDF page or an
    /// XLSX sheet arrives ("## Page 2", "## Sheet Rates") — then each section split
    /// as documents are (DocumentApis.ChunkText: ~800 characters, ~100 overlap).
    /// Each piece is located in the original to recover its line range, and
    /// carries its section's heading in the SourceRef.
    /// </summary>
    private static IReadOnlyList<HrChunk> SplitText(string name, string text)
    {
        var lines = text.Split('\n');
        var sections = new List<(string? Title, int FirstLine, int LastLine)>();
        string? title = null;
        var first = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var heading = MarkdownHeading.Match(lines[i]);
            if (!heading.Success || i == first) { if (heading.Success) title = heading.Groups["title"].Value.Trim(); continue; }
            sections.Add((title, first, i - 1));
            title = heading.Groups["title"].Value.Trim();
            first = i;
        }
        sections.Add((title, first, lines.Length - 1));

        var chunks = new List<HrChunk>();
        foreach (var (section, firstLine, lastLine) in sections)
        {
            var body = string.Join("\n", lines[firstLine..(lastLine + 1)]);
            if (string.IsNullOrWhiteSpace(body)) continue;
            var cursor = 0;
            foreach (var piece in DocumentApis.ChunkText(body))
            {
                var at = body.IndexOf(piece, cursor, StringComparison.Ordinal);
                if (at < 0) at = body.IndexOf(piece, StringComparison.Ordinal);
                if (at < 0) at = cursor;
                cursor = at + 1;

                var from = firstLine + LineOf(body, at);
                var to = firstLine + LineOf(body, at + Math.Max(piece.Length - 1, 0));
                chunks.Add(new HrChunk(piece, section, from, to, SourceRef(name, section, from, to)));
            }
        }
        return chunks;
    }

    private static int LineOf(string text, int offset)
    {
        var line = 1;
        for (var i = 0; i < offset && i < text.Length; i++) if (text[i] == '\n') line++;
        return line;
    }

}
