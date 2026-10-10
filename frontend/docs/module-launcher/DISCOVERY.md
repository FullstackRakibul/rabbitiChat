# Module Launcher — Phase 0 Discovery

Read-only survey of the current frontend before the launcher replaces the tab strip.
All paths are relative to `frontend/`.

> **Blocker found up front:** `docs/module-launcher/` did not exist. The prompt's
> `reference/ModuleLauncher.vue`, `reference/LauncherPanel.vue`, `reference/AppShell.vue`,
> `reference/harness/AppLauncher.vue`, `eval/launcher-eval.mjs` and `screens/` are **not in the repo**.
> This file is the only thing in the folder so far. See "Needs KAKAROT" at the end.

---

## D1 — Importers of `ModuleTabBar.vue` and `AppSidebar.vue`

Command: grep `ModuleTabBar|AppSidebar` over `frontend/` excluding `node_modules`.

| File | Hit |
|---|---|
| `src/components/AppShell.vue:16` | `import ModuleTabBar from '@/components/ModuleTabBar.vue'` |
| `src/components/AppShell.vue:63` | `<ModuleTabBar :tabs="tabs" :model-value="activeTab" />` |
| `src/components/sidebars/ChatSidebar.vue:5` | a comment only: "This is the previous AppSidebar's content…" |

- **`ModuleTabBar.vue`** has one importer, `AppShell.vue`. It can be deleted in Phase 3 once AppShell drops it.
- **`AppSidebar.vue` is dead.** Nothing imports it. `App.vue:23-24` imports `ChatSidebar` / `WorkspaceSidebar` instead, and the only mention is a comment.
- Note: `ChatSidebar.vue:139-150` still holds its own Settings button, initials avatar and Log-out button. These duplicate the new account menu. Out of scope, so they stay.

## D2 — `useShortcuts` API (`src/composables/useShortcuts.js`)

- Signature `useShortcuts({ onPalette, onSidebar, onSubmit, onEscape, onJump, enabled })` (`:36-38`).
- The sequences are hand-rolled. `const JUMPS = { c: 'chat', w: 'workspace', d: 'docs', t: 'todo' }` (`:77`). A bare `g` arms a 1200 ms window (`:110-114`). The next key looks up `JUMPS[k]` and calls `onJump(tabId)` (`:100-107`).
- Guards: the handler is inert while typing in an input, textarea, select or contenteditable (`:95`), and when a Ctrl, Meta or Alt modifier is held (`:96`). Esc always calls `onEscape` (`:89-93`).
- App wiring is at `App.vue:827-838`, with `onJump: goTab`.
- **`g then m` is free.** `m` is not in `JUMPS`. It opens the launcher rather than a tab, so `onJump` (which takes a tab id) is the wrong channel. The plan is a new optional `onLauncher` handler, checked before `JUMPS` when `k === 'm'`. That is a small, additive change to the composable.

## D3 — `CommandPalette.vue` command shape

- Props comment (`:17`) and usage (`App.vue:785-821`): `{ id, label, group, icon?, keys?, hint?, run }`.
- Filtering matches `label`, `group` and `hint` (`CommandPalette.vue:33-40`). Selecting a command emits `run`, and App calls `cmd.run()` (`App.vue:823-825`).
- The new row goes into the `Actions` group of `commands` (`App.vue:794+`):
  `{ id: 'action:launcher', label: 'Open module launcher', group: 'Actions', icon: Grip, keys: 'g then m', run: () => launcherRef.value?.show() }`.
  The palette closes itself before `run` (`CommandPalette.vue:79-80`), so the launcher should open on `nextTick` to avoid the dialog's focus-return fight.

## D4 — Theme tokens and the dark mechanism (`src/assets/index.css`)

- Dark variant: `@custom-variant dark (&:where(.dark, .dark *));` (`:8`).
- `useTheme()` toggles `.dark` on `<html>` (`src/composables/useTheme.js:14-16`), using storage key `rabbitic-theme`.
- Light `:root` (`:12-33`) and `.dark` (`:35-55`) define `--background --foreground --card(-foreground) --popover(-foreground) --primary(-fg) --secondary(-fg) --muted(-fg) --accent(-fg) --destructive(-fg) --border --input --ring --radius`. They are bare HSL triplets, mapped to `--color-*` in `@theme inline` (`:59-83`).
- Radius: `--radius: 0.5rem`, plus `--radius-sm/md/lg/xl`.
- **Design constraint.** `--popover`, `--card` and `--background` are identical in both themes: light is `0 0% 100%` and dark is `0 0% 3.9%`. Dark `--muted` is `0 0% 14.9%`. The spec wants the dark favourites card "a step darker than the panel", but a `--popover` panel at 3.9% has no darker token below it. Two token-only options:
  1. In dark, the panel uses `--muted` (14.9%) and the card uses `--popover`/`--background` (3.9%). This matches the Google screenshot: a light-grey panel with a darker card.
  2. Derive with `color-mix(in oklab, hsl(var(--popover)) …, black)`. This stays on tokens, but the step at 3.9% would be barely visible.

  My preference is option 1. I'll confirm against the reference once it arrives.
- `tw-animate-css` 1.4.0 is present, so the `animate-in`/`zoom-in-95` utilities exist, as already used in `ui/dialog/DialogContent.vue:27`.

## D5 — Installed versions (read from `node_modules/*/package.json`)

| Package | Version | Needed bits | Present |
|---|---|---|---|
| reka-ui | **2.10.5** | `PopoverRoot/Trigger/Portal/Content`, `DialogRoot/Portal/Overlay/Content`, `DropdownMenuRoot/Trigger/Content/Item`, `TabsRoot/Content` | all ✔ (checked with a runtime `import('reka-ui')`) |
| @vueuse/core | 14.4.0 | `useMediaQuery` | ✔ |
| @lucide/vue | 1.28.0 | `Grip`, `ChevronDown`, `Pencil`, `LogOut` | ✔ |
| vue / vue-router | 3.5.39 / 4.6.4 | — | ✔ |
| tailwindcss / vite | 4.3.3 / 8.1.0 | — | ✔ |
| playwright | — | the eval runner | **✘ not installed** (rule 3: ask first) |

- There is no `ui/popover` or `ui/dropdown-menu` shadcn wrapper. `ui/` has only badge, button, card, checkbox, dialog, input, kbd, label, select, separator, switch, tabs and textarea.
- There is precedent for using reka Popover directly: `src/components/SourcesPopover.vue:4`. The launcher will do the same, so no new wrappers are needed.

## D6 — Router: DEV route placement and unknown paths (`src/app/router.js`)

- Routes are built at `:64-82`: `/` redirect, the `MODULE_ROUTES` map (`:29-45`), `/notes/:id`, `/n/:token`, then the catch-all `/:pathMatch(.*)*` → `/dashboard` (`:79`).
- **The DEV route must be inserted before the catch-all**, or it gets redirected to `/dashboard`. Plan: build the routes array, then `if (import.meta.env.DEV) routes.splice(-1, 0, { path: '/__dev/launcher', name: 'dev-launcher', component: () => import('@/dev/LauncherHarness.vue') })`. Vite statically replaces `import.meta.env.DEV` with `false` in production, so the dynamic import is dropped from the build. G3.2 will verify this.
- Guard (`:106-125`): a route without `meta.tab` passes (`:115-116`). The dev route has no `meta.tab`, so the guard never hijacks it.
- `tabForPath` (`:53-55`) returns `null` for unknown paths, and `App.vue:385` turns that into `'dashboard'`.
- **Important: `App.vue` has no `<RouterView>`.** `main.js:6` mounts `App`, and App renders panels from the route itself. Only `isPublicNote` (`App.vue:392`, `:844`) takes a separate branch. A route `component` is therefore never rendered by itself, and the harness needs one of these:
  - (a) an `App.vue` branch, `<RouterView v-if="isDevHarness" />`, in the same style as `isPublicNote`. This is a one-line template plus a one-line computed.
  - (b) `main.js` mounts the harness app instead of `App` when `import.meta.env.DEV && location.pathname.startsWith('/__dev/')`. This keeps App's `bootstrap()`, permission fetch and `router.replace` from running around the harness, but it means G1.3 touches `main.js` too.

  Either way, G1.3 ("only launcher files, harness, router DEV branch") **cannot hold literally**. One more small file must change. I recommend (b): the harness gets no login gate, shortcuts or permission refresh interfering, and the App.vue diff in Phase 2 stays clean.
- **Existing quirk (noticed, not changed):** `/notes/:id` has `meta.tab: 'notes'`, but `tabForPath('/notes/5')` returns `null`, so `activeTab` shows **Dashboard** on a note-detail URL. The new chip will inherit this and show "Dashboard" there. Fixing it would mean `tabForPath` honouring `route.meta.tab`, which is a router change and outside this shell-only task.

## D7 — Names in `App.vue` to pass to `AppShell`

| Need | Name | Line |
|---|---|---|
| username | `username` (ref, from `getUsername()`) | `App.vue:49` |
| initials | `initials` (computed) | `App.vue:51-52` |
| role | `myRole` (ref, from `/api/me/tabs`) | `App.vue:422`, set `:450` |
| user id | `myUserId` (ref, `0` until known) | `App.vue:424`, set `:452` |
| admin flag | `isSystemAdmin` | `App.vue:423` |
| permission set | `allowedTabs` (`null` = loading) | `App.vue:421` |
| filtered modules | `tabs` (computed, `[]` while loading) | `App.vue:427-430` |
| catalogue | `ALL_TABS` | `App.vue:400-416` |
| navigate | `goTab(id)` (refuses forbidden ids) | `App.vue:769-777` |
| log out | `logout()` (resets `allowedTabs`, `myUserId`…) | `App.vue:81-95` |
| theme | `theme`, `toggleTheme` | `App.vue:45` |
| palette | `paletteOpen`, `commands` | `App.vue:744`, `:785` |
| shell mount | `<Tabs v-model="activeTab" :unmount-on-hide="false">` / `<AppShell …>` | `App.vue:854-858` |

Notes for wiring:
- **Loading state.** `tabs` is `[]` both while loading and when the fetch failed (`allowedTabs = []`, `:465`). The launcher cannot tell those apart from `tabs` alone, so AppShell needs an explicit `loading` prop (`allowedTabs === null`).
- **Storage-key race.** `myUserId` is `0` before `/api/me/tabs` answers and after logout. The launcher must not read or write `mostisko.launcher.v1:0`; it should wait for a non-zero `userKey`. Otherwise G2.7 (separate users) can leak defaults across accounts.
- `logout()` does not reset the route, so after re-login the URL from the previous user is kept and re-checked by `refreshPermissions` (`:458-460`). That is fine for the chip.

---

## G0.2 — Files to create, edit, delete

**Create**
- `src/components/launcher/ModuleLauncher.vue`
- `src/components/launcher/LauncherPanel.vue`
- `src/dev/LauncherHarness.vue` (DEV only)
- `docs/module-launcher/launcher-report.md` (eval output, Phase 1/3)

**Edit**
- `src/components/AppShell.vue`: new header (brand · chip · search · status · theme · launcher · avatar menu), new props `username, initials, role, userId, loading` and emits `launch, logout, open-settings`. Sidebar block unchanged.
- `src/app/router.js`: DEV-only `/__dev/launcher` inserted before the catch-all.
- `src/main.js` **or** `src/App.vue`: harness mount branch (see D6, recommend `main.js`).
- `src/composables/useShortcuts.js`: optional `onLauncher` for `g then m` (additive).
- `src/App.vue` (Phase 2): AppShell props and events, a `launcherRef`/`launcherOpen`, the palette "Open module launcher" row, and `onLauncher` in `useShortcuts`.

**Delete (Phase 3)**
- `src/components/ModuleTabBar.vue`: sole importer `AppShell.vue:16`, removed in Phase 2.
- `src/components/AppSidebar.vue`: zero importers (D1).

---

## Needs KAKAROT

1. **The reference bundle is missing.** Please copy `reference/*.vue`, `reference/harness/AppLauncher.vue`, `eval/launcher-eval.mjs` and `screens/` into `frontend/docs/module-launcher/`. Without them, Phase 1 would be a fresh implementation of §4 rather than a port, and G1.1 (24/24) and G1.2 (screenshot equivalence) cannot be measured.
2. **Playwright.** It is not installed. May I add it as a devDependency (plus `npx playwright install chromium`) to run the eval?
3. **Harness mount (D6).** May `main.js` (recommended) or `App.vue` get the small DEV-only branch, given G1.3's file list?
4. **Dark panel colour (D4).** Is it OK for the dark panel to use `--muted` with the favourites card on `--popover`, so the card is darker than the panel?
