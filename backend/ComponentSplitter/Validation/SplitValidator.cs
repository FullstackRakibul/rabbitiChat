// Stage 6 — validation. A SEPARATE stage, because generation succeeding proves
// only that code came out, not that the code is right.
//
// INDEPENDENCE IS THE POINT, and it has two halves.
//
// FIRST: every check reads the GENERATED TEXT, never the plan or the generator's
// intentions. @Input/@Output are recovered by parsing the emitted child .ts, and
// wiring by parsing the emitted parent. If the generator planned something and
// failed to emit it, the plan still claims success — only the text disagrees.
//
// SECOND: the binding check's right-hand side is THE PARSER'S FACTS, re-derived
// here from the IR. Checking the output only against itself is not validation:
// a plan that drops a required @Input yields a child without it and a parent
// that does not bind it, which agree with each other perfectly and are wrong
// together. Comparing against Stage 2a is what turns that from an invisible
// false pass into a named failure.
//
//   - coverage : re-compose children into the parent, compare to the ORIGINAL
//   - bindings : emitted contract vs. the parent, AND vs. the parser's facts
//   - wiring   : emitted imports[]/declarations vs. the parser's detection

using System.Text.RegularExpressions;
using RabbitIC.Api.ComponentSplitter.Facts;
using RabbitIC.Api.ComponentSplitter.Generation;
using RabbitIC.Api.ComponentSplitter.Ir;

namespace RabbitIC.Api.ComponentSplitter.Validation;

/// <summary>One check's outcome.</summary>
/// <param name="Check">"coverage" | "bindings" | "wiring".</param>
/// <param name="Passed">Whether it passed.</param>
/// <param name="Summary">One line, shown next to the check.</param>
/// <param name="Details">Specifics — exactly what is off, when it failed.</param>
public record ValidationCheck(
    string Check, bool Passed, string Summary, IReadOnlyList<string> Details);

/// <summary>
/// The full report. <see cref="Passed"/> gates the "looks correct" state in the
/// UI — a failed check must block it, or validation is decoration.
/// </summary>
public record ValidationReport(IReadOnlyList<ValidationCheck> Checks)
{
    public bool Passed => Checks.All(c => c.Passed);
}

public static partial class SplitValidator
{
    [GeneratedRegex(@"@Input\(\)\s*([A-Za-z_$][\w$]*)", RegexOptions.Compiled)]
    private static partial Regex EmittedInput();

    [GeneratedRegex(@"@Output\(\)\s*([A-Za-z_$][\w$]*)", RegexOptions.Compiled)]
    private static partial Regex EmittedOutput();

    [GeneratedRegex(@"imports\s*:\s*\[([^\]]*)\]", RegexOptions.Compiled)]
    private static partial Regex EmittedImports();

    [GeneratedRegex(@"declarations\s*:\s*\[([^\]]*)\]", RegexOptions.Compiled)]
    private static partial Regex EmittedDeclarations();

    [GeneratedRegex(@"\[([A-Za-z_$][\w$]*)\]\s*=", RegexOptions.Compiled)]
    private static partial Regex ParentPropertyBinding();

    [GeneratedRegex(@"\(([A-Za-z_$][\w$]*)\)\s*=", RegexOptions.Compiled)]
    private static partial Regex ParentEventBinding();

    /// <summary>
    /// Runs all three checks.
    ///
    /// Note what is passed in: the IR, and the generated output. NOT the plan.
    /// The binding check RE-DERIVES the required contract from the IR (Stage 2a,
    /// run again here) and compares the generated code against that — never
    /// against the plan the generator was given.
    ///
    /// That distinction is the whole value of this stage. Checking generated code
    /// against the plan that produced it only proves the generator is
    /// self-consistent: a plan that drops a required @Input would produce a child
    /// without it and a parent that does not bind it, and the two would agree
    /// with each other perfectly while being wrong. Comparing against the facts
    /// catches exactly that case.
    /// </summary>
    public static ValidationReport Validate(ComponentIr ir, GenerationResult generated)
    {
        // Stage 2a, independently: what the parser says each block REQUIRES.
        var facts = StructuralFactDeriver.Derive(ir)
            .ToDictionary(f => f.CandidateId);

        return new(new[]
        {
            CheckCoverage(ir, generated),
            CheckBindingIntegrity(generated, facts),
            CheckWiring(ir, generated),
        });
    }

    /// <summary>
    /// COVERAGE — do the children, composed via the parent, reproduce the
    /// original template?
    ///
    /// Verified by INVERSE SUBSTITUTION: take the generated parent template and
    /// put each child's block back where its element sits. The result must equal
    /// the original. This catches both failure directions — markup dropped
    /// (result too short) and markup duplicated (result too long) — without
    /// needing to diff two trees.
    ///
    /// Comparison is whitespace-normalised: the generator re-indents when it
    /// inlines a template, and an indentation change is not a dropped element.
    /// </summary>
    private static ValidationCheck CheckCoverage(ComponentIr ir, GenerationResult generated)
    {
        var details = new List<string>();
        var recomposed = generated.ParentTemplate;

        // Put each block back, replacing the child element that stands for it.
        foreach (var replacement in generated.Replacements)
        {
            var idx = recomposed.IndexOf(replacement.ChildElement, StringComparison.Ordinal);
            if (idx < 0)
            {
                details.Add(
                    $"{replacement.ComponentName}: its element <{ElementName(replacement.ChildElement)}> " +
                    "is not present in the generated parent template, so the block it replaced " +
                    "is missing from the output entirely.");
                continue;
            }

            // A second occurrence means the same block was emitted twice.
            var second = recomposed.IndexOf(
                replacement.ChildElement, idx + replacement.ChildElement.Length, StringComparison.Ordinal);
            if (second >= 0)
                details.Add(
                    $"{replacement.ComponentName}: its element appears more than once in the " +
                    "parent, which would render the extracted block twice.");

            recomposed = recomposed[..idx]
                       + replacement.OriginalBlock
                       + recomposed[(idx + replacement.ChildElement.Length)..];
        }

        var original = Normalise(ir.TemplateSource);
        var rebuilt = Normalise(recomposed);

        if (original != rebuilt)
        {
            details.Add(DescribeDifference(original, rebuilt));
            return new ValidationCheck("coverage", false,
                "The extracted components do not reproduce the original template.", details);
        }

        return details.Count > 0
            ? new ValidationCheck("coverage", false,
                "The template recomposes, but a block is rendered more than once.", details)
            : new ValidationCheck("coverage", true,
                $"All {generated.Replacements.Count} extracted block(s) recompose into the " +
                "original template exactly — no markup dropped or duplicated.",
                Array.Empty<string>());
    }

    /// <summary>
    /// BINDING INTEGRITY — three comparisons, not one.
    ///
    ///   1. child's emitted @Input  ↔ parent's emitted binding   (self-consistency)
    ///   2. child's emitted @Output ↔ parent's emitted handler   (self-consistency)
    ///   3. emitted contract        ↔ THE PARSER'S FACTS         (correctness)
    ///
    /// The third is the one that matters. The first two can both pass on output
    /// that is uniformly wrong — drop a required @Input from the plan and the
    /// generator emits a child without it AND a parent that does not bind it, in
    /// perfect agreement. Only the facts know the block actually needed it.
    /// </summary>
    private static ValidationCheck CheckBindingIntegrity(
        GenerationResult generated, IReadOnlyDictionary<int, CandidateFacts> facts)
    {
        var details = new List<string>();

        foreach (var file in generated.Files.Where(f => f.Kind == "child-ts"))
        {
            var componentName = ClassNameOf(file.Contents) ?? file.Path;

            var declaredInputs = EmittedInput().Matches(file.Contents)
                .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            var declaredOutputs = EmittedOutput().Matches(file.Contents)
                .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

            var replacement = generated.Replacements
                .FirstOrDefault(r => r.ComponentName == componentName);

            if (replacement is null)
            {
                details.Add($"{componentName}: generated as a file but never used in the parent.");
                continue;
            }

            var element = replacement.ChildElement;

            var passedInputs = ParentPropertyBinding().Matches(element)
                .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            var handledOutputs = ParentEventBinding().Matches(element)
                .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

            foreach (var input in declaredInputs.Where(i => !passedInputs.Contains(i)))
                details.Add(
                    $"{componentName}: declares @Input() {input} but the parent never binds " +
                    $"[{input}] — it would be undefined at runtime.");

            foreach (var bound in passedInputs.Where(p => !declaredInputs.Contains(p)))
                details.Add(
                    $"{componentName}: the parent binds [{bound}] but the component declares " +
                    "no such @Input() — Angular would reject this at compile time.");

            foreach (var output in declaredOutputs.Where(o => !handledOutputs.Contains(o)))
                details.Add(
                    $"{componentName}: declares @Output() {output} but the parent does not " +
                    $"handle ({output}) — the event would go nowhere.");

            foreach (var handled in handledOutputs.Where(h => !declaredOutputs.Contains(h)))
                details.Add(
                    $"{componentName}: the parent handles ({handled}) but the component emits " +
                    "no such @Output().");

            // ---- 3. The check that cannot be satisfied by self-consistency ----
            // Compare the emitted contract against what the PARSER says this
            // block requires. This is the only comparison whose right-hand side
            // the generator did not produce.
            if (!facts.TryGetValue(replacement.CandidateId, out var required)) continue;

            foreach (var input in required.Inputs.Where(i => !declaredInputs.Contains(i.Name)))
                details.Add(
                    $"{componentName}: the extracted block reads '{input.Name}' from the parent, " +
                    $"so it requires @Input() {input.Name}: {input.Type} — but the generated " +
                    "component does not declare it. The block would render with it undefined.");

            foreach (var output in required.Outputs.Where(o => !declaredOutputs.Contains(o.Name)))
                details.Add(
                    $"{componentName}: the extracted block calls '{output.ParentHandler}()', which " +
                    $"lives on the parent, so it requires @Output() {output.Name} — but the " +
                    "generated component does not emit it. That interaction would be dead.");

            foreach (var extra in declaredOutputs.Where(o => !required.Outputs.Any(r => r.Name == o)))
                details.Add(
                    $"{componentName}: declares @Output() {extra}, but nothing in the extracted " +
                    "block raises it. It would never fire.");

            foreach (var extra in declaredInputs.Where(i => !required.Inputs.Any(r => r.Name == i)))
                details.Add(
                    $"{componentName}: declares @Input() {extra}, but nothing in the extracted " +
                    "block reads it.");
        }

        return details.Count == 0
            ? new ValidationCheck("bindings", true,
                "Every @Input() is passed by the parent, every @Output() is handled, and the "
                + "emitted contract matches what the parser derived from the original block.",
                Array.Empty<string>())
            : new ValidationCheck("bindings", false,
                $"{details.Count} binding problem(s) between parent and children.", details);
    }

    /// <summary>
    /// WIRING — is each child registered where the PARSER said it must be?
    ///
    /// The expectation comes from ir.Wiring, so this check cannot be satisfied by
    /// the generator having decided something self-consistently wrong.
    /// </summary>
    private static ValidationCheck CheckWiring(ComponentIr ir, GenerationResult generated)
    {
        var details = new List<string>();

        var parent = generated.Files.FirstOrDefault(f => f.Kind == "parent-ts");
        if (parent is null)
            return new ValidationCheck("wiring", false,
                "No parent component file was generated.", Array.Empty<string>());

        var childNames = generated.Replacements
            .Select(r => r.ComponentName).Distinct(StringComparer.Ordinal).ToList();

        if (ir.Wiring == WiringKind.Standalone)
        {
            var importsMatch = EmittedImports().Match(parent.Contents);
            if (!importsMatch.Success)
            {
                details.Add(
                    "The parent is standalone but its @Component has no imports array, " +
                    "so no child is registered.");
            }
            else
            {
                var imported = importsMatch.Groups[1].Value
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.Ordinal);

                foreach (var child in childNames.Where(c => !imported.Contains(c)))
                    details.Add(
                        $"{child} is not in the standalone parent's imports array — " +
                        "its selector would not resolve.");
            }

            if (parent.Contents.Contains("declarations", StringComparison.Ordinal))
                details.Add(
                    "The parent is standalone but a declarations array was emitted; " +
                    "standalone components use imports.");
        }
        else
        {
            // NgModule: the child must NOT be in a standalone imports array, and
            // the required declarations edit must be stated.
            foreach (var child in childNames)
            {
                var declaredSomewhere =
                    EmittedDeclarations().Matches(parent.Contents)
                        .Any(m => m.Groups[1].Value.Contains(child, StringComparison.Ordinal))
                    || parent.Contents.Contains($"declarations: [{ir.ClassName}, ", StringComparison.Ordinal);

                if (!declaredSomewhere)
                    details.Add(
                        $"{child} is not listed in a declarations array — an NgModule-based " +
                        "parent registers children via declarations.");
            }

            if (parent.Contents.Contains("standalone: true", StringComparison.Ordinal))
                details.Add(
                    "The original component is NgModule-based but the generated parent is " +
                    "marked standalone: true.");
        }

        var where = ir.Wiring == WiringKind.Standalone ? "imports" : "declarations";

        return details.Count == 0
            ? new ValidationCheck("wiring", true,
                $"All {childNames.Count} child component(s) registered in {where}, " +
                $"matching the detected {(ir.Wiring == WiringKind.Standalone ? "standalone" : "NgModule")} parent.",
                Array.Empty<string>())
            : new ValidationCheck("wiring", false,
                $"{details.Count} wiring problem(s).", details);
    }

    /// <summary>
    /// Where two templates first diverge, in terms a reviewer can act on. A bare
    /// "they differ" would leave someone diffing 200 lines by eye.
    /// </summary>
    private static string DescribeDifference(string original, string rebuilt)
    {
        var limit = Math.Min(original.Length, rebuilt.Length);
        var i = 0;
        while (i < limit && original[i] == rebuilt[i]) i++;

        var context = original[Math.Max(0, i - 40)..Math.Min(original.Length, i + 40)];

        if (rebuilt.Length < original.Length)
            return $"Markup is missing from the output. First difference near: …{context}…";

        return rebuilt.Length > original.Length
            ? $"The output contains markup the original did not. First difference near: …{context}…"
            : $"The output differs from the original near: …{context}…";
    }

    /// <summary>
    /// Whitespace-insensitive comparison form. Collapses runs of whitespace and
    /// drops whitespace between tags, so re-indentation is not a false failure
    /// while a moved or missing element still is.
    /// </summary>
    private static string Normalise(string template)
    {
        var collapsed = Regex.Replace(template, @"\s+", " ");
        collapsed = Regex.Replace(collapsed, @">\s+<", "><");
        return collapsed.Trim();
    }

    private static string ElementName(string element)
    {
        var end = element.IndexOfAny(new[] { ' ', '>', '/' }, 1);
        return end > 1 ? element[1..end] : element.Trim('<', '>');
    }

    private static string? ClassNameOf(string source)
    {
        var match = Regex.Match(source, @"export\s+class\s+([A-Za-z_$][\w$]*)");
        return match.Success ? match.Groups[1].Value : null;
    }
}
