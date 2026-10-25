// সত্য মন্ত্রণালয় — the command safety check (docs/TRUTH_PROMPT.md §6.4).
//
// Runs on every rendered variant of a guide (each platform × every variable
// combination), because a template can be safe for one and not another. Each
// finding is a rule, its level and the line that tripped it:
//
//   block  curl_pipe_sh · chmod_777 · rm_rf_root · tls_disabled · inline_secret
//   warn   memory_unlimited · waf_block_all_multipart · tee_overwrite ·
//          restart_without_verify · unknown_ini_directive · docker.exec_ephemeral
//
// A block stops publishing; a warning needs an acknowledgement with a note, and
// stays visible to readers (§11). The "do now" commands of an item go through it too.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RabbitIC.Api.Truth;

public sealed record SafetyFinding(string RuleId, string Level, int StepOrder, string Line, string Message)
{
    public const string Block = "block";
    public const string Warn = "warn";
}

/// <summary>A step as the check sees it, already rendered for one variant.</summary>
public sealed record RenderedStep(int Order, string Kind, string Code, string? Language);

public static partial class SafetyCheck
{
    public static readonly (string Id, string Level, string Message)[] Rules =
    {
        ("curl_pipe_sh", SafetyFinding.Block, "Pipes a download straight into a shell. Download, read, then run."),
        ("chmod_777", SafetyFinding.Block, "Makes files writable by everyone."),
        ("rm_rf_root", SafetyFinding.Block, "Deletes recursively from a root-like path."),
        ("tls_disabled", SafetyFinding.Block, "Turns certificate checking off."),
        ("inline_secret", SafetyFinding.Block, "A real-looking key, token or password is written in the command. Use a placeholder."),
        ("memory_unlimited", SafetyFinding.Warn, "memory_limit = -1 removes PHP's memory cap."),
        ("waf_block_all_multipart", SafetyFinding.Warn, "Blocks every multipart upload, not only the vulnerable path."),
        ("tee_overwrite", SafetyFinding.Warn, "tee without -a replaces the whole file."),
        ("restart_without_verify", SafetyFinding.Warn, "Restarts a service with no verify step after it."),
        ("unknown_ini_directive", SafetyFinding.Warn, "Sets a directive not known for the selected version."),
        ("docker.exec_ephemeral", SafetyFinding.Warn, "Edits a file inside a running container with docker exec: lost when the container is recreated. Change the image or a mounted config."),
    };

    private static string LevelOf(string id) => Rules.First(r => r.Id == id).Level;
    private static string MessageOf(string id) => Rules.First(r => r.Id == id).Message;

    [GeneratedRegex(@"\b(curl|wget)\b[^\n|]*\|\s*(sudo\s+(-\S+\s+)*)?(ba|z|da|k)?sh\b|\b(iwr|irm|Invoke-WebRequest|Invoke-RestMethod)\b[^\n|]*\|\s*(iex|Invoke-Expression)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CurlPipe();

    [GeneratedRegex(@"\bchmod\s+(-[a-zA-Z]+\s+)*(0?777|(a|ugo)\+rwx)\b")]
    private static partial Regex Chmod777();

    [GeneratedRegex(@"\bcurl\b[^\n]*\s(-[a-zA-Z]*k[a-zA-Z]*|--insecure)(\s|$)|\bwget\b[^\n]*--no-check-certificate|NODE_TLS_REJECT_UNAUTHORIZED\s*=\s*['""]?0|sslVerify\s*=?\s*false|\bverify\s*=\s*False\b|-SkipCertificateCheck|strict-ssl\s*(=\s*)?false|--no-verify-ssl|InsecureSkipVerify\s*:\s*true", RegexOptions.IgnoreCase)]
    private static partial Regex TlsOff();

    [GeneratedRegex(@"\b(sk-(or-v1-|ant-|proj-)?[A-Za-z0-9_-]{20,}|gh[pousr]_[A-Za-z0-9]{30,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{35}|xox[abprs]-[A-Za-z0-9-]{10,})\b|Bearer\s+[A-Za-z0-9._-]{20,}")]
    private static partial Regex ProviderKey();

    [GeneratedRegex(@"\b([A-Za-z_]*(api[_-]?key|token|secret|passw(or)?d|pwd)[A-Za-z_]*)\s*[=:]\s*['""]?([^\s'""]{8,})", RegexOptions.IgnoreCase)]
    private static partial Regex SecretAssign();

    [GeneratedRegex(@"^(your[_-]|<|\$|\{\{|%|x{3,}|\*{3,}|changeme|change[_-]me|replace|example|placeholder|redacted)", RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\bmemory_limit\s*=\s*-1\b")]
    private static partial Regex MemoryUnlimited();

    [GeneratedRegex(@"\btee\s+((-[a-zA-Z]*|--[a-z-]+)\s+)*")]
    private static partial Regex Tee();

    [GeneratedRegex(@"\b(systemctl\s+(restart|reload)|service\s+\S+\s+(restart|reload)|Restart-Service|iisreset|apachectl\s+(-k\s+)?(graceful|restart)|nginx\s+-s\s+reload|docker\s+(compose\s+)?(restart|up)\b|docker-compose\s+(restart|up)|kubectl\s+rollout\s+restart|brew\s+services\s+restart|pm2\s+(restart|reload))", RegexOptions.IgnoreCase)]
    private static partial Regex Restart();

    [GeneratedRegex(@"\bdocker(\s+compose|-compose)?\s+exec\b.*(\bsed\s+-i|\btee\b|>\s*\S|\bvi\b|\bvim\b|\bnano\b|\bcat\s*>)")]
    private static partial Regex DockerExecEdit();

    [GeneratedRegex(@"^\s*([A-Za-z_][\w.]*)\s*=")]
    private static partial Regex IniDirective();

    private static readonly HashSet<string> RootLike = new()
    {
        "/", "/*", "~", "~/", "~/*", "$HOME", "$HOME/", "${HOME}", "/etc", "/usr", "/var", "/bin", "/sbin", "/boot", "/lib", "/lib64", "/opt", "/root", "/home", "/srv", "C:\\", "C:/",
    };

    /// <summary>The directives each guide variable knows (directives.json), version-bounded.</summary>
    public sealed record Directive(string? Since, string? Until);

    /// <summary>
    /// Every finding of one variant. <paramref name="values"/> holds the variant's
    /// variable values (platform included); <paramref name="directives"/> the registry.
    /// </summary>
    public static List<SafetyFinding> Run(IReadOnlyList<RenderedStep> steps, IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, Directive>> directives)
    {
        var findings = new List<SafetyFinding>();
        void Add(string rule, RenderedStep step, string line) =>
            findings.Add(new SafetyFinding(rule, LevelOf(rule), step.Order, line.Trim().Length > 200 ? line.Trim()[..200] : line.Trim(), MessageOf(rule)));

        var ordered = steps.OrderBy(s => s.Order).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var step = ordered[i];
            var lines = step.Code.Replace("\r", "").Split('\n');
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (CurlPipe().IsMatch(line)) Add("curl_pipe_sh", step, line);
                if (Chmod777().IsMatch(line)) Add("chmod_777", step, line);
                if (RmRfRoot(line)) Add("rm_rf_root", step, line);
                if (TlsOff().IsMatch(line)) Add("tls_disabled", step, line);
                if (ProviderKey().IsMatch(line) || SecretAssign().Matches(line).Any(m => !Placeholder().IsMatch(m.Groups[4].Value))) Add("inline_secret", step, line);
                if (MemoryUnlimited().IsMatch(line)) Add("memory_unlimited", step, line);
                if (TeeOverwrites(line)) Add("tee_overwrite", step, line);
                if (DockerExecEdit().IsMatch(line)) Add("docker.exec_ephemeral", step, line);
                // Undo steps come after the verify step by design (§6.1), so their restart isn't flagged.
                if (step.Kind != "undo" && Restart().IsMatch(line) && !ordered.Skip(i + 1).Any(s => s.Kind == "verify"))
                    Add("restart_without_verify", step, line);
            }
            if (step.Kind == "waf_rule" && Regex.IsMatch(step.Code, "multipart/form-data", RegexOptions.IgnoreCase)
                && !Regex.IsMatch(step.Code, @"\band\b|&&|\bpath\b", RegexOptions.IgnoreCase))
                Add("waf_block_all_multipart", step, lines.FirstOrDefault(l => l.Contains("multipart", StringComparison.OrdinalIgnoreCase)) ?? "");
            if (string.Equals(step.Language, "ini", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var line in lines)
                {
                    var m = IniDirective().Match(line);
                    if (!m.Success || line.TrimStart().StartsWith(';') || line.TrimStart().StartsWith('#')) continue;
                    if (!KnownDirective(m.Groups[1].Value, values, directives)) Add("unknown_ini_directive", step, line);
                }
            }
        }
        return findings;
    }

    /// <summary>
    /// A directive is known when some variable of the guide has it in the registry for
    /// that variable's selected version. With no registry entry for any of the guide's
    /// variables there is nothing to check against, and nothing is flagged.
    /// </summary>
    private static bool KnownDirective(string name, IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, Directive>> directives)
    {
        var registries = values.Where(v => directives.ContainsKey(v.Key)).ToList();
        if (registries.Count == 0) return true;
        foreach (var (variable, version) in registries)
        {
            if (!directives[variable].TryGetValue(name, out var d)) continue;
            var ok = !GuideTemplate.IsVersion(version)
                || ((d.Since is null || GuideTemplate.CompareVersions(version, d.Since) >= 0)
                    && (d.Until is null || GuideTemplate.CompareVersions(version, d.Until) <= 0));
            if (ok) return true;
        }
        return false;
    }

    private static bool RmRfRoot(string line)
    {
        var tokens = line.Split(new[] { ' ', '\t', ';', '&', '|' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] != "rm") continue;
            var flags = new StringBuilder();
            var targets = new List<string>();
            for (var j = i + 1; j < tokens.Length; j++)
            {
                if (tokens[j] == "--no-preserve-root") return true;
                if (tokens[j].StartsWith("--")) continue;
                if (tokens[j].StartsWith('-')) flags.Append(tokens[j][1..]);
                else targets.Add(tokens[j].Trim('"', '\''));
            }
            var f = flags.ToString();
            if ((f.Contains('r') || f.Contains('R')) && f.Contains('f') && targets.Any(t => RootLike.Contains(t) || RootLike.Contains(t.TrimEnd('/'))))
                return true;
        }
        return false;
    }

    private static bool TeeOverwrites(string line)
    {
        foreach (Match m in Tee().Matches(line))
        {
            var flags = m.Groups[1].Captures.Select(c => c.Value.Trim()).ToList();
            var append = flags.Any(f => f == "--append" || (f.StartsWith('-') && !f.StartsWith("--") && f.Contains('a')));
            var rest = line[(m.Index + m.Length)..].TrimStart();
            if (!append && rest.Length > 0 && !rest.StartsWith('|') && !rest.StartsWith('>')) return true;
        }
        return false;
    }

    /// <summary>A stable id for one finding: rule, variant, step and the exact line — so an acknowledgement goes stale when any of them changes.</summary>
    public static string Key(SafetyFinding f, string variant) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{f.RuleId}\u001f{variant}\u001f{f.StepOrder}\u001f{f.Line}")))[..20].ToLowerInvariant();

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, Directive>> LoadDirectives(JsonObject doc)
    {
        var map = new Dictionary<string, IReadOnlyDictionary<string, Directive>>();
        foreach (var (variable, list) in doc["variables"]?.AsObject() ?? new JsonObject())
        {
            var d = new Dictionary<string, Directive>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, info) in list?.AsObject() ?? new JsonObject())
                d[name] = new Directive(info?["since"]?.GetValue<string>(), info?["until"]?.GetValue<string>());
            map[variable] = d;
        }
        return map;
    }
}
