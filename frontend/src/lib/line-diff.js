// A line diff for the Rulebook's history (docs/HR_RULEBOOK_PROMPT.md §3.6), written
// by hand: the longest common subsequence of the two texts' lines. Lines in it
// are unchanged; the rest of the old text was removed and the rest of the new
// one added. No dependencies, so scripts/hr-eval.mjs imports this file as it is
// and checks the diff the page shows.
//
// Pages are a few hundred lines at most, so the O(n·m) table is fine.

/** "a\nb\n" → ["a", "b"]: a final line break does not make an empty last line. */
export function toLines(text) {
  const lines = String(text ?? '').replace(/\r\n?/g, '\n').split('\n')
  if (lines.length > 1 && lines.at(-1) === '') lines.pop()
  return lines
}

/**
 * The lines of `before` and `after` in reading order, each
 * { type: 'same' | 'del' | 'add', text, from?: line in before, to?: line in after }.
 * Where lines changed, the removed ones come before the added ones.
 */
export function diffLines(before, after) {
  const a = toLines(before)
  const b = toLines(after)
  const n = a.length
  const m = b.length

  // lcs[i][j]: the common subsequence length of a[i..] and b[j..].
  const lcs = Array.from({ length: n + 1 }, () => new Uint32Array(m + 1))
  for (let i = n - 1; i >= 0; i--)
    for (let j = m - 1; j >= 0; j--)
      lcs[i][j] = a[i] === b[j] ? lcs[i + 1][j + 1] + 1 : Math.max(lcs[i + 1][j], lcs[i][j + 1])

  const out = []
  let i = 0
  let j = 0
  while (i < n || j < m) {
    if (i < n && j < m && a[i] === b[j]) {
      out.push({ type: 'same', text: a[i], from: i + 1, to: j + 1 })
      i++
      j++
    } else if (j >= m || (i < n && lcs[i + 1][j] >= lcs[i][j + 1])) {
      out.push({ type: 'del', text: a[i], from: i + 1 })
      i++
    } else {
      out.push({ type: 'add', text: b[j], to: j + 1 })
      j++
    }
  }
  return out
}

/** How many lines were added and removed. */
export function diffStats(diff) {
  return {
    added: diff.filter((d) => d.type === 'add').length,
    removed: diff.filter((d) => d.type === 'del').length,
  }
}
