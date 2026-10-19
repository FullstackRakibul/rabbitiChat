# Mostisko — Access: role presets for every tab, section and action, editable in the UI

### Evaluation-based build prompt for Claude Code

> **How to start.** Put this file at `docs/RBAC_ROLES_PROMPT.md` and tell Claude Code:
>
> *"Follow `docs/RBAC_ROLES_PROMPT.md`. Do **Phase P1** only, then stop and give me the Phase P1 report."*
>
> After each report: *"Continue with Phase P2."* Never let it run several phases in one go.

---

## 0. Rules of engagement

1. **One phase at a time.** Each phase ends with the report in §8. Stop and wait.
2. **A gate is PASS only with evidence**: eval output, a command and its output, a file path and line, or a screenshot. After three failed attempts on one gate, stop and report the blocker.
3. **Evals first.** Write `scripts/rbac-eval.mjs` and each phase's checks before the code. Run them once while they fail and paste that line. Reuse the shared fake Ollama and API start-up.
4. **The server is the control.** The UI hides what a user can't use, but every route checks access itself, through one function. A second list of roles, tabs or permissions in the frontend is an automatic FAIL. The frontend reads everything from the API.
5. **Nothing changes for anyone on upgrade.** The seeded presets equal today's hard-coded baselines and today's hard-coded capability checks. Every existing user's effective access must be identical before and after the migration (G-P1.2 proves it).
6. **One rule, one place.**
   - Effective access = role preset + user overrides, worked out in one service (`AccessPolicy`) and cached, with the cache cleared on every change.
   - `TabAccessMiddleware`, `ModelPolicy`'s callers, and every capability check (HR library editing, Rulebook confirm, Procedures upload, …) call it.
7. **Safety invariants, enforced by the server:**
   - SystemAdmin always has everything and can't be reduced;
   - `settings` is always on for every role (it holds the account and log-out);
   - at least one active SystemAdmin user must exist;
   - an admin can't remove their own access to Access.
8. **No new packages.** Do not push, merge to `main` or open a pull request. Work on a branch `rbac-presets` from the current `hr-knowledge`; commit at the end of each phase.
9. Re-confirm every fact in §2 and report what changed.

---

## 1. What we are building

Today each role's tabs are written in code (`TabCatalog`), and capabilities such as "can edit the HR library" or "can upload procedures" are written as role checks in each service. Access lets an admin add tabs to one user, shown in the screenshot as unlocked rows beside locked "ROLE" rows, but nobody can change what a **role** gets without a code change.

**After this build:**
- **Roles page.** A matrix of every role against every tab. A SystemAdmin ticks what each role gets, expands a tab to set its **sections** (for example HR's Ask, Library, Rulebook and Procedures) and **actions** (for example *edit library*, *confirm Rulebook page*, *upload procedure*), and saves. Every user of that role gets the change on their next request.
- **User page.** Each tab, section and action has three states: **Role default · Allow · Deny**. So one user can get more or less than their role. The locked "ROLE" rows become "Role default", showing the role's value.
- **"View as".** A SystemAdmin can see exactly what any user or role sees (tabs, sections, actions, models) without logging in as them.
- **History.** Every change is logged: who, what, before → after, when, and an optional note.

| Phase | Work | Why in this order |
|---|---|---|
| **P1** | The permission catalog, role presets in the database (seeded from today's code), one `AccessPolicy` service, server enforcement switched to it, role tab presets editable | Moving the rules from code to data must change nothing first |
| **P2** | Sections and actions: HR, PO Studio and Access capabilities as permission keys; per-user Allow/Deny; the Roles and User pages | Needs P1's single policy |
| **P3** | "View as", the change log page, export and import of presets, live check | Admin comfort and safety once the model is right |

**Not in this build:** creating new roles from the UI (roles stay in code), per-record permissions (for example unit scoping), and time-limited grants.

---

## 2. Established facts (re-confirm, do not assume)

| # | Fact | Evidence |
|---|---|---|
| P-F1 | Roles: General, HrAssistant ("hr-assistant"), DevOps, System, GroundControl, SystemAdmin; normalised in one place; unknown values → General | `backend/Models/Rbac.cs` |
| P-F2 | Tab keys, labels and each role's baseline are in code; grants are additive and stored only when beyond the baseline | `backend/Rbac/TabCatalog.cs`, `backend/Services/RbacService.cs` |
| P-F3 | `TabAccessMiddleware` maps route prefixes to tab keys and returns 403 | `backend/Middleware/TabAccessMiddleware.cs` |
| P-F4 | Per-role model policy is already data: `RoleModelPolicies`, seeded per role, cached, editable in Access → Models by role | `backend/Rbac/ModelPolicy.cs` |
| P-F5 | Capabilities are role checks in services, for example: HR library editors (`ContextSourceService.CanEditShared`), Rulebook editors, Procedures upload (DevOps, SystemAdmin), PO Studio profile editing | `backend/Hr/*`, `backend/Services/*` |
| P-F6 | Access shows a role picker and the user's tabs with locked "ROLE" rows and grantable rows | `frontend/src/components/tabs/RbacTab.vue` |
| P-F7 | The module switcher and the System menu read the user's tabs from `/api/me/tabs` | the System menu change |
| P-F8 | Existing suites assert role baselines (for example General's six tabs in `oddity-eval` and `model-policy-eval`) | `scripts/*-eval.mjs` |

**Find the full list yourself.** Before P1, `git grep` for role checks (`Role ==`, `IsInRole`, `RbacRoles.`, `CanEdit`, `IsSystemAdmin`, `HrAssistant`, `DevOps`) across `backend/`, and paste every place a role decides access. The gates check against your list.

---

## 3. The design contract

### 3.1 Permission catalog (code, the only list of what exists)
`backend/Rbac/PermissionCatalog.cs` declares every permission key once, with its label (English and Bengali), its group, and its parent:

```
tab:<key>                     e.g. tab:chat, tab:hr, tab:workspace, tab:access
section:<tab>.<section>       e.g. section:hr.ask, section:hr.library, section:hr.rulebook, section:hr.procedures
action:<area>.<verb>          e.g. action:hr.library.edit, action:hr.rulebook.edit, action:hr.rulebook.confirm,
                                   action:hr.procedures.upload, action:hr.procedures.accept-rules,
                                   action:hr.lookups.edit, action:po.profiles.edit, action:access.manage
```

- **Parents:** a section needs its tab; an action needs its section (or its tab when it has no section). A child granted without its parent has no effect, and the UI shows why.
- **Groups** follow the module menu: Favourites (not a group), Work, Tools, System.
- **The catalog** is served at `GET /api/access/catalog`. The frontend draws everything from it.

### 3.2 Data
| Table | Columns | Notes |
|---|---|---|
| `RolePermissions` | `Role`, `Key`, `Allowed` (bool), `UpdatedByUserId`, `UpdatedAtUtc` | One row per role and key. Seeded on start only for missing rows, from today's `TabCatalog` baselines and today's capability checks (§2 list) |
| `UserPermissionOverrides` | `UserId`, `Key`, `Effect` (`allow`/`deny`), `UpdatedByUserId`, `UpdatedAtUtc`, `Note` | Existing tab grants migrate here as `allow` rows |
| `AccessChangeLog` | `AtUtc`, `ByUserId`, `Scope` (`role`/`user`), `Target`, `Key`, `Before`, `After`, `Note` | Append-only (SQLite trigger, as for Rulebook versions) |

Create them in `SchemaUpgrade` and in the EF model path (fresh database).

### 3.3 Effective access (one function)
```
effective(user, key) =
    SystemAdmin                              → allowed
    key is tab:settings                      → allowed
    user override deny                       → denied
    user override allow                      → allowed
    otherwise                                → RolePermissions[role, key]
    …and then every parent must be effective too
```
- **`AccessPolicy.Get(user)`** returns the full set, cached per user and cleared when anything about that user or their role changes.
- **`/api/me/tabs`** keeps its shape for existing callers. A new **`/api/me/access`** returns tabs, sections, actions and the model policy in one response.
- **Server checks:**
  - `TabAccessMiddleware` checks `tab:` keys.
  - Each section's routes check their `section:` key. HR's routes are grouped by section, so the Rulebook API can't be reached with only `section:hr.ask`.
  - Each capability check from §2 calls `AccessPolicy.Has(user, "action:…")`. The old role checks are deleted, not left beside it.

### 3.4 API (SystemAdmin, or `action:access.manage`)
| Route | Purpose |
|---|---|
| `GET /api/access/roles` | Every role with its keys and values |
| `PUT /api/access/roles/{role}` | Save a role's keys (diff only), with an optional note; 400 on an invariant breach, naming the invariant |
| `GET /api/access/users/{id}` | The user's role, overrides and effective access (each key with its reason: role, allow, deny, parent missing, invariant) |
| `PUT /api/access/users/{id}` | Save overrides (set, change or clear a key) and/or the role |
| `GET /api/access/view-as?userId=&role=` | Effective access for a user or a role (P3) |
| `GET /api/access/log` | Change log, filterable (P3) |
| `GET /api/access/export`, `POST /api/access/import` | Role presets as JSON; import shows a diff first and applies only on confirm (P3) |

### 3.5 UI (Access)
- **Roles page:**
  - **A matrix:** rows are tabs grouped Work · Tools · System, with Bengali and English names from the catalog; columns are roles.
  - **SystemAdmin's column** is locked "all", and **Settings** is locked "on" for everyone.
  - **Expanding a tab row** shows its sections and actions as indented rows.
  - **Unsaved changes** show a count and a Save bar with a note field; leaving with unsaved changes warns.
  - **After saving**, a toast says how many users are affected.
- **User page** (replaces the current tabs block):
  - Each key shows the role's value and a three-way control: **Role default · Allow · Deny**.
  - Overridden rows are marked.
  - A child whose parent is off is shown greyed with "needs <parent>".
  - The current "ROLE" lock badge becomes "from role".
- **Phones:** on small screens the matrix becomes one role at a time, chosen from a role picker.
- **Self-protection:**
  - **Your own access:** an admin can't deny themselves `tab:access` or `action:access.manage` (the control is disabled, with the reason).
  - **The last SystemAdmin:** changing their role is refused.
- **Live update:** after a change, affected users' module menu and System menu update on their next API call. When a 403 comes back, the frontend reloads `/api/me/access` once and redraws.
- **Style:** theme tokens only, light and dark, usable at 360 px.

---

## 4. Phase P1 — Catalog, presets in the database, one policy

**Do:** §3.1–§3.3 for **tabs**, with sections and actions declared in the catalog but still seeded to match today; `PUT /api/access/roles/{role}` for `tab:` keys; the Roles matrix for tabs only.

**Gates**

| Gate | Check |
|---|---|
| G-P1.1 | **The list of role checks** (§2) and, for each, the file and line where it now calls `AccessPolicy`. `git grep` shows no remaining role comparison that decides access outside `AccessPolicy` and `Rbac.cs` normalisation |
| G-P1.2 | **Nothing changed.** For a copy of the dev database and a fresh one: dump every user's effective tabs and capabilities before the change (with the old code) and after (with the new code); the two dumps are identical. Paste the diff command and its empty output |
| G-P1.3 | **Seeds.** A fresh database has one `RolePermissions` row per role × key; existing tab grants became `allow` overrides; restarting does not duplicate or overwrite edited rows |
| G-P1.4 | **Role edit applies.** Give General `tab:chat`: a General user's next request to `/api/chat` passes the tab gate (and `ModelPolicy` still applies); remove it: the next request is 403. No restart. Every user's cache is cleared (two users checked) |
| G-P1.5 | **Invariants.** Saving SystemAdmin without a tab, turning off `settings` for any role, or leaving zero active SystemAdmins: each is 400 naming the invariant, and nothing is saved |
| G-P1.6 | **UI.** The Roles matrix for tabs saves and reloads; the module switcher of an affected user changes after their next call. Screenshots, light and dark, 360 px |
| G-P1.7 | **Suites.** `rbac-eval --phase P1` summary and the red line; `hr-eval` (all phases), `po-eval`, `oddity-eval`, `model-policy-eval` unchanged; `dotnet build` 0 warnings; `npm run build` clean |

---

## 5. Phase P2 — Sections, actions, per-user Allow/Deny

**Do:** section and action enforcement (§3.3), user overrides (§3.4 user routes), the expanded Roles matrix and the User page (§3.5).

**Gates**

| Gate | Check |
|---|---|
| G-P2.1 | **Sections.** An `HrAssistant` with `section:hr.procedures` denied gets 403 on every Procedures route, and its menu hides Procedures; Ask, Library and Rulebook still work. Grant it back: they work on the next call |
| G-P2.2 | **Actions.** Turn off `action:hr.rulebook.confirm` for HrAssistant: confirm returns 403 for them and the Confirm button is hidden, while editing still works. Turn on `action:hr.procedures.upload` for HrAssistant: upload works for them |
| G-P2.3 | **Overrides.** User-level Deny beats the role's Allow; user-level Allow beats the role's Deny; clearing returns to the role value. Each case checked at a route, not only in `/api/me/access` |
| G-P2.4 | **Parents.** An action allowed whose section is off has no effect (route 403) and the UI shows "needs …" |
| G-P2.5 | **Self-protection.** An admin can't deny themselves Access; the last SystemAdmin's role can't be changed; both are refused by the server, not only by the UI |
| G-P2.6 | **Log.** Every change in P2's checks has an `AccessChangeLog` row with before → after; `UPDATE`/`DELETE` on the log fails |
| G-P2.7 | **UI.** The Roles matrix with an expanded HR row (four sections, its actions) and the User page with all three states. Screenshots, light and dark, 360 px; no hard-coded colours |
| G-P2.8 | Builds clean; `rbac-eval --phase P2`; the other suites unchanged |

---

## 6. Phase P3 — View as, history, export and import, live

| Gate | Check |
|---|---|
| G-P3.1 | **View as.** For `hr-assistant`, a DevOps user and a General user, "View as" shows the same tabs, sections, actions and models their own `/api/me/access` returns (compare JSON) |
| G-P3.2 | **History page.** Filter by role, user, key and date; shows who, before → after, note |
| G-P3.3 | **Export and import.** Export, change two values in the JSON, import: the diff shows exactly those two; confirm applies them and logs them; an import that breaks an invariant is refused whole |
| G-P3.4 | **Live.** On the real app: give DevOps `section:hr.rulebook` read-only (no edit actions), log in as DevOps, screenshot the HR tab showing Procedures and Rulebook without edit buttons; revert from the log's values |
| G-P3.5 | Builds clean; all suites |

---

## 7. Do not

- Do not keep a role or permission list in the frontend.
- Do not leave old role checks beside `AccessPolicy`.
- Do not let any change reduce SystemAdmin, turn off Settings, remove the last SystemAdmin, or lock an admin out of Access.
- Do not change anyone's access during the migration.
- Do not push, merge to `main` or open a pull request.

---

## 8. Report format (end of every phase)

```
## Phase PN report — <title>
Summary: <2–3 lines>
Eval: <N/M passed>   first run before the code: <N/M passed>
Other suites: hr-eval <N/M>, po-eval <N/M>, oddity-eval <N/M>, model-policy-eval <N/M>

| Gate | Check | Result | Evidence |
|------|-------|--------|----------|

Facts from §2 that changed: <list, or "none">
Role checks found and replaced: <list with file:line>   (P1 only)
Files changed: <list>
Decisions made (and why): <list>
Noticed, not changed: <list>
Needs KAKAROT: <questions, or "none">
```

A phase with any FAIL is not complete. Say so plainly.

---

## 9. Open questions for KAKAROT

Each has a default the build follows until you say otherwise.

1. **New roles.** Default: roles stay in code; the UI edits what each role gets. Say if admins should create roles from the UI.
2. **Who manages access.** Default: SystemAdmin, plus anyone given `action:access.manage`. Say if only SystemAdmin.
3. **Model policy.** Default: stays in its own "Models by role" panel; the Roles page links to it. Say if models should become rows in the same matrix.
4. **Ground Control.** Default: its Oddity rights become `action:oddity.*` keys with today's values, editable like the rest.
