# Stage 7f - Export / import (Implementation Notes)

- `SessionExport`:
  - `ExportJsonlAsync` / `ExportJsonlString` — every session entry (full
    history, including branches) as JSONL via the shared serializer.
  - `ExportHtmlAsync` / `BuildHtml` — standalone styled HTML of the active
    branch (inline CSS, HTML-escaped text, thinking/tool/image blocks).
  - `ImportJsonlAsync` / `ImportJsonlStringAsync` — reads a JSONL file/string
    into a new in-memory session (fresh id), restoring entries verbatim.
- `SessionJson.WriteEntryText(entry)` — shared entry serializer (also used by
  `SessionStore`), so exports and the session files round-trip identically.
- CLI (`InteractiveLoop`):
  - `/export jsonl|html <path>` — missing path writes
    `pinet-<session-id>.<format>` under the session cwd; works with an active
    session or a snapshot of the ephemeral context.
  - `/import <path.jsonl>` — imports, persists the new session under a fresh id
    (`SessionManager.SaveAsync`), rebuilds the agent so the next turn continues
    the imported conversation.
  - Malformed/missing files fail cleanly (red `[import]` message); the current
    session is untouched.
- Tests: `PiNet.Sessions.Tests/SessionExportTests` (round-trip, HTML, escaping,
  malformed), `InteractiveLoopTests` export/import section (round-trip across
  loops, usage, malformed + missing file).
