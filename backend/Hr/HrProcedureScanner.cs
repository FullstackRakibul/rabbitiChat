// মানব সম্পদ · প্রসিডিউর বিশ্লেষণ — the deterministic scan (docs/HR_PROCEDURE_ANALYZER_PROMPT.md §3.2).
//
// SQL is text here, never executed: a small tokenizer that knows strings ('…',
// N'…'), comments (--, /* */, nested), bracketed names ([…]) and line numbers,
// then pattern rules over the tokens. Comments never become tokens, so nothing
// commented out can reach the facts; the commented-out code itself is reported
// as "Ignored code" through HrSplitter, the rule the HR library already uses.
//
// The facts are the ground truth the model's rules are checked against later
// (Phase A2), so this file never guesses: a value is reported as it is written,
// and what it means comes from the lookup registry, or becomes a question.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RabbitIC.Api.Hr;

// ───────────────────────── tokens ─────────────────────────

public enum SqlTokenKind { Word, Variable, String, Number, Name, Op, Punct }

/// <summary>One token and the 1-based line it starts on. Name: a [bracketed] or "quoted" identifier, unwrapped.</summary>
public sealed record SqlToken(SqlTokenKind Kind, string Text, int Line)
{
    public bool IsWord(string word) => Kind == SqlTokenKind.Word && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
    public bool IsPunct(string p) => Kind == SqlTokenKind.Punct && Text == p;

    /// <summary>A word or a bracketed name — anything that can name an object or column.</summary>
    public bool IsIdentifier => Kind is SqlTokenKind.Word or SqlTokenKind.Name;

    public bool IsComparison => Kind == SqlTokenKind.Op && Text is "=" or "<>" or "!=" or "<" or ">" or "<=" or ">=" or "!<" or "!>";

    public bool IsLiteral => Kind is SqlTokenKind.String or SqlTokenKind.Number;
}

public static class SqlTokenizer
{
    public static List<SqlToken> Tokenize(string text)
    {
        var tokens = new List<SqlToken>();
        var line = 1;
        var i = 0;
        var n = text.Length;

        while (i < n)
        {
            var c = text[i];
            if (c == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }

            // -- comment to the end of the line
            if (c == '-' && i + 1 < n && text[i + 1] == '-')
            {
                while (i < n && text[i] != '\n') i++;
                continue;
            }

            // /* … */, nested as T-SQL allows
            if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                var depth = 1;
                i += 2;
                while (i < n && depth > 0)
                {
                    if (text[i] == '\n') line++;
                    if (text[i] == '/' && i + 1 < n && text[i + 1] == '*') { depth++; i += 2; continue; }
                    if (text[i] == '*' && i + 1 < n && text[i + 1] == '/') { depth--; i += 2; continue; }
                    i++;
                }
                continue;
            }

            // 'string', N'string'; '' inside is one quote
            if (c == '\'' || ((c == 'N' || c == 'n') && i + 1 < n && text[i + 1] == '\''))
            {
                var start = line;
                i += c == '\'' ? 1 : 2;
                var sb = new StringBuilder();
                while (i < n)
                {
                    if (text[i] == '\'')
                    {
                        if (i + 1 < n && text[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                        i++;
                        break;
                    }
                    if (text[i] == '\n') line++;
                    sb.Append(text[i++]);
                }
                tokens.Add(new SqlToken(SqlTokenKind.String, sb.ToString(), start));
                continue;
            }

            // [name] with ]] inside, and "name"
            if (c == '[' || c == '"')
            {
                var close = c == '[' ? ']' : '"';
                var start = line;
                i++;
                var sb = new StringBuilder();
                while (i < n)
                {
                    if (text[i] == close)
                    {
                        if (i + 1 < n && text[i + 1] == close) { sb.Append(close); i += 2; continue; }
                        i++;
                        break;
                    }
                    if (text[i] == '\n') line++;
                    sb.Append(text[i++]);
                }
                tokens.Add(new SqlToken(SqlTokenKind.Name, sb.ToString(), start));
                continue;
            }

            // @variable, @@global
            if (c == '@')
            {
                var start = i;
                i++;
                while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '@' or '#' or '$')) i++;
                tokens.Add(new SqlToken(SqlTokenKind.Variable, text[start..i], line));
                continue;
            }

            // numbers: 12, 1.33, .5
            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(text[i + 1])))
            {
                var start = i;
                while (i < n && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                tokens.Add(new SqlToken(SqlTokenKind.Number, text[start..i], line));
                continue;
            }

            // words: keywords, names, #temp
            if (char.IsLetter(c) || c is '_' or '#')
            {
                var start = i;
                while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '#' or '$' or '@')) i++;
                tokens.Add(new SqlToken(SqlTokenKind.Word, text[start..i], line));
                continue;
            }

            // operators
            if (i + 1 < n)
            {
                var two = text.Substring(i, 2);
                if (two is "<=" or ">=" or "<>" or "!=" or "!<" or "!>")
                {
                    tokens.Add(new SqlToken(SqlTokenKind.Op, two, line));
                    i += 2;
                    continue;
                }
            }
            if (c is '=' or '<' or '>' or '+' or '-' or '*' or '/' or '%' or '&' or '|' or '^' or '~')
            {
                tokens.Add(new SqlToken(SqlTokenKind.Op, c.ToString(), line));
                i++;
                continue;
            }

            tokens.Add(new SqlToken(SqlTokenKind.Punct, c.ToString(), line));
            i++;
        }
        return tokens;
    }
}

// ───────────────────────── facts ─────────────────────────

public sealed record HrProcParameter(string Name, string Type);
public sealed record HrProcSection(string Name, int LineFrom, int LineTo);
public sealed record HrProcTable(string Table, List<int> Lines);
public sealed record HrProcWrite(string Table, string Operation, List<int> Lines);
public sealed record HrProcCall(string Name, string Kind, List<int> Lines);

/// <param name="Via">How the code names it: "@UnitId", "LeaveID".</param>
public sealed record HrProcCondition(string Column, string Operator, List<string> Values, List<int> Lines, List<string> Sections, List<string> Via);
public sealed record HrProcDate(string Date, List<string> Operators, List<int> Lines, List<string> Sections);

/// <param name="Business">In a condition (IF, WHERE, WHEN, or compared): a rule that depends on the day it runs.</param>
public sealed record HrProcRunDate(string Function, int Line, string? Section, bool Business, string Snippet);
public sealed record HrProcEmployee(string Value, string Form, List<int> Lines, List<string> Sections);
public sealed record HrProcThreshold(string Variable, string Operator, string Value, List<int> Lines, List<string> Sections);
public sealed record HrProcRange(int LineFrom, int LineTo);

/// <param name="Value">The literal compared with; null when the column is compared with a variable ("each").</param>
public sealed record HrProcFilterTerm(string Column, string Operator, List<string>? Values);
public sealed record HrProcFilter(string Table, List<HrProcFilterTerm> Terms, string? LatestBy, List<int> Lines);

public sealed record HrProcFacts(
    string ObjectName, string Kind,
    List<HrProcParameter> Parameters,
    List<HrProcSection> Sections,
    List<HrProcTable> TablesRead,
    List<HrProcWrite> TablesWritten,
    List<HrProcCall> Calls,
    List<HrProcCondition> Conditions,
    List<HrProcDate> Dates,
    List<HrProcRunDate> RunDate,
    List<HrProcEmployee> Employees,
    List<HrProcThreshold> Thresholds,
    List<HrProcRange> Ignored,
    List<HrProcFilter> Filters,
    Dictionary<string, string> VariableColumns);

/// <summary>The analyzer's settings: the parameter columns it knows (A-F6), and aliases (@SalaryStractureId → CalculationFormulaId).</summary>
public sealed record HrScanSettings(IReadOnlyList<string> Columns, IReadOnlyDictionary<string, string> Aliases)
{
    public static readonly string[] DefaultColumns =
        { "EmpTypeID", "EmpCategoryID", "EmpStatusID", "UnitId", "PaymentModeId", "CalculationFormulaId", "LeaveId", "AttStatus", "ModeId", "CurrencyId" };

    public const string DefaultAliases = "SalaryStractureId=CalculationFormulaId";

    public static HrScanSettings From(string? columns, string? aliases) => new(
        (string.IsNullOrWhiteSpace(columns) ? DefaultColumns : columns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList(),
        (string.IsNullOrWhiteSpace(aliases) ? DefaultAliases : aliases)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2, StringSplitOptions.TrimEntries)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase));

    /// <summary>The column a name stands for, in its canonical spelling, or null: "UnitID" → "UnitId", "SalaryStractureId" → "CalculationFormulaId".</summary>
    public string? Column(string name)
    {
        var bare = name.TrimStart('@');
        var known = Columns.FirstOrDefault(c => string.Equals(c, bare, StringComparison.OrdinalIgnoreCase));
        if (known is not null) return known;
        return Aliases.TryGetValue(bare, out var alias) ? Columns.FirstOrDefault(c => string.Equals(c, alias, StringComparison.OrdinalIgnoreCase)) ?? alias : null;
    }
}

public static class HrProcedureScanner
{
    private static readonly Regex IsoDate = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);

    /// <summary>The words that start a statement: a WHERE clause, a SELECT list or an IF condition ends before them.</summary>
    private static readonly HashSet<string> StatementWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "SET", "IF", "ELSE", "BEGIN", "END", "INSERT", "UPDATE", "DELETE", "MERGE", "EXEC", "EXECUTE",
        "DECLARE", "RETURN", "WHILE", "GOTO", "PRINT", "TRUNCATE", "WITH", "UNION", "ORDER", "GROUP", "HAVING", "OPTION",
    };

    /// <summary>The words a written table follows: "INSERT INTO dbo.T(" names a table, not a call. (FROM dbo.F( is a table-valued function: a call.)</summary>
    private static readonly HashSet<string> TableWords = new(StringComparer.OrdinalIgnoreCase) { "INTO", "UPDATE", "TABLE", "MERGE" };

    private static readonly HashSet<string> RunDateFunctions = new(StringComparer.OrdinalIgnoreCase) { "GETDATE", "SYSDATETIME", "CURRENT_TIMESTAMP" };

    /// <summary>The variables that hold an employee: compared with a literal, they are a hard-coded employee.</summary>
    private static readonly HashSet<string> EmployeeVariables = new(StringComparer.OrdinalIgnoreCase) { "@EmpId", "@EmpID", "@EmployeeId" };

    /// <summary>The object a file creates or alters, and its kind; null when it names none.</summary>
    public static (string Name, string Kind)? Header(IReadOnlyList<SqlToken> t)
    {
        for (var i = 0; i + 1 < t.Count; i++)
        {
            if (!(t[i].IsWord("CREATE") || t[i].IsWord("ALTER"))) continue;
            var j = i + 1;
            if (t[j].IsWord("OR") && j + 1 < t.Count && t[j + 1].IsWord("ALTER")) j += 2;
            if (j >= t.Count) break;
            var kind = t[j].IsWord("PROCEDURE") || t[j].IsWord("PROC") ? "procedure" : t[j].IsWord("FUNCTION") ? "function" : null;
            if (kind is null) continue;
            var (name, _) = QualifiedName(t, j + 1);
            if (name is not null) return (name, kind);
        }
        return null;
    }

    public static HrProcFacts Scan(string sql, HrScanSettings settings)
    {
        var text = (sql ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = text.Split('\n');
        var t = SqlTokenizer.Tokenize(text);
        var header = Header(t);

        // ---- sections ----
        var sections = HrSplitter.SqlSections(text).Select(s => new HrProcSection(s.Name, s.From, s.To)).ToList();
        if (sections.Count == 0) sections = TopLevelIfBlocks(t, lines.Length);
        string Section(int line) => sections.LastOrDefault(s => s.LineFrom <= line && line <= s.LineTo)?.Name ?? "";

        // ---- variables and the columns they hold ----
        var varColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 2 < t.Count; i++)
        {
            if (t[i].Kind != SqlTokenKind.Variable || t[i + 1].Text != "=" || !t[i + 2].IsIdentifier) continue;
            // An assignment in a SELECT list: SELECT @X = [alias.]Column, …
            var prev = i > 0 ? t[i - 1] : null;
            var inSelect = prev is not null && (prev.IsWord("SELECT") || prev.IsPunct(",") || (prev.Kind == SqlTokenKind.Number && i > 1 && t[i - 2].IsWord("TOP")));
            if (!inSelect) continue;
            var (col, end) = ColumnRef(t, i + 2);
            if (col is null) continue;
            var after = end + 1 < t.Count ? t[end + 1] : null;
            if (after is not null && (after.Kind == SqlTokenKind.Op || after.IsPunct("(") || after.IsPunct("."))) continue;
            if (settings.Column(col) is { } known) varColumns[t[i].Text] = known;
        }
        string? ColumnOfVariable(string variable) =>
            varColumns.TryGetValue(variable, out var c) ? c : settings.Column(variable);

        var parameters = Parameters(t);
        var conditions = new List<(string Column, string Op, List<string> Values, int Line, string Via)>();
        var thresholds = new List<(string Var, string Op, string Value, int Line)>();
        var employees = new List<(string Value, string Form, int Line)>();
        var dates = new List<(string Date, string Op, int Line)>();
        var reads = new List<(string Table, int Line)>();
        var writes = new List<(string Table, string Op, int Line)>();
        var calls = new List<(string Name, string Kind, int Line)>();
        var runDate = new List<HrProcRunDate>();
        var filters = new List<HrProcFilter>();
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < t.Count; i++)
        {
            var tok = t[i];

            // ---- comparisons: operand, operator, literal(s) ----
            // Only where a condition can start: "SET @x = 0" is an assignment, not a rule.
            if ((tok.Kind == SqlTokenKind.Variable || (tok.IsIdentifier && !StatementWords.Contains(tok.Text))) && i > 0 && StartsCondition(t[i - 1]))
            {
                var (operand, end) = tok.Kind == SqlTokenKind.Variable ? (tok.Text, i) : ColumnRef(t, i);
                // "(@NetPayableAmount)>50000": the parentheses around a lone operand.
                if (tok.Kind == SqlTokenKind.Variable && i > 0 && t[i - 1].IsPunct("(") && end + 1 < t.Count && t[end + 1].IsPunct(")")) end++;
                if (operand is not null && Comparison(t, end + 1) is { } cmp && (i == 0 || !t[i - 1].IsPunct(".")))
                {
                    var column = tok.Kind == SqlTokenKind.Variable ? ColumnOfVariable(operand) : settings.Column(operand);
                    if (column is not null)
                        conditions.Add((column, cmp.Op, cmp.Values, tok.Line, operand));
                    else if (tok.Kind == SqlTokenKind.Variable && EmployeeVariables.Contains(operand) && cmp.Numeric)
                        employees.AddRange(cmp.Values.Select(v => (v, $"{operand} {cmp.Op} {v}", tok.Line)));
                    else if (tok.Kind == SqlTokenKind.Variable && cmp.Numeric && !EmployeeVariables.Contains(operand))
                        thresholds.Add((operand, cmp.Op, string.Join(" AND ", cmp.Values), tok.Line));
                    else if (tok.Kind != SqlTokenKind.Variable && string.Equals(operand, "EmpCode", StringComparison.OrdinalIgnoreCase) && !cmp.Numeric)
                        employees.AddRange(cmp.Values.Select(v => (v, $"EmpCode='{v}'", tok.Line)));
                }
            }

            // ---- hard-coded employees through functions: dbo.REmpId('…'), dbo.ReturnEmpCode(@EmpId) IN ('…') ----
            if (tok.IsIdentifier && i + 1 < t.Count && t[i + 1].IsPunct("("))
            {
                if (string.Equals(tok.Text, "REmpId", StringComparison.OrdinalIgnoreCase) && i + 3 < t.Count && t[i + 2].Kind == SqlTokenKind.String && t[i + 3].IsPunct(")"))
                    employees.Add((t[i + 2].Text, $"dbo.REmpId('{t[i + 2].Text}')", tok.Line));
                if (string.Equals(tok.Text, "ReturnEmpCode", StringComparison.OrdinalIgnoreCase))
                {
                    var close = Matching(t, i + 1);
                    if (close > 0 && Comparison(t, close + 1) is { } cmp)
                        employees.AddRange(cmp.Values.Select(v => (v, $"ReturnEmpCode(@EmpId) {cmp.Op} ('{v}')", tok.Line)));
                }
            }

            // ---- date cut-offs: a 'yyyy-mm-dd' literal next to a comparison ----
            if (tok.Kind == SqlTokenKind.String && IsoDate.IsMatch(tok.Text))
            {
                var op = i > 0 && t[i - 1].IsComparison ? t[i - 1].Text : i + 1 < t.Count && t[i + 1].IsComparison ? t[i + 1].Text : null;
                if (op is not null) dates.Add((tok.Text, op == "!=" ? "<>" : op, tok.Line));
            }

            // ---- run-date dependence ----
            if (tok.Kind == SqlTokenKind.Word && RunDateFunctions.Contains(tok.Text) && (tok.IsWord("CURRENT_TIMESTAMP") || (i + 1 < t.Count && t[i + 1].IsPunct("("))))
            {
                var line = tok.Line;
                runDate.Add(new HrProcRunDate($"{tok.Text.ToUpperInvariant()}{(tok.IsWord("CURRENT_TIMESTAMP") ? "" : "()")}", line, Section(line),
                    IsBusiness(t, i), lines[line - 1].Trim()));
            }

            // ---- tables read: FROM / JOIN / APPLY ----
            if ((tok.IsWord("FROM") || tok.IsWord("JOIN") || tok.IsWord("APPLY")) && i + 1 < t.Count)
            {
                if (tok.IsWord("FROM") && i > 0 && t[i - 1].IsWord("DELETE")) continue;    // a write, below
                var (name, end) = QualifiedName(t, i + 1);
                if (name is null || name.StartsWith('#')) continue;
                if (end + 1 < t.Count && t[end + 1].IsPunct("(")) continue;                  // a table-valued function: a call, below
                reads.Add((name, tok.Line));
                string? alias = null;
                if (end + 1 < t.Count && t[end + 1].IsIdentifier && !StatementWords.Contains(t[end + 1].Text) && !IsClauseWord(t[end + 1]))
                    alias = t[end + 1].Text;
                else if (end + 2 < t.Count && t[end + 1].IsWord("AS") && t[end + 2].IsIdentifier)
                    alias = t[end + 2].Text;
                if (alias is not null) aliases[alias] = name;
                if (Filter(t, i, end, name, alias, tok.IsWord("FROM"), settings) is { } filter) filters.Add(filter);
            }

            // ---- tables written ----
            if (tok.IsWord("INSERT") || tok.IsWord("UPDATE") || tok.IsWord("DELETE") || tok.IsWord("MERGE"))
            {
                var j = i + 1;
                if (j < t.Count && (t[j].IsWord("INTO") || (tok.IsWord("DELETE") && t[j].IsWord("FROM")))) j++;
                if (tok.IsWord("UPDATE") && j < t.Count && t[j].IsWord("STATISTICS")) continue;
                var (name, _) = QualifiedName(t, j);
                if (name is null || name.StartsWith('#')) continue;
                writes.Add((name, tok.Text.ToUpperInvariant(), tok.Line));
            }

            // ---- calls: EXEC name; [dbo].[Name]( and dbo.Name( ----
            if ((tok.IsWord("EXEC") || tok.IsWord("EXECUTE")) && i + 1 < t.Count)
            {
                var j = i + 1;
                if (t[j].Kind == SqlTokenKind.Variable && j + 1 < t.Count && t[j + 1].Text == "=") j += 2;   // EXEC @r = proc
                var (name, _) = QualifiedName(t, j);
                if (name is not null) calls.Add((name, "procedure", tok.Line));
            }
            // Not a table's column list: "INSERT INTO dbo.Table(" names a table.
            if (tok.IsIdentifier && i + 3 < t.Count && t[i + 1].IsPunct(".") && t[i + 2].IsIdentifier && t[i + 3].IsPunct("(")
                && (i == 0 || (!t[i - 1].IsPunct(".") && !TableWords.Contains(t[i - 1].Text))))
                calls.Add((t[i + 2].Text, "function", tok.Line));
        }

        // An UPDATE through an alias ("UPDATE A … FROM Table A") names the table.
        writes = writes.Select(w => aliases.TryGetValue(w.Table, out var table) ? (table, w.Op, w.Line) : w).ToList();

        static List<int> Lines(IEnumerable<int> ls) => ls.Distinct().OrderBy(l => l).ToList();
        List<string> Sections(IEnumerable<int> ls) => ls.Select(Section).Distinct().ToList();

        return new HrProcFacts(
            header?.Name ?? "", header?.Kind ?? "",
            parameters,
            sections,
            reads.GroupBy(r => r.Table, StringComparer.OrdinalIgnoreCase).Select(g => new HrProcTable(g.First().Table, Lines(g.Select(x => x.Line)))).OrderBy(x => x.Table, StringComparer.OrdinalIgnoreCase).ToList(),
            writes.GroupBy(w => (w.Table.ToLowerInvariant(), w.Op)).Select(g => new HrProcWrite(g.First().Table, g.Key.Op, g.Select(x => x.Line).OrderBy(l => l).ToList())).ToList(),
            calls.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(g => new HrProcCall(g.First().Name, g.First().Kind, Lines(g.Select(x => x.Line)))).ToList(),
            conditions.GroupBy(c => (c.Column, c.Op, string.Join("\u0001", c.Values)))
                .Select(g => new HrProcCondition(g.Key.Column, g.Key.Op, g.First().Values, Lines(g.Select(x => x.Line)), Sections(g.Select(x => x.Line)), g.Select(x => x.Via).Distinct().ToList()))
                .OrderBy(c => c.Column).ThenBy(c => c.Lines[0]).ToList(),
            dates.GroupBy(d => d.Date).Select(g => new HrProcDate(g.Key, g.Select(x => x.Op).Distinct().ToList(), Lines(g.Select(x => x.Line)), Sections(g.Select(x => x.Line))))
                .OrderBy(d => d.Date, StringComparer.Ordinal).ToList(),
            runDate,
            employees.GroupBy(e => e.Value).Select(g => new HrProcEmployee(g.Key, g.First().Form, Lines(g.Select(x => x.Line)), Sections(g.Select(x => x.Line)))).ToList(),
            thresholds.GroupBy(x => (x.Var.ToLowerInvariant(), x.Op, x.Value))
                .Select(g => new HrProcThreshold(g.First().Var, g.Key.Op, g.Key.Value, Lines(g.Select(x => x.Line)), Sections(g.Select(x => x.Line)))).ToList(),
            HrSplitter.IgnoredCode(text).Select(r => new HrProcRange(r.From, r.To)).ToList(),
            filters.GroupBy(f => $"{f.Table.ToLowerInvariant()}|{string.Join(";", f.Terms.Select(x => $"{x.Column}{x.Operator}{string.Join(",", x.Values ?? new())}"))}|{f.LatestBy}")
                .Select(g => g.First() with { Lines = Lines(g.SelectMany(x => x.Lines)) }).ToList(),
            varColumns);
    }

    // ───────────────────────── helpers ─────────────────────────

    private sealed record Cmp(string Op, List<string> Values, bool Numeric);

    /// <summary>
    /// A comparison starting at <paramref name="j"/> with literal values only:
    /// "= 3", "&lt;&gt; 'A'", "IN (1, 2)", "NOT IN ('P','LT')", "BETWEEN 3 AND 9". Null otherwise.
    /// </summary>
    private static Cmp? Comparison(IReadOnlyList<SqlToken> t, int j)
    {
        if (j >= t.Count) return null;
        if (t[j].IsComparison)
        {
            var (v, ok, numeric) = Literal(t, j + 1);
            return ok ? new Cmp(t[j].Text == "!=" ? "<>" : t[j].Text, new List<string> { v! }, numeric) : null;
        }
        var not = t[j].IsWord("NOT");
        var k = not ? j + 1 : j;
        if (k < t.Count && t[k].IsWord("IN") && k + 1 < t.Count && t[k + 1].IsPunct("("))
        {
            var values = new List<string>();
            var numeric = true;
            var m = k + 2;
            while (m < t.Count)
            {
                var (v, ok, isNumber) = Literal(t, m);
                if (!ok) return null;                                   // IN (SELECT …), IN (@x): not a literal list
                values.Add(v!);
                numeric &= isNumber;
                m += t[m].Text == "-" ? 2 : 1;
                if (m < t.Count && t[m].IsPunct(",")) { m++; continue; }
                if (m < t.Count && t[m].IsPunct(")")) return new Cmp(not ? "NOT IN" : "IN", values, numeric);
                return null;
            }
            return null;
        }
        if (k < t.Count && t[k].IsWord("BETWEEN"))
        {
            var (a, okA, numA) = Literal(t, k + 1);
            var andAt = k + 2 + (t.Count > k + 1 && t[k + 1].Text == "-" ? 1 : 0);
            if (!okA || andAt >= t.Count || !t[andAt].IsWord("AND")) return null;
            var (b, okB, numB) = Literal(t, andAt + 1);
            return okB ? new Cmp(not ? "NOT BETWEEN" : "BETWEEN", new List<string> { a!, b! }, numA && numB) : null;
        }
        return null;
    }

    /// <summary>A literal at <paramref name="j"/>: a number (with its sign) or a string.</summary>
    private static (string? Value, bool Ok, bool Numeric) Literal(IReadOnlyList<SqlToken> t, int j)
    {
        if (j >= t.Count) return (null, false, false);
        if (t[j].Kind == SqlTokenKind.String) return (t[j].Text, true, false);
        if (t[j].Kind == SqlTokenKind.Number) return (Number(t[j].Text), true, true);
        if (t[j].Text == "-" && j + 1 < t.Count && t[j + 1].Kind == SqlTokenKind.Number) return ("-" + Number(t[j + 1].Text), true, true);
        return (null, false, false);
    }

    /// <summary>"01" → "1", "2.50" → "2.5": numbers as their value, so 01 and 1 are one value.</summary>
    private static string Number(string text) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? d.ToString("0.############", CultureInfo.InvariantCulture) : text;

    /// <summary>A possibly qualified name at <paramref name="j"/> — [dbo].[X], dbo.X, X — and the index of its last part.</summary>
    private static (string? Name, int End) QualifiedName(IReadOnlyList<SqlToken> t, int j)
    {
        if (j >= t.Count || !t[j].IsIdentifier || (t[j].Kind == SqlTokenKind.Word && StatementWords.Contains(t[j].Text))) return (null, j);
        var end = j;
        while (end + 2 < t.Count && t[end + 1].IsPunct(".") && t[end + 2].IsIdentifier) end += 2;
        return (t[end].Text, end);
    }

    /// <summary>A column reference at <paramref name="j"/>: Column or alias.Column; the column and the index of its last token.</summary>
    private static (string? Column, int End) ColumnRef(IReadOnlyList<SqlToken> t, int j)
    {
        if (j >= t.Count || !t[j].IsIdentifier) return (null, j);
        if (j + 2 < t.Count && t[j + 1].IsPunct(".") && t[j + 2].IsIdentifier) return (t[j + 2].Text, j + 2);
        return (t[j].Text, j);
    }

    /// <summary>The tokens a condition follows: IF, WHERE, AND, OR, WHEN, ON, NOT, WHILE, HAVING, CASE, or "(".</summary>
    private static bool StartsCondition(SqlToken prev) =>
        prev.IsPunct("(") || (prev.Kind == SqlTokenKind.Word && prev.Text.ToUpperInvariant() is "IF" or "WHERE" or "AND" or "OR" or "WHEN"
            or "ON" or "NOT" or "WHILE" or "HAVING" or "CASE");

    private static bool IsClauseWord(SqlToken tok) =>
        tok.Kind == SqlTokenKind.Word && tok.Text.ToUpperInvariant() is "WHERE" or "INNER" or "LEFT" or "RIGHT" or "FULL" or "CROSS" or "OUTER"
            or "JOIN" or "ON" or "AS" or "WITH" or "GROUP" or "ORDER" or "HAVING" or "UNION" or "AND" or "OR" or "APPLY";

    /// <summary>The index of the ")" that closes the "(" at <paramref name="open"/>, or -1.</summary>
    private static int Matching(IReadOnlyList<SqlToken> t, int open)
    {
        var depth = 0;
        for (var k = open; k < t.Count; k++)
        {
            if (t[k].IsPunct("(")) depth++;
            else if (t[k].IsPunct(")") && --depth == 0) return k;
        }
        return -1;
    }

    /// <summary>
    /// Whether a GETDATE() is a business rule: it sits in a condition (its clause
    /// starts with IF, WHERE, AND, OR, WHEN, ON, WHILE, HAVING) or is compared
    /// directly. "DECLARE @t = GETDATE()" and "VALUES (…, GETDATE())" are timestamps.
    /// </summary>
    private static bool IsBusiness(IReadOnlyList<SqlToken> t, int i)
    {
        // Compared directly: GETDATE() >= …, CAST(GETDATE() AS date) <= …, … < GETDATE().
        var after = i + 1 < t.Count && t[i + 1].IsPunct("(") ? Matching(t, i + 1) : i;
        for (var k = after + 1; k < Math.Min(t.Count, after + 5); k++)
        {
            if (t[k].IsComparison) return true;
            if (!(t[k].IsPunct(")") || t[k].IsWord("AS") || t[k].IsIdentifier)) break;
        }
        for (var k = i - 1; k >= Math.Max(0, i - 4); k--)
        {
            if (t[k].IsComparison) return true;
            if (!(t[k].IsPunct("(") || t[k].IsWord("CAST") || t[k].IsWord("CONVERT"))) break;
        }
        // Otherwise by the clause it is in.
        for (var k = i - 1; k >= 0; k--)
        {
            var w = t[k];
            if (w.Kind != SqlTokenKind.Word && !w.IsPunct(",") && !w.IsPunct(";")) continue;
            if (w.IsPunct(",") || w.IsPunct(";")) return false;
            switch (w.Text.ToUpperInvariant())
            {
                case "IF": case "WHERE": case "AND": case "OR": case "WHEN": case "ON": case "WHILE": case "HAVING": return true;
                case "SET": case "VALUES": case "DECLARE": case "SELECT": case "INSERT": case "UPDATE": case "RETURN":
                case "BEGIN": case "END": case "THEN": case "ELSE": return false;
            }
        }
        return false;
    }

    /// <summary>
    /// What a read filters a table on (§3.4's setup list): from the table to the
    /// end of its statement, each known column compared with a literal or a
    /// variable, and "latest by X" for SELECT TOP 1 … ORDER BY X DESC. A term
    /// with an alias counts for the table of that alias only; a bare column, for
    /// the FROM table only (a joined table gets only what names it).
    /// </summary>
    private static HrProcFilter? Filter(IReadOnlyList<SqlToken> t, int at, int nameEnd, string table, string? alias, bool isFrom, HrScanSettings settings)
    {
        var depth = 0;
        var terms = new List<HrProcFilterTerm>();
        string? orderBy = null;
        for (var k = nameEnd + 1; k < t.Count; k++)
        {
            var tok = t[k];
            if (tok.IsPunct("(")) { depth++; continue; }
            if (tok.IsPunct(")")) { if (--depth < 0) break; continue; }
            if (depth > 0) continue;
            if (tok.IsPunct(";")) break;
            if (tok.Kind == SqlTokenKind.Word && StatementWords.Contains(tok.Text))
            {
                if (tok.IsWord("ORDER") && k + 2 < t.Count && t[k + 1].IsWord("BY"))
                {
                    var (col, end) = ColumnRef(t, k + 2);
                    if (col is not null && end + 1 < t.Count && t[end + 1].IsWord("DESC")) orderBy = col;
                }
                break;
            }
            if (!tok.IsIdentifier || (k > 0 && t[k - 1].IsPunct("."))) continue;
            var (operand, opEnd) = ColumnRef(t, k);
            var column = operand is null ? null : settings.Column(operand);
            if (column is null || opEnd + 1 >= t.Count) continue;
            var prefix = opEnd > k ? tok.Text : null;
            var mine = prefix is null ? isFrom
                : string.Equals(prefix, alias, StringComparison.OrdinalIgnoreCase) || string.Equals(prefix, table, StringComparison.OrdinalIgnoreCase);
            if (!mine) { k = opEnd; continue; }
            if (Comparison(t, opEnd + 1) is { } cmp) terms.Add(new HrProcFilterTerm(column, cmp.Op, cmp.Values));
            else if (t[opEnd + 1].Text == "=" && opEnd + 2 < t.Count && t[opEnd + 2].Kind == SqlTokenKind.Variable)
                terms.Add(new HrProcFilterTerm(column, "=", null));
            k = opEnd;
        }
        if (terms.Count == 0) return null;

        // TOP 1 in the SELECT this FROM belongs to.
        var top1 = false;
        for (var k = at - 1; k >= 0 && k >= at - 60; k--)
        {
            if (t[k].IsWord("SELECT")) { top1 = k + 2 < t.Count && t[k + 1].IsWord("TOP") && t[k + 2].Text == "1"; break; }
        }
        return new HrProcFilter(table, terms.DistinctBy(x => $"{x.Column}{x.Operator}{string.Join(",", x.Values ?? new())}").ToList(),
            top1 ? orderBy : null, new List<int> { t[at].Line });
    }

    /// <summary>The header's parameters: "@name type" up to AS (a procedure) or RETURNS (a function).</summary>
    private static List<HrProcParameter> Parameters(IReadOnlyList<SqlToken> t)
    {
        var list = new List<HrProcParameter>();
        var start = -1;
        for (var i = 0; i + 1 < t.Count; i++)
        {
            if ((t[i].IsWord("PROCEDURE") || t[i].IsWord("PROC") || t[i].IsWord("FUNCTION")) && i > 0 && (t[i - 1].IsWord("CREATE") || t[i - 1].IsWord("ALTER")))
            {
                var (_, end) = QualifiedName(t, i + 1);
                start = end + 1;
                break;
            }
        }
        if (start < 0) return list;
        var depth = 0;
        for (var k = start; k < t.Count; k++)
        {
            var tok = t[k];
            if (tok.IsPunct("(")) { depth++; continue; }
            if (tok.IsPunct(")")) { depth--; continue; }
            if (depth <= 0 && (tok.IsWord("AS") || tok.IsWord("RETURNS") || tok.IsWord("BEGIN") || tok.IsWord("WITH"))) break;
            if (depth > 1 || tok.Kind != SqlTokenKind.Variable || k + 1 >= t.Count || !t[k + 1].IsIdentifier) continue;
            var type = new StringBuilder(t[k + 1].Text);
            if (k + 2 < t.Count && t[k + 2].IsPunct("("))
            {
                var close = Matching(t, k + 2);
                if (close > 0) type.Append('(').Append(string.Join(",", t.Skip(k + 3).Take(close - k - 3).Where(x => !x.IsPunct(",")).Select(x => x.Text))).Append(')');
            }
            list.Add(new HrProcParameter(tok.Text, type.ToString()));
        }
        return list;
    }

    /// <summary>
    /// Sections for a file without section comments: each IF at the top level of
    /// the body starts one, named by its condition, and it runs to the line before
    /// the next top-level IF (or the end).
    /// </summary>
    private static List<HrProcSection> TopLevelIfBlocks(IReadOnlyList<SqlToken> t, int lastLine)
    {
        var bodyStart = -1;
        for (var i = 0; i < t.Count; i++)
            if (t[i].IsWord("BEGIN")) { bodyStart = i; break; }
        if (bodyStart < 0) return new();

        var starts = new List<(int Line, string Name)>();
        var depth = 0;
        for (var i = bodyStart; i < t.Count; i++)
        {
            if (t[i].IsWord("BEGIN") || t[i].IsWord("CASE")) depth++;
            else if (t[i].IsWord("END")) depth--;
            else if (depth == 1 && t[i].IsWord("IF") && (i == 0 || !t[i - 1].IsWord("ELSE")))
            {
                var words = new StringBuilder("IF");
                for (var k = i + 1; k < t.Count && k < i + 12 && !t[k].IsWord("BEGIN") && t[k].Line == t[i].Line; k++)
                    words.Append(t[k].Kind is SqlTokenKind.Punct ? "" : " ").Append(t[k].Kind == SqlTokenKind.String ? $"'{t[k].Text}'" : t[k].Text);
                var name = words.ToString();
                starts.Add((t[i].Line, name.Length > 60 ? name[..60] + "…" : name));
            }
        }
        return starts.Select((s, k) => new HrProcSection(s.Name, s.Line, k + 1 < starts.Count ? starts[k + 1].Line - 1 : lastLine)).ToList();
    }
}
