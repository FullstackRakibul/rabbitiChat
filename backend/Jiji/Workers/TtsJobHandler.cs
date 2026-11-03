// Jiji — the Tts job, through the speech provider in Jiji → Settings. One WAV per spoken part
// (hook, each beat, myTake, CTA) in {contentId}/r{revision}/tts/, each measured from its real
// data size; every part in the first part's format (Kokoro: 24 kHz mono); then narration.wav,
// the parts joined with a fixed gap, and timings.json, every part's start and end in
// narration.wav. timings.json is written last: its presence means the narration is whole.
// A rerun (retry, reclaimed lease) starts the folder over, so it never mixes two runs.

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitIC.Api.Data;
using RabbitIC.Api.Jiji.Dtos;
using RabbitIC.Api.Jiji.Models;
using RabbitIC.Api.Jiji.Services;

namespace RabbitIC.Api.Jiji.Workers;

public sealed record JijiNarrationPart(string Id, string Kind, int? BeatId, string Text, string File,
    double StartMs, double EndMs, double DurationMs);

public sealed record JijiNarration(int Revision, int SampleRate, double GapMs, double TotalMs, IReadOnlyList<JijiNarrationPart> Parts)
{
    public const string FileName = "timings.json";
    public const string AudioName = "narration.wav";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

public sealed class TtsJobHandler(AppDbContext db, TtsService tts, JijiStorage storage,
    ILogger<TtsJobHandler> log) : IJijiJobHandler
{
    public string Type => JijiJobType.Tts;

    public async Task RunAsync(JijiClaimedJob job, CancellationToken ct)
    {
        var c = await db.JijiContents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == job.ContentId, ct)
            ?? throw new JijiSupersededException("The item no longer exists.");
        if (c.Revision != job.ContentRevision || c.Status != JijiStatus.ScriptApproved)
            throw new JijiSupersededException($"The item is now revision {c.Revision}, {c.Status}.");
        var script = JijiScript.TryParse(c.ScriptJson ?? "", out var parseError)
            ?? throw new JijiJobException($"The approved script cannot be read: {parseError}", transient: false);

        var spoken = Parts(script);
        // Settings first: a missing provider or voice fails before any file is touched.
        var voice = await tts.ChooseAsync(ct);
        var dir = storage.RevisionDir(c.Id, job.ContentRevision);
        var partsDir = storage.Resolve($"{c.Id}/r{job.ContentRevision}/tts");
        if (Directory.Exists(partsDir)) Directory.Delete(partsDir, recursive: true);
        Directory.CreateDirectory(partsDir);
        File.Delete(Path.Combine(dir, JijiNarration.FileName));

        var audio = new List<(byte[] Bytes, JijiWavInfo Info)>();
        var parts = new List<JijiNarrationPart>();
        double gapMs = 0;
        double at = 0;
        foreach (var (id, kind, beatId, text) in spoken)
        {
            var wav = await tts.SynthesizeAsync(voice, text, ct);
            var file = storage.Resolve($"{c.Id}/r{job.ContentRevision}/tts/{id}.wav");
            await File.WriteAllBytesAsync(file, wav, ct);

            // The saved file's real data size, not its header's size field or the service's estimate.
            var saved = await File.ReadAllBytesAsync(file, ct);
            JijiWavInfo info;
            try { info = JijiWav.Read(saved); }
            catch (InvalidDataException e)
            {
                throw new JijiJobException($"{voice.Target.Name} returned unusable audio for {id}: {e.Message}", transient: false, e);
            }
            if (info.DataBytes == 0) throw new JijiJobException($"{voice.Target.Name} returned no audio for {id}.", transient: true);
            // Mismatched audio is never joined: the job fails and says which part differs.
            if (audio.Count > 0 && JijiWav.FormatMismatch(audio[0].Info, info) is { } why)
                throw new JijiJobException($"{voice.Target.Name} returned {id} as {why}. Parts of different formats are not joined; check the provider's settings, then retry.", transient: false);
            if (audio.Count == 0) gapMs = JijiWav.GapMsExact(info, voice.GapMs);

            if (audio.Count > 0) at += gapMs;
            parts.Add(new JijiNarrationPart(id, kind, beatId, text, $"tts/{id}.wav", Round(at), Round(at + info.DurationMs), Round(info.DurationMs)));
            at += info.DurationMs;
            audio.Add((saved, info));
        }

        var narration = Path.Combine(dir, JijiNarration.AudioName);
        var temp = narration + ".part";
        JijiWav.Join(temp, audio, voice.GapMs);
        File.Move(temp, narration, overwrite: true);

        // The join's own header must agree with the sum of the parts.
        var joined = JijiWav.Read(await File.ReadAllBytesAsync(narration, ct));
        if (Math.Abs(joined.DurationMs - at) > 1)
            throw new JijiJobException($"narration.wav is {joined.DurationMs:0} ms; the parts add up to {at:0} ms.", transient: false);

        var timings = new JijiNarration(job.ContentRevision, joined.SampleRate, Round(gapMs), Round(joined.DurationMs), parts);
        await File.WriteAllTextAsync(Path.Combine(dir, JijiNarration.FileName), JsonSerializer.Serialize(timings, JijiNarration.Json), ct);
        log.LogInformation("Jiji: narration for item {Id} r{Revision}: {Parts} parts, {Seconds:0.0} s, {Provider} voice {Voice}.",
            c.Id, job.ContentRevision, parts.Count, joined.DurationMs / 1000, voice.Target.Name, voice.Voice);
    }

    /// <summary>What is said, in order. On-screen code and list items are not read aloud.</summary>
    public static IEnumerable<(string Id, string Kind, int? BeatId, string Text)> Parts(JijiScript s)
    {
        if (s.Hook.Trim() != "") yield return ("hook", "hook", null, s.Hook.Trim());
        foreach (var b in s.Beats.Where(b => b.Text.Trim() != ""))
            yield return ($"beat-{b.Id}", "beat", b.Id, b.Text.Trim());
        if (s.MyTake.Trim() != "") yield return ("mytake", "myTake", null, s.MyTake.Trim());
        if (s.Cta.Trim() != "") yield return ("cta", "cta", null, s.Cta.Trim());
    }

    private static double Round(double ms) => Math.Round(ms, 1);
}
