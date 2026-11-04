# Jiji — Final Build Plan

Nov 1, 2026 · KAKAROT

Jiji is a Mostisko module that turns engineering notes, books and thoughts into English YouTube Shorts. You approve each one, and it schedules them. Week one ships the pipeline and the first 3 Shorts. Money comes from a resource page, affiliate links and developer-tool sponsors, not from Shorts ads.

## Name and logo

- "Jiji" is the internal module name only.
- Never use Studio Ghibli's artwork of the character, anywhere (copyright and trademark risk). Use an original icon (a lucide icon for now).
- The public channel name is not "Jiji" and has no black-cat mascot. The public brand is your own (e.g. "Build Better").

## Content strategy

English-first: practical software engineering and applied AI, books as a supporting series. Your own projects are the source material.

| Pillar | Share | Topics |
|---|---|---|
| Real-world software engineering | 45% | Backend, APIs, databases, auth, background jobs, production bugs |
| AI engineering | 35% | Ollama, RAG, embeddings, gateways, local vs cloud models, automation |
| Books and engineering thinking | 20% | Ideas from books and systems thinking, applied to software |

First 12 Shorts (30-day test, 3 per week):

1. Your API returned 200. Why did the data never save?
2. Why I store API keys as hashes, not plaintext
3. What actually happens when you send an HTTP request
4. Why your background jobs sometimes run twice
5. SQLite is more capable than you think, but know its limits
6. RAG is not just vector search
7. Why I built my own Ollama-compatible API gateway
8. Embeddings explained without the maths
9. Local LLM vs cloud LLM: what changes in production
10. Why your AI app needs source citations
11. Clean Architecture: when it helps and when it is overengineering
12. What a systems-thinking book teaches about software architecture

Never show employer code or data, credentials, private infrastructure or customer information. Use sanitised examples.

After 30 days, measure: viewed vs swiped away, average percentage viewed, subscribers per 1,000 views, shares and comments, repeated questions. Judge each pillar by several videos.

## Earning

| Path | Starts | Effort |
|---|---|---|
| Resource page (public page in Mostisko) linked from the channel's links | Week 2 | Half a day |
| Affiliate links on that page (programs that pay out to Bangladesh) | Week 2–3 | Sign-ups only |
| Sponsorships: one-page media kit to developer-tool companies | After the 30-day test | A few hours |
| Monthly long-form video expanding the best Short | Month 2 | One video a month |
| YouTube ads | When eligible | None |
| Templates / mini course, Bangla versions of proven topics | Month 3+ | Only after demand shows |

## Channel

@RabbiticProduction, cleaned and rebranded. Before the first upload:

- [ ] Confirm no active copyright strike from the 5 removed videos
- [ ] Change name, handle, picture, banner and description to the public brand
- [ ] Remove old links and playlists
- [ ] Post one Community post about the new direction

If the first 12 Shorts show the music audience holds them back, move to a new channel. Jiji publishes to whichever channel is connected.

## Scope

| Area | Decision |
|---|---|
| Home | A module inside Mostisko (ASP.NET Core 8 minimal API + Vue 3 SPA) |
| Language | English first; code supports bn, Bangla videos wait |
| Database | The existing SQLite file, 3 new tables through SchemaUpgrade |
| Jobs | One BackgroundService with leases and retries |
| LLM | ChatService / ModelSourceRegistry; default chosen on day 2 |
| Voice | An OpenAI-compatible speech provider from Settings → Model providers (Kokoro-FastAPI, local), one English voice chosen in Jiji → Settings |
| Video | One template rendered by FFmpeg + ASS subtitles inside Mostisko |
| Captions | Line by line, timed from each beat's audio length |
| Publishing | YouTube Data API v3, private + publishAt, verified by read-back |
| Frontend | The existing Vite build, no React |

Not in week one: Remotion, extra templates, Whisper, text-to-video AI, other platforms, analytics, the resource page, long-form, Bangla videos.

## Architecture

Everything runs inside Mostisko. The worker calls the LLM, the speech provider, FFmpeg and YouTube in turn; Mostisko alone holds the credentials.

```
backend/Jiji/
  Models/        JijiContent, JijiJob, JijiYouTubeAccount
  Dtos/          IdeaRequest, ScriptDto, ApproveVideoRequest
  Services/      IdeaGenerator, ScriptGenerator, TtsService, VideoRenderService, StorageService
  Publishing/    IVideoPublisher, YouTubePublisher, YouTubeAuthService
  Workers/       JijiWorker
  JijiApis.cs
  JijiModule.cs   // AddJijiModule(config) + ApplicationJijiApis()
```

### Tables

No foreign keys to other modules; ownership is the user id only.

- **JijiContents**: Id, OwnerId, ContentType (TechExplainer/BuildLog/BookInsight/OriginalThought), Subject, Language, Angle, SourceNotes, ScriptJson (the only home of myTake), Revision (raised on every script approval), Status, Title, Description, TagsJson, PublishAtUtc, ContainsSyntheticMedia, MadeForKids, VideoPath, UploadToken (internal only), YouTubeVideoId, RejectReason, LastError, CreatedAt, UpdatedAt
- **JijiJobs**: Id, ContentId (FK to JijiContents), ContentRevision, Type (Tts/Render/Upload), Status (Pending/Running/Done/Failed/UnknownOutcome), Attempts, AvailableAtUtc, LeaseUntilUtc, LeasedBy, LeaseToken, UploadSessionUriProtected, StartedAtUtc, CompletedAtUtc, LastError. Unique index on (ContentId, Type, ContentRevision)
- **JijiYouTubeAccounts**: OwnerId (key), ChannelId, ChannelTitle, RefreshTokenProtected, ConnectedAt, OAuthStateHash, OAuthStateExpiresAtUtc, OAuthCodeVerifierProtected

Status flow: idea → script_draft → script_approved → voiced → rendered → video_approved → scheduled → published. The publication check sets published, not the upload. A failure sets LastError and keeps the last good status. A rejection goes back to script_draft with a reason.

### Endpoints (/api/jiji, session token, jiji tab)

| Method | Path | Does |
|---|---|---|
| GET | /status | What is configured (day 1) |
| POST | /ideas | 5 angles from a subject, content type and notes |
| GET | /contents?status= | Queue list (day 1) |
| POST | /contents | Create the item once an angle is picked (day 2, not in the original list) |
| GET | /contents/{id} | One item with its script, jobs and approval check (day 1) |
| PUT | /contents/{id} | Edit subject, angle, notes before approval; 409 when stale (day 2, not in the original list) |
| DELETE | /contents/{id} | An idea or draft with no queued job (day 2, not in the original list) |
| POST | /contents/{id}/script/generate | LLM writes the script JSON |
| PUT | /contents/{id}/script | Save edits (409 on a stale expectedUpdatedAt) |
| POST | /contents/{id}/approve-script | Requires originalAngle and myTake, then queues TTS (render follows) |
| GET | /contents/{id}/audio, /contents/{id}/video | Stream files for preview (audio: day 3) |
| GET | /contents/{id}/timings | Every spoken part's start and end in narration.wav (day 3) |
| POST | /contents/{id}/approve-video | Title, description, tags, publishAtUtc, made-for-kids and synthetic-media answers, then queues Upload |
| POST | /contents/{id}/reject | Back to the script with a reason |
| POST | /jobs/{jobId}/retry | Re-queue a Failed job. Refused for UnknownOutcome |
| POST | /contents/{id}/resolve-upload | Manual, verified fix for UnknownOutcome |
| GET | /youtube/status, /youtube/connect | Connected channel; start OAuth |

The OAuth callback (/public/jiji/youtube/callback) sits outside /api (Google's redirect carries no bearer token). A one-time server-side state value ties it to the user.

### Script JSON

```json
{
  "contentType": "TechExplainer",
  "language": "en",
  "hook": "Your API returned 200. The data never saved.",
  "originalAngle": "An HTTP response is not a committed database transaction.",
  "beats": [
    { "id": 1, "text": "A 200 response does not guarantee a database commit.", "visual": "text" },
    { "id": 2, "text": "This line is where the data is really saved.", "visual": "code", "code": "await db.SaveChangesAsync();" }
  ],
  "myTake": "A sanitised example from my own work.",
  "cta": "Follow for practical software engineering.",
  "title": "...", "description": "...",
  "hashtags": ["dotnet", "backend", "softwareengineering"]
}
```

35–55 seconds, about 90–130 English words. originalAngle and myTake are required before approval. visual is text, code (at most 4 lines) or list (with `items`, 2–5 on-screen phrases). A code beat may only contain a snippet pasted into SourceNotes, never generated code. The prompt builds only from SourceNotes and must not invent facts. Malformed JSON is refused and never saved. A context source named "Jiji voice" holds the writing style.

## Worker and files

JijiWorker: one worker, concurrency 1, polls every 5 seconds, runs Tts → Render → Upload. Each job rule is a single conditional UPDATE:

- **Claim**: Pending → Running only WHERE Status = 'Pending' AND AvailableAtUtc <= now; sets LeasedBy (worker id made at app start), a new random LeaseToken, LeaseUntilUtc = now + 2 min, StartedAtUtc. Continue only if exactly 1 row changed.
- **Heartbeat**: every 30 s, extend LeaseUntilUtc only WHERE Id = @id AND LeaseToken = @token.
- **Complete or fail**: only WHERE Id = @id AND LeaseToken = @token. 0 rows → reclaimed, drop the result.
- **Next job**: inserted idempotently through the unique (ContentId, Type, ContentRevision) index.
- **Expired**: Running → Pending when LeaseUntilUtc < now, at startup and every poll. An expired Upload with a saved session goes through the session check instead.
- **Retries**: Tts and Render back off 1, 5, 30 minutes, then Failed.
- **Publication check**: at startup and every 15 minutes, read back each scheduled item; privacyStatus == public → published.

TTS job: one WAV per beat (hook, beats, myTake, CTA) through SSML, all Riff24Khz16BitMonoPcm. Measure each file's real duration from its header (data bytes ÷ byte rate). Save durations, compute cumulative times, join into narration.wav, build the ASS events from those times.

Files: /data/jiji/{contentId}/. Keep final.mp4 and the script. Delete beat WAVs, narration.wav, the .ass file and intermediates after a successful render. At 70% volume, extend it or copy old MP4s off.

Settings (Jiji → Settings, stored in JijiSettings; SystemAdmin edits): script model and source, speech provider (a Model providers key with the speech capability), speech model (kokoro), voice. Config fallbacks: Jiji:Model, Jiji:ModelSource, Jiji:Speech:{Provider, Model, Voice, Speed, GapMs}. Also Jiji:Worker:{Enabled, PollSeconds, LeaseSeconds, HeartbeatSeconds}, Jiji:YouTube:ClientId, ClientSecret (env), Jiji:StoragePath, Jiji:FfmpegPath, Jiji:FontsDir. The speech provider's URL and key live in Model providers.

## Renderer (FFmpeg inside Mostisko)

1. Background: static dark gradient. zoompan only after the day-4 memory gate passes.
2. Text beats: ASS events with fades, movement and highlighted words.
3. Code beats: drawbox panel, monospace snippet, keywords coloured with ASS colour tags.
4. Cards: title, myTake and end card as ASS styles.
5. Progress bar: drawbox whose width grows with time.
6. Audio and encode: narration + optional Audio Library music, -14 LUFS, H.264 -preset veryfast -crf 20, AAC 192k, 1080×1920 at 30 fps.

Setup: ffmpeg, fontconfig, fonts-noto-core and a monospace font in the Docker runtime stage; fontsdir; check `ffmpeg -filters` lists subtitles and `fc-list` finds the fonts.

Safety:

- Escape script text without removing characters: literal braces as `\{` `\}`, a word joiner (U+2060) after every backslash, line breaks written as `\N` by the renderer. Model output can never create an override.
- ProcessStartInfo.ArgumentList, never a shell string.
- 10-minute timeout; on timeout or cancel Kill(entireProcessTree: true).
- Every path resolved and refused outside Jiji:StoragePath.
- Render test strings: `{formatting}`, `C:\Users\Developer`, `C:\new\Notes`, `\SaveChangesAsync()`, `"quoted text"` and a multi-line snippet appear exactly as typed.

Day-4 gate: on the real Fly machine, render one 55-second Short with API and worker running; measure total machine peak memory. `-threads 2 -filter_threads 1 -filter_complex_threads 1`. Pass: peak under ~700 MB, render under 5 minutes, API responsive. Fail → 2 GB.

## Frontend

```
frontend/src/
  components/tabs/JijiTab.vue        sub-nav shell (boards mount on first open, like Oddity)
  components/jiji/
    QueueBoard.vue        list by status, filter chips, "New" button
    NewIdeaDialog.vue     content type, subject, notes -> 5 angles to pick
    ScriptEditor.vue      hook, beats (text/code/list), myTake (required), CTA
    VideoReview.vue       video preview, title, description, tags, publish time
    JijiSettings.vue      YouTube connect, voice
  services/jiji.service.js   every call goes through http.js
```

- Wiring: `jiji` in PermissionCatalog (→ TabCatalog.All), TabAccessMiddleware's prefix map, MODULE_ROUTES (`/jiji`) and App.vue (Work group). Shortcut: g then j.
- Preview: fetch the file through http.js as a blob, play it with URL.createObjectURL, revoke on unmount.
- Live status: poll GET /contents/{id} every 5 s while a job runs.
- Approve stays disabled until myTake has text. LastError shows in a red card with Retry. Publish times show in Asia/Dhaka, sent in UTC.

## YouTube publishing

1. Google Cloud project, YouTube Data API v3, a Web OAuth client with redirect `https://<host>/public/jiji/youtube/callback`.
2. Scopes: youtube.upload and youtube.readonly.
3. Connect: random state + PKCE verifier; store the state's hash, a 10-minute expiry and the protected verifier. access_type=offline, prompt=consent. Callback checks hash and expiry, then clears it (works once). Protect and store the refresh token.
4. Testing-mode refresh tokens expire after 7 days: publish the consent screen once the flow works; until then Settings shows Reconnect and the worker warns a day early.
5. Data Protection (purpose Jiji.YouTube) for the refresh token, code verifier and session URIs; key ring on /data (PersistKeysToFileSystem). All of them and the Authorization header go into the Redactor.

Upload: title, description, tags, categoryId "28", language en; privacyStatus private, publishAt UTC ≥ 15 minutes ahead; selfDeclaredMadeForKids and containsSyntheticMedia from the content's flags (asked per video).

Safe recovery:

1. Start a resumable session; save its URI on the job, encrypted, before sending bytes.
2. Send the bytes; on success save YouTubeVideoId immediately.
3. If interrupted, empty PUT with `Content-Range: bytes */<total>`: 200/201 → finished, store the id; 308 → resume from the reported Range; 404 → expired; if uncertain, UnknownOutcome (never retried automatically).
4. resolve-upload: a pasted id is checked (exists, connected channel, intended title, private with the right publishAt, or fixed via videos.update); "never uploaded" starts a fresh session.

UploadToken stays internal. On quotaExceeded, retry after midnight Pacific.

Day-6 test: upload one test video as unlisted; check Studio for an API-upload private lock. If locked, submit the API audit and use "Download MP4 + copy metadata" until it clears. Delete the test video.

## 7-day plan

| Day | Build | Done when |
|---|---|---|
| 1 | Module skeleton, 3 tables, jiji tab, empty shell, Google Cloud project, channel checklist, notes for the first 12 topics | Jiji opens and the rest of Mostisko still works; notes for 12 topics exist |
| 2 | IdeaGenerator, ScriptGenerator, NewIdeaDialog, ScriptEditor; test 3 models on one topic | 3 usable English scripts with an angle and your take; default model chosen |
| 3 | TtsService, JijiWorker with atomic claims and lease fencing | Correct narration and per-beat timings |
| 4 | VideoRenderService, FFmpeg and fonts on Fly | One 1080×1920 MP4, day-4 memory gate passed |
| 5 | QueueBoard, VideoReview, reject flow | Video approved in the UI; first Short uploaded by hand and live |
| 6 | OAuth, encryption, resumable uploader with recovery, publication check | A scheduled API upload verified in Studio |
| 7 | Topics 2–4 end to end; failure drills | 3 Shorts scheduled (Mon/Wed/Fri, 20:00 Dhaka) |

## Implementation tracker

Backend
- [x] Register Jiji through existing module conventions (day 1)
- [x] Create 3 SQLite tables through SchemaUpgrade (day 1)
- [x] Integrate RBAC and TabAccessMiddleware (day 1)
- [x] Implement idea and script generation (day 2)
- [x] Add script validation and optimistic concurrency (day 2)

Worker
- [x] Atomic job claiming and lease fencing (day 3)
- [ ] TTS → Render → Upload orchestration (day 3: Tts runs and queues Render; the Render and Upload handlers arrive on days 4 and 6)
- [x] Heartbeat and expired-lease recovery (day 3)
- [x] Idempotent job creation (day 3: INSERT OR IGNORE on the unique index)
- [x] Retry classification and UnknownOutcome handling (day 3: transient/permanent, retry endpoint refuses UnknownOutcome; resolve-upload is day 6)

Media
- [x] English TTS through the speech provider (POST /audio/speech, WAV; Kokoro-FastAPI). Tested against a Kokoro stand-in; real Kokoro still to confirm
- [x] Measure actual beat WAV durations (day 3)
- [ ] Generate ASS subtitles safely
- [ ] FFmpeg static-gradient template
- [ ] Benchmark full-container memory and render time

YouTube
- [ ] OAuth with persisted one-time state
- [ ] Persist Data Protection key ring
- [ ] Encrypt refresh token and upload URI
- [ ] Resumable upload and recovery
- [ ] Scheduled publication and reconciliation

Frontend
- [x] Jiji launcher tile and route (day 1)
- [x] Queue and content editor (day 2; review and reject on day 5)
- [x] OriginalAngle and MyTake approval (day 2: approval queues the TTS job)
- [ ] Authenticated media preview
- [ ] Video approval and scheduling UI

Release
- [ ] Publish one manually approved Short
- [ ] Test automatic scheduled publication
- [ ] Run render and network failure drills
- [ ] Verify log redaction
- [ ] Schedule three subsequent Shorts

## Channel rules and open questions

Rules: every video carries your own real example; 3 Shorts a week during the test; sanitise every snippet; YouTube Audio Library music and original artwork only.

Open: strike check (day 1); public channel name; best script model (day 2); whether API uploads are audit-locked to private (day 6); which affiliate programs pay out to Bangladesh (week 2).
