# Qwen 3.8 27B on a Mac mini M4 (24 GB) with Ollama

Settings for three jobs: code in your demo pattern, PDF analysis and extraction, and
reports from big Excel files.

Checked against the sources at the end on 11 October 2026. **Nothing here was run on
your Mac.** Each step ends with a check, so you confirm it on the machine itself.

## 1. The short version

| | Setting | Why |
|---|---|---|
| Build | Try `qwen3.8:27b` first. If any check in §3 fails, use the 3-bit build `hf.co/unsloth/Qwen3.8-27B-GGUF:UD-Q3_K_XL` | The 4-bit build is 18 GB and sits at the edge of this Mac's GPU memory. The 3-bit build is 13.1 GB |
| Context window | **16,384**, the same number in every place that sets one | The 24 GB Mac's default is 4K. A request with a different window reloads the whole model |
| Parallel requests | 1 | Memory grows with parallel × context |
| Keep loaded | `-1` (never unload) | A reload costs minutes |
| Thinking | `"low"` for code, analysis and reports. Off for extraction | The default is `xhigh`: long reasoning at about 5 tokens a second |
| Sampling | Thinking on: temperature 1.0, top_p 0.95, top_k 20. Extraction: temperature 0 with a JSON schema | Qwen's own values. Never temperature 0 with thinking on |
| Speed to expect | About 5–6 tokens a second written | See §2. A 1,500-token answer takes 4–5 minutes |

## 2. Why the 4-bit build is borderline on 24 GB

| Item | Size |
|---|---|
| GPU memory macOS allows on a 24 GB Mac | about 17.8 GiB (measured on a 24 GB M4 Pro: 17.76 GiB, 74% of RAM) |
| `qwen3.8:27b` weights (Q4_K_M) + vision projector | 17 GB + 931 MB, about 16.6 GiB |
| Context cache, per token | about 64 KiB: 8K ≈ 0.5 GiB, 16K ≈ 1 GiB, 32K ≈ 2 GiB |
| 3-bit build `UD-Q3_K_XL` | 13.1 GB, about 12.2 GiB |

So the 4-bit build plus a 16K context is already past the limit before buffers. Check
your own limit with `ioreg -l | grep recommendedMaxWorkingSetSize` (divide by 2^30).

One published test on a 24 GB Mac (M4 Pro, llama.cpp) shows what this means:

- **4-bit (Q4_K_M):** peaked at 19.6 GiB, 110% of the GPU limit. The Mac crashed with a
  kernel panic three times. Raising `iogpu.wired_limit_mb` made it worse.
- **3-bit (UD-Q3_K_XL), 16K context, quantised context cache:** ran at 16.4 GiB and
  12.6 tokens a second. It passed that author's 21 task checks and 6 tool-call checks.
- **Prompt cache:** a 4,826-token prompt took 47.9 s to read the first time and 3.4 s
  when its start was unchanged.

Two things differ on your machine:

- **Speed.** Writing speed follows memory bandwidth. The base M4 has 120 GB/s, the M4 Pro
  273 GB/s. So expect about 5–6 tokens a second (my estimate: 12.6 × 120 ÷ 273 ≈ 5.5).
  One article reports an independent test at 5–6 tokens a second for a 4-bit MLX build
  on a Mac mini M4 with 32 GB, which agrees.
- **Ollama is not raw llama.cpp.** It estimates memory and can put part of the model on
  the CPU instead of crashing, which is safe but very slow. From version 0.40 it also
  runs this model on its MLX engine on Apple silicon. I found no measurement of that
  path on a 24 GB Mac, so §3 tests it.

The guides disagree here. Unsloth and others say a 4-bit build "works on a Mac with
24 GB", and one says it fits with a 64K window. The only measured 24 GB run I found is
the crash above. Treat the 4-bit build as unproven on your Mac until §3 passes.

## 3. Install and decide (about 15 minutes plus downloads)

1. Update Ollama. Qwen 3.8 needs 0.32.12 or later; 0.40 or later uses MLX on Apple
   silicon. Check with `ollama --version`.
2. Apply the server settings in §4 and restart Ollama.
3. Save your work and close other apps. Then:
   ```bash
   ollama pull qwen3.8:27b
   ollama show qwen3.8:27b --modelfile > ~/qwen38.Modelfile   # kept for step 6
   ollama run qwen3.8:27b --think=false --verbose "Write a C# record for a purchase order line."
   ```
4. Run the three checks while the model is still loaded:

   | Check | Pass |
   |---|---|
   | `ollama ps` | PROCESSOR is `100% GPU` and CONTEXT is `16384` |
   | Activity Monitor → Memory | Pressure stays green and swap does not keep growing |
   | `eval rate` printed by `--verbose` | 4 tokens a second or more |

5. All three pass: keep `qwen3.8:27b`. It has Ollama's own template, so thinking, tools
   and images work as documented.
6. Any check fails: switch to the 3-bit build.
   ```bash
   ollama stop qwen3.8:27b
   ollama pull hf.co/unsloth/Qwen3.8-27B-GGUF:UD-Q3_K_XL
   ollama run hf.co/unsloth/Qwen3.8-27B-GGUF:UD-Q3_K_XL --verbose "Write a C# record for a purchase order line."
   ```
   Repeat the three checks, then make one more: send a request with `"think": "low"` and
   confirm the reasoning arrives in `message.thinking`, separate from `message.content`.
   **I could not test this build under Ollama.** A Hugging Face file brings its own
   template, and Ollama may not split thinking or parse tool calls with it. If that
   happens, pair the 3-bit weights with Ollama's template: in `~/qwen38.Modelfile` change
   the first `FROM` line to `FROM hf.co/unsloth/Qwen3.8-27B-GGUF:UD-Q3_K_XL`, then run
   `ollama create qwen3.8-27b-q3 -f ~/qwen38.Modelfile` and use `qwen3.8-27b-q3`. This
   step is also untested.
   When the 3-bit build works, free the disk: `ollama rm qwen3.8:27b`.
7. Do not raise `iogpu.wired_limit_mb` to force the 4-bit build in. In the 24 GB test it
   made the crash worse, and it leaves macOS too little memory.

## 4. Server settings

The Ollama app on macOS reads variables set with `launchctl`. They are lost at reboot,
so put these lines in a login script. Context length and network exposure can also be
set in the Ollama app's Settings, which keeps them.

```bash
launchctl setenv OLLAMA_CONTEXT_LENGTH 16384
launchctl setenv OLLAMA_NUM_PARALLEL 1
launchctl setenv OLLAMA_KEEP_ALIVE -1
launchctl setenv OLLAMA_FLASH_ATTENTION 1
launchctl setenv OLLAMA_KV_CACHE_TYPE q8_0
```

Quit and reopen Ollama afterwards.

| Variable | What it does | Notes |
|---|---|---|
| `OLLAMA_CONTEXT_LENGTH` | Window for callers that send no `num_ctx` | Workspace, PO extraction and the component splitter send none. Oddity sends its own window on every call. Use the same number here, in Oddity's settings and in the chat context engine, or the model reloads when you move between them |
| `OLLAMA_NUM_PARALLEL` | Requests served at once per model | 1 is the default. Each extra slot costs another full context of memory |
| `OLLAMA_KEEP_ALIVE` | How long a model stays loaded | Default is 5 minutes. A request's own `keep_alive` overrides it. Oddity sends its own on every call (its Settings board, default `-1`) |
| `OLLAMA_FLASH_ATTENTION` + `OLLAMA_KV_CACHE_TYPE` | Stores the context cache at 8 bits, about half the memory | `q4_0` is about a quarter and is what the 24 GB test used. These apply to GGUF builds. I could not confirm they affect the MLX engine |

Two settings to leave alone:

- **Do not set `OLLAMA_MAX_LOADED_MODELS=1`.** Your app also loads `nomic-embed-text`
  for document search. With a limit of 1, every embedding call unloads the 27B. After a
  document upload, `ollama ps` should list both models. If the 27B disappears whenever
  embeddings run, there is not room for both, which is one more reason for the 3-bit build.
- **Do not set `OLLAMA_HOST=0.0.0.0` unless another machine must reach Ollama directly.**
  Ollama has no login. Run the Mostisko API on the Mac and leave Ollama on `127.0.0.1`;
  then an Oddity key is the only way to the model.

Try a 32K window only on the 3-bit build, and only if `ollama ps` still shows
`100% GPU` and memory pressure stays green afterwards.

## 5. Request settings per job

| Job | `think` | temperature / top_p / top_k | Also |
|---|---|---|---|
| Code in your demo pattern | `"low"` | 1.0 / 0.95 / 20 | Rules and one or two real example files in the system prompt, identical on every call. One file per request |
| PDF analysis: questions, summaries | `"low"` | 1.0 / 0.95 / 20 | Whole text with page markers when it fits. Ask for the page after each fact |
| Information extraction | `false` | 0 | A JSON schema in `format`, and the same schema in the prompt. Ask for `null` where the document is silent |
| Report from Excel | `"low"` | 1.0 / 0.95 / 20 | Never send a big sheet. Calculate totals in code and let the model write from them |
| Plain writing, no reasoning | `false` | 0.7 / 0.8 / 20, `presence_penalty` 1.5 | Qwen's non-thinking values |

- `think` takes `false`, `"low"`, `"medium"` or `"xhigh"` for this model, and `xhigh`
  applies when it is left out. Ollama's rule is that a level must match a name the
  model reports, so confirm with `ollama show qwen3.8:27b`.
- **Never temperature 0 with thinking on.** Qwen's guidance for its thinking models is
  not to decode greedily, because it can cause endless repetition. The 3.8 card itself
  lists only the sampling values. Keep 0 for structured output with thinking off.
- The model card warns that a lower reasoning level does not always finish sooner.
  Measure `"low"` against `"medium"` on your own tasks before fixing one.

These are the starter profiles Oddity ships with (`code`, `document`, `extract`,
`report`), so a caller only names the profile.

## 6. What a 16K window holds, and what the speed means

- 16,384 minus 4,096 kept for the answer leaves about 12,000 tokens of prompt, which is
  roughly 40,000 characters of English or code.
- **PDF:** about 10–15 dense pages in one call. Longer documents need retrieval (ingest
  them in the Documents tab) or one section at a time.
- **Excel:** a 50,000-row sheet never fits, at any window this Mac can hold. The model
  must be given calculated summaries. Oddity's table digest does this.
- **Reading is slow once, then cached.** A 12,000-token prompt takes minutes on the first
  call. The next call that starts with the same text reads only the new part. So keep
  the start of the prompt byte-identical (rules, examples, the document) and put the
  question last.
- **Writing is the long part.** At 5–6 tokens a second, 300 tokens take about a minute.
  Ask for one file or one section per request, and always stream.

## 7. Measure it

The last frame of every Ollama reply carries the numbers (durations in nanoseconds):

| You want | Formula |
|---|---|
| Writing speed | `eval_count ÷ eval_duration × 1e9` |
| Share of the prompt read from cache | `prompt_eval_cached_count ÷ prompt_eval_count` |
| A reload happened | `load_duration` above about a second on a call that is not the first |
| The answer was cut off | `done_reason` is `length` |

Oddity stores these for every call and shows them on its Overview and Requests boards.

## 8. Effects on the existing app

- `backend/Program.cs:89` gives the `ollama` HTTP client 10 minutes. A PO extraction that
  does not stream can take longer than that on the 27B. Keep PO extraction on a smaller
  model, or raise that timeout.
- Chat (context engine): set this model's window to 16K there as well.
- Switching between the 27B and another chat model reloads each time. On 24 GB they do
  not fit together.

## 9. If this is too slow for daily work

The 27B on this Mac suits jobs you can wait for: a report, an extraction, one code
file. For quick back-and-forth, a 9B model is a third of the size and writes
correspondingly faster. The two cannot stay loaded together, so decide per job and
expect a reload when you switch.

## Sources

- [Qwen3.8-27B model card](https://huggingface.co/Qwen/Qwen3.8-27B): sampling values, thinking levels, context length
- [Ollama library: qwen3.8:27b](https://ollama.com/library/qwen3.8:27b) and [tags](https://ollama.com/library/qwen3.8/tags): build sizes, default parameters
- [Qwen3.8-27B on a 24 GB MacBook](https://www.kartit.net/blog/qwen38-27b-24gb-macbook): GPU limit, the 4-bit crash, the 3-bit run, cache timings
- [unsloth/Qwen3.8-27B-GGUF](https://huggingface.co/unsloth/Qwen3.8-27B-GGUF) and [Unsloth's guide](https://unsloth.ai/docs/models/qwen3.8): file sizes, memory per quant, the Ollama pull form
- [Qwen 3.8 on a Mac](https://zachrattner.com/projects/ai-mac-cluster/agentic-coding): memory bandwidth by chip, context defaults by memory
- [Qwen3.8-27B with MLX](https://www.orcarouter.ai/blog/qwen-3-8-27b-mlx): speed measured on a Mac mini M4
- [Ollama + Qwen 3.8](https://agenticwire.news/article/ollama-qwen): context cache per token, `think` levels, minimum version
- [Ollama FAQ](https://docs.ollama.com/faq), [context length](https://docs.ollama.com/context-length), [thinking](https://docs.ollama.com/capabilities/thinking), [structured outputs](https://docs.ollama.com/capabilities/structured-outputs), [chat API](https://docs.ollama.com/api/chat)
- [Ollama v0.40.0 release notes](https://github.com/ollama/ollama/releases/tag/v0.40.0) and [Ollama's MLX announcement](https://ollama.com/blog/mlx)
- [Pin num_ctx or pay the reload tax](https://bmdpat.com/blog/pin-num-ctx-local-llm-reload-tax-2026): reload on a changed window
- [Sampling issue in a project using this model](https://github.com/alex-alecu/garden-desk/issues/138): temperature 0 kept for structured JSON only
