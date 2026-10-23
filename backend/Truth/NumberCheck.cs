// সত্য মন্ত্রণালয় — the number check (docs/TRUTH_PROMPT.md §1.1 rule 4).
//
// Every CVE ID, CVSS score, version, date, time, count and money amount an item
// shows must appear in the fetched text of one of its sources; one that does not
// blocks publishing until it is fixed or verified by hand with a note.
//
// Both sides are normalised the same way first: Bangla digits become ASCII,
// dates written in words (English or Bangla month names) become yyyy-MM-dd,
// 12-hour times become HH:mm, and thousands separators go. A version line ("3.1")
// is found when a source names it or a release of it ("3.1.7"). Plain integers
// below 10 are not checked — "step 1" and "৩টি" are wording, not claims — but
// money and percentages are, whatever their size.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Truth;

/// <summary>One number an item shows. <see cref="Found"/>: in a source; <see cref="HandNote"/>: verified by hand instead.</summary>
public sealed record NumberFinding(string Token, string Kind, List<string> Fields, bool Found, string? HandNote)
{
    public bool Ok => Found || HandNote is not null;
}

public static partial class NumberCheck
{
    public const string KindCve = "cve";
    public const string KindDate = "date";
    public const string KindTime = "time";
    public const string KindVersion = "version";
    public const string KindNumber = "number";
    public const string KindMoney = "money";

    [GeneratedRegex(@"\bCVE-\d{4}-\d{4,7}\b", RegexOptions.IgnoreCase)]
    private static partial Regex Cve();

    [GeneratedRegex(@"\b(\d{4})-(\d{2})-(\d{2})\b")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"\b([01]?\d|2[0-3]):([0-5]\d)\b")]
    private static partial Regex Time24();

    [GeneratedRegex(@"\b(1[0-2]|0?[1-9])(?::([0-5]\d))?\s?([ap])\.?m\.?(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex Time12();

    [GeneratedRegex(@"(?<![\d.])v?(\d+\.\d+(?:\.\d+)*)(?![\d.]*\d)")]
    private static partial Regex Dotted();

    [GeneratedRegex(@"(?:[$€£৳]\s?(\d+(?:\.\d+)?))|(?:(\d+(?:\.\d+)?)\s?(?:usd|bdt|eur|gbp|taka|টাকা|ডলার|মিলিয়ন|million|billion|বিলিয়ন)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex Money();

    [GeneratedRegex(@"(?<![\d.])(\d+)\s?%")]
    private static partial Regex Percent();

    [GeneratedRegex(@"(?<![\d.])(\d+)(?![\d.]*\d)")]
    private static partial Regex Integer();

    [GeneratedRegex(@"(?<=\d),(?=\d{3}\b)")]
    private static partial Regex Thousands();

    [GeneratedRegex(@"\[\d{1,2}\]")]
    private static partial Regex Citation();

    private static readonly string[] EnMonths =
        { "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december" };
    private static readonly string[] BnMonths =
        { "জানুয়ারি", "ফেব্রুয়ারি", "মার্চ", "এপ্রিল", "মে", "জুন", "জুলাই", "আগস্ট", "সেপ্টেম্বর", "অক্টোবর", "নভেম্বর", "ডিসেম্বর" };

    private static readonly Regex WordDate = BuildWordDate();

    private static Regex BuildWordDate()
    {
        var months = string.Join("|", EnMonths.Select(m => m.Length > 3 ? $"{m[..3]}(?:{m[3..]})?\\.?" : m)
            .Concat(BnMonths.Select(Regex.Escape)));
        // "21 October 2026", "October 21, 2026", "Oct. 21 2026", "২১ অক্টোবর ২০২৬" (digits already ASCII).
        return new Regex($@"(?:\b(?<d1>\d{{1,2}})(?:st|nd|rd|th)?\s+(?<m1>{months})[,]?\s+(?<y1>\d{{4}})\b)|(?:(?<m2>{months})\s+(?<d2>\d{{1,2}})(?:st|nd|rd|th)?,?\s+(?<y2>\d{{4}})\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    /// <summary>Bangla digits to ASCII, dashes and spaces made plain, dates in words to ISO, 12-hour times to HH:mm.</summary>
    public static string Normalise(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            sb.Append(ch switch
            {
                >= '০' and <= '৯' => (char)('0' + (ch - '০')),
                '‐' or '‑' or '‒' or '–' or '—' or '−' => '-',
                ' ' or ' ' or ' ' => ' ',
                _ => ch,
            });
        }
        var s = Thousands().Replace(sb.ToString(), "");
        s = WordDate.Replace(s, m =>
        {
            var d = m.Groups["d1"].Success ? m.Groups["d1"].Value : m.Groups["d2"].Value;
            var mo = m.Groups["m1"].Success ? m.Groups["m1"].Value : m.Groups["m2"].Value;
            var y = m.Groups["y1"].Success ? m.Groups["y1"].Value : m.Groups["y2"].Value;
            var month = MonthNumber(mo);
            return month is null ? m.Value : $"{y}-{month:00}-{int.Parse(d, CultureInfo.InvariantCulture):00}";
        });
        s = Time12().Replace(s, m =>
        {
            var h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) % 12;
            if (char.ToLowerInvariant(m.Groups[3].Value[0]) == 'p') h += 12;
            var min = m.Groups[2].Success ? m.Groups[2].Value : "00";
            return $"{h:00}:{min}";
        });
        return s;
    }

    private static int? MonthNumber(string word)
    {
        var w = word.TrimEnd('.').ToLowerInvariant();
        for (var i = 0; i < 12; i++)
            if (EnMonths[i].StartsWith(w, StringComparison.Ordinal) && w.Length >= 3 || BnMonths[i] == word) return i + 1;
        return null;
    }

    /// <summary>The numbers in one normalised text, as (token, kind). Each span is taken once, by the first rule that claims it.</summary>
    public static List<(string Token, string Kind)> Extract(string normalised)
    {
        var found = new List<(string, string)>();
        var s = Citation().Replace(normalised, " ");
        string Mask(string text, Match m) => text[..m.Index] + new string(' ', m.Length) + text[(m.Index + m.Length)..];

        foreach (Match m in Cve().Matches(s)) { found.Add((m.Value.ToUpperInvariant(), KindCve)); s = Mask(s, m); }
        foreach (Match m in IsoDate().Matches(s)) { found.Add((m.Value, KindDate)); s = Mask(s, m); }
        foreach (Match m in Time24().Matches(s))
        {
            found.Add(($"{int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture):00}:{m.Groups[2].Value}", KindTime));
            s = Mask(s, m);
        }
        foreach (Match m in Money().Matches(s))
        {
            found.Add((m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, KindMoney));
            s = Mask(s, m);
        }
        foreach (Match m in Percent().Matches(s)) { found.Add((m.Groups[1].Value, KindNumber)); s = Mask(s, m); }
        foreach (Match m in Dotted().Matches(s)) { found.Add((m.Groups[1].Value, KindVersion)); s = Mask(s, m); }
        foreach (Match m in Integer().Matches(s))
        {
            if (m.Value.Length > 1 && long.TryParse(m.Value, out var n) && n >= 10) found.Add((m.Value.TrimStart('0') is { Length: > 0 } t ? t : "0", KindNumber));
        }
        return found;
    }

    /// <summary>What the sources say, as sets the item's tokens are looked up in.</summary>
    private sealed class Corpus
    {
        public readonly HashSet<string> Exact = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Versions = new();
        public readonly HashSet<string> Numbers = new();

        public Corpus(IEnumerable<string> texts)
        {
            foreach (var text in texts)
            foreach (var (token, kind) in Extract(Normalise(text)))
            {
                Exact.Add($"{kind}:{token}");
                if (kind == KindVersion) Versions.Add(token);
                if (kind is KindNumber or KindMoney or KindVersion) Numbers.Add(token);
                if (kind == KindDate) { Numbers.Add(token[..4]); Numbers.Add(token[5..7].TrimStart('0')); Numbers.Add(token[8..].TrimStart('0')); }
                if (kind == KindCve) foreach (var part in token.Split('-').Skip(1)) Numbers.Add(part.TrimStart('0'));
            }
        }

        public bool Has(string token, string kind) => kind switch
        {
            KindVersion => Versions.Any(v => v == token || v.StartsWith(token + ".", StringComparison.Ordinal)) || Numbers.Contains(token),
            KindNumber or KindMoney => Numbers.Contains(token) || Exact.Contains($"{KindNumber}:{token}") || Exact.Contains($"{KindMoney}:{token}"),
            _ => Exact.Contains($"{kind}:{token}"),
        };
    }

    /// <summary>The fields of an item that are public, each with its text — what the check reads.</summary>
    public static IEnumerable<(string Field, string Text)> PublicFields(
        TrItem item, IReadOnlyList<TrAdvisory> advisories, IReadOnlyList<TrTimelineEvent> timeline)
    {
        (string, string?)[] own =
        {
            ("headlineBn", item.HeadlineBn), ("headlineEn", item.HeadlineEn), ("summaryBn", item.SummaryBn), ("summaryEn", item.SummaryEn),
            ("impactBn", item.ImpactBn), ("impactEn", item.ImpactEn), ("whatToDoBn", item.WhatToDoBn), ("whatToDoEn", item.WhatToDoEn),
        };
        foreach (var (f, t) in own) if (!string.IsNullOrWhiteSpace(t)) yield return (f, t!);
        if (item.PatchScheduledAtUtc is { } patch)
            yield return ("patchScheduledAt", patch.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        for (var i = 0; i < advisories.Count; i++)
        {
            var a = advisories[i];
            var p = $"advisories[{i + 1}]";
            if (!string.IsNullOrWhiteSpace(a.CveId)) yield return ($"{p}.cveId", a.CveId!);
            if (a.Cvss is { } cvss) yield return ($"{p}.cvss", cvss.ToString("0.0", CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(a.TitleBn)) yield return ($"{p}.titleBn", a.TitleBn!);
            if (!string.IsNullOrWhiteSpace(a.TitleEn)) yield return ($"{p}.titleEn", a.TitleEn!);
            if (!string.IsNullOrWhiteSpace(a.AffectedRange)) yield return ($"{p}.affectedRange", a.AffectedRange!);
            foreach (var v in TruthJson.Strings(a.FixedVersionsJson)) yield return ($"{p}.fixedVersions", v);
        }
        for (var i = 0; i < timeline.Count; i++)
        {
            var e = timeline[i];
            yield return ($"timeline[{i + 1}].date", e.OccurredAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(e.TextBn)) yield return ($"timeline[{i + 1}].textBn", e.TextBn);
            if (!string.IsNullOrWhiteSpace(e.TextEn)) yield return ($"timeline[{i + 1}].textEn", e.TextEn!);
        }
    }

    /// <summary>Every number the item shows, whether a source has it, and any hand verification.</summary>
    public static List<NumberFinding> Run(
        TrItem item, IReadOnlyList<TrAdvisory> advisories, IReadOnlyList<TrTimelineEvent> timeline,
        IReadOnlyList<TrSource> sources, IReadOnlyDictionary<string, string> handVerified)
    {
        var corpus = new Corpus(sources.SelectMany(s => new[] { s.Title, s.FetchedText }));
        var byToken = new Dictionary<(string, string), List<string>>();
        foreach (var (field, text) in PublicFields(item, advisories, timeline))
        foreach (var key in Extract(Normalise(text)))
        {
            if (!byToken.TryGetValue(key, out var fields)) byToken[key] = fields = new List<string>();
            if (!fields.Contains(field)) fields.Add(field);
        }
        return byToken.Select(kv => new NumberFinding(
                kv.Key.Item1, kv.Key.Item2, kv.Value, corpus.Has(kv.Key.Item1, kv.Key.Item2),
                handVerified.TryGetValue(kv.Key.Item1, out var note) ? note : null))
            .OrderBy(f => f.Ok).ThenBy(f => f.Kind).ThenBy(f => f.Token, StringComparer.Ordinal)
            .ToList();
    }
}
