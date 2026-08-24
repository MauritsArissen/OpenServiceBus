# Canned Messages & Environments

The Explorer's reuse layer: save fully configured messages under a name, parameterize
them with dynamic variables and Postman-style environments, and commit the whole setup
to git so every developer on the team gets it.

Everything on this page is an **OpenServiceBus Explorer feature** - it exists only in
the Explorer's compose-and-send surface. Applications talking to the broker directly
through the Azure SDKs are completely unaffected: no SDK behavior, wire protocol or
broker semantics change, and a `{{$guid}}` sent by YOUR code is just literal text.
Because the Explorer materializes all values BEFORE its SDK send, the features also
work identically whether the Explorer is connected to OpenServiceBus or to a real
Azure Service Bus namespace.

## Canned messages

A canned message stores every field the Send tab supports - body, MessageId,
CorrelationId, Subject, ContentType, ReplyTo, To, SessionId, PartitionKey, TTL, a
relative schedule delay, application properties, copy count and send strategy - plus a
name and a target scope (one specific entity, or any).

- **Save** from the Send tab ("Save as canned") or create from scratch on the
  management page (sidebar entry, or the Send tab's Manage button).
- **Pick** from the Send tab's picker, which only offers messages scoped to the
  selected entity plus the "any entity" ones; loading pre-fills the whole form.
- **Manage** on the master-detail page: library rows on the left, an inline editor on
  the right with explicit Save/Revert, duplicate and delete. No modals; phone-friendly.
- **Import/Export** as a JSON array - the export IS the import format and also the
  library file format below, so the three round-trip losslessly.

## The library file - commit it to git

Point `OSB_EXPLORER_CANNED_FILE` at a JSON file and the library becomes shared,
versioned team configuration:

- Loaded at startup; every UI edit writes back atomically, pretty-printed with stable
  name ordering so git diffs stay readable. "Tweak in the UI, `git diff`, commit" is
  the whole workflow.
- **Reload from file** (or `POST /api/canned/reset`) re-reads the disk as it is right
  now - the way to pick up a `git pull` without restarting.
- A read-only file (`:ro` docker mount) still loads; edits stay in memory until
  restart, and the management page flags it. A missing file starts empty and is created
  on first save; invalid JSON logs a warning and starts empty instead of crashing.
- Unset, the library is simply in-memory - the file is entirely optional.

The reference `docker-compose.yml` in [Docker](Docker) mounts `canned-messages.json`
next to `config.json`. The hosted demo mounts its library read-only and restores it
(along with environments and sequence counters) on its 30-minute reset cadence.

## Environments

Postman's environments, applied to messaging: named key/value sets, ONE active per
browser, referenced in payloads as `{{key}}`.

- Define **Card of Alice** (`cardnumber = 123400000`, `cardholder = alice`) and
  **Card of Bob**; a canned message writes
  `{ "cardnr": {{cardnumber}}, "cardholder": "{{cardholder}}" }` - switch the active
  environment in the topbar's globe pill and the same send produces different data.
- **Namespace split**: plain `{{key}}` is the environment namespace; `{{$...}}` is
  reserved for built-ins - they never collide (Postman's own convention). Environment
  resolution runs FIRST, so an environment value may itself contain `{{$guid}}` and
  still resolve per message copy.
- Per-value **enable toggles**; disabled values never resolve. Unresolved names are
  sent verbatim - after an explicit confirmation (see below).
- The ACTIVE environment is a per-browser choice (localStorage); the library of
  environments is shared Explorer state, managed on the same master-detail page style.
- **Postman-compatible import/export**: real Postman environment exports import
  directly (`id`, `type`, `_postman_variable_scope` and friends are ignored); export
  downloads the same `{ "name": ..., "values": [{ "key", "value", "enabled" }] }`
  shape.
- `OSB_EXPLORER_ENVIRONMENTS_FILE` gives environments the same committable-file
  lifecycle as the canned library. Out of scope by design: Postman's initial/current
  value split, secret masking, cloud sync.

## Dynamic variables

Built-ins are resolved at send time, independently for EVERY copy of a multi-count
send, in the body, all system property fields and application property values. Names
are case-insensitive; arguments are space-separated with single quotes for values that
contain spaces.

| Variable | Result |
| -------- | ------ |
| `{{$guid}}` | random guid, lowercase (also `{{$guid upper}}` / `{{$guid lower}}`) |
| `{{$ulid}}` | time-sortable unique id (ULID) - send order stays visible when peeking |
| `{{$sequence}}` | incrementing counter, `{{$sequence 100}}` picks the start; scoped per entity + template, survives across sends until the Explorer restarts |
| `{{$index}}` | zero-based copy index within a multi-count send (`count: 5` gives 0..4) |
| `{{$datetime iso8601}}` | current UTC time; formats: `iso8601`, `rfc1123`, `unix`, `unixms` |
| `{{$datetime 'yyyy-MM-dd'}}` | custom .NET format string - must be single-quoted (spaces allowed) |
| `{{$datetime iso8601 -5d}}` | optional offset: `[+-]N` plus `y M w d h m s` (capital `M` = months) |
| `{{$timestamp}}` | unix seconds shorthand |
| `{{$randomInt}}` | random integer 0..1000, or `{{$randomInt min max}}` inclusive |
| `{{$randomDouble min max}}` | random decimal in range; optional third argument = decimals (default 2) |
| `{{$randomBoolean}}` | `true` or `false` |
| `{{$randomAlphaNumeric n}}` | random letters+digits of length n |
| `{{$randomHex n}}` | random lowercase hex of length n |
| `{{$randomChoice a\|b\|c}}` | one of the listed values - enums like regions or statuses |
| `{{$randomBase64 bytes}}` | random blob of exactly N bytes, base64-encoded - payloads of a precise size |
| `{{$repeat 'text' n}}` | deterministic padding: the quoted text repeated n times |

Generated output is capped at 1 MB (`randomBase64` at 768 KB of raw bytes); anything
malformed - wrong argument counts, unbalanced quotes, out-of-range values, unknown
names - stays in the text verbatim rather than half-resolving.

Special cases worth knowing:

- A **MessageId** containing any variable is used as resolved, per copy - the usual
  `-0…-N` multi-count suffix is skipped because each copy is already unique.
- `{{$sequence}}` counters live in Explorer memory, keyed by entity + template + start
  value: the same saved canned message keeps counting across sends, an edited template
  starts a fresh counter, and a restart (or the demo's 30-minute reset) starts over.

## Composing, hover and the pre-send check

While composing, tokens are highlighted in place: green = valid built-in, blue =
environment token resolved by the active environment, amber with a wavy underline =
unknown, malformed or unresolved. Hover a token to see what it resolves to - built-ins
explain what they generate, environment tokens show the active value. Clicking a token
places the caret after it; double-click selects it. The "Variables" button opens the
grouped guide with click-to-copy syntax.

Pressing **Send** with any unresolvable variable - built-in or environment - opens a
confirmation listing the offending tokens; continue to send the token text verbatim, or
cancel and fix it.

## API reference

Everything the UI does is plain JSON over the Explorer's backend - scriptable from CI
or the command line:

| Endpoint | Purpose |
| -------- | ------- |
| `GET /api/canned` | list the library |
| `POST /api/canned` | create (409 on name collision) |
| `PUT /api/canned/{name}` | update or rename |
| `DELETE /api/canned/{name}` | delete |
| `POST /api/canned/{name}/duplicate` | copy under the next free "name (copy N)" |
| `POST /api/canned/import` | merge by name; body `{ messages, conflictMode: "skip"\|"replace" }`, 409 lists conflicts when no mode is given |
| `GET /api/canned/export` | download the library in the file/import format |
| `POST /api/canned/reset` | restore the startup defaults / re-read the library file |
| `GET/POST/PUT/DELETE /api/environments...` | the identical surface for environments, plus `/api/environments/export` and `/reset` |
| `POST /api/send` | send; pass `environment` (a name) to resolve `{{key}}` tokens from that environment |

`GET /api/config` reports `cannedFile` and `environmentsFile` status (configured,
writable, path) so tooling can detect the file-backed mode.

## See also

- [Explorer](Explorer) - the UI these features live in.
- [Docker](Docker) - compose mounts for both library files.
- [Configuration](Configuration) - the environment variable reference.
