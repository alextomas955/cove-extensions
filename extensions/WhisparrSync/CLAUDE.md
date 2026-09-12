# Whisparr Sync

Cove extension `com.alextomas955.whisparrsync`. It synchronizes Cove with the user's Whisparr
instance. Cove holds the Whisparr API key server-side and calls outward. Whisparr downloads on its
own machine, and nothing downloads that the user did not ask for.

The repo-root `CLAUDE.md` rules apply here. This file adds only what is specific to Whisparr Sync.

## What v2 and v3 are

Whisparr ships as two products built on different architectures, and this extension supports both.

- **v2** keeps a series-and-episode catalogue. A site is a series, a scene is an episode under it,
  and a delivery names its file under `episodeFile`.
- **v3** keeps a movie catalogue. A scene is a movie, there is no series above it, and a delivery
  names its file under `movieFile`.

Neither replaces the other. Both are maintained, both are supported targets, and each holds
capabilities the other does not. Do not describe either as older, newer, legacy or next-generation,
in code, comments, tests, documentation or commit messages: the framing makes a gap on one of them
read as acceptable, which is how this extension came to drive several shared capabilities on v3
alone. Write `v2` and `v3`, or name the architecture.

## Identity and routing

- `extension.json` is the only source of identity (id, name, version, description, host floor,
  file names). The base class's identity properties are virtual, and a C# override replaces the
  manifest value with no warning. Override none of them.
- The API route prefix is `/api/extensions/` plus `Id`, computed on instance members at runtime,
  never a literal.
- Every endpoint declares its permission gate and re-checks the principal in its handler. Both read
  one shared array. The host's attribute filter is MVC-only and does nothing on a minimal-API
  endpoint. A handler that only checks itself advertises nothing to the host.

## The one anonymous route

- The inbound import callback is the only route that responds without a Cove permission. A test
  checks that count.
- It is declared with the SDK's anonymous convention.
- It is authenticated by a secret this extension generates and stores server-side. The secret is
  accepted in a header, as Basic auth, or in the address.
- A route that declares no gate is admitted anonymously, with only a host warning. Declare a gate on
  every other route.

## The library card surfaces

- Whether a card badge exists on a generation is a manifest fact, not a branch inside a component.
  `GetUIManifest` omits the videos-view and performers-view registrations when the stored generation
  is v2, so the host renders no wrapper element for them. A component returning null
  leaves the host's in-card box behind, which is a different observable result.
- `GetUIManifest` is synchronous and cannot read the store. Keep its generation available across
  host threads and current after options are loaded. A generation not established keeps every surface.
- The host mounts its card slot in the grid display mode only, and the toolbar slot in every mode. So
  a control that gates card badges can be pressed where no badge can mount, and the disclosure of
  that is on the control.

## Settings component name

The name the C# UI manifest advertises and the key in the bundle's `defineExtension` components map
must match byte for byte. On a mismatch the host renders the tab and heading and an empty component,
with no error. Verify agreement between the actual manifest and bundle registrations. Follow the
root testing policy when choosing how to exercise that contract.

## Which half of the outbound seam a request belongs in

Use the generated client package for the target generation: `Whisparr3.Net` or `Whisparr2.Net`.

- A new request on either generation goes through that generation's generated client. Add it
  hand-composed only when the generated model cannot express the body, and say which member it
  cannot express.
- Hand-composed requests use the configured `HttpClient` so the same transport bounds apply.
- The notification create and update carry a body built from the schema the instance itself returned.
  Its flags differ per instance and per generation, so a fixed member set cannot express it.
- The v3 studio scope change re-sends the resource it just read with two members changed. A fixed
  member set would drop whatever else the instance answered with.
- Stream the exclusions read because its row count grows with the library. Keep per-operation
  memory bounded independently of library size.
- The generated client fixes its address and key at registration. The gateway holds one registration
  per address-and-key pair because both are settings a person edits.
- The generated client applies no response bound, no redirect cap and no timeout of its own. All
  three are attached through each generation's own `ConfigureHttpClient`.
- The v3 add resources declare one acquisition-suppressing flag each and they differ: studio and
  performer declare `searchOnAdd`, the scene resource declares `addOptions.searchForMovie`. The v2
  add resource declares two, `addOptions.searchForMissingEpisodes` and
  `addOptions.searchForCutoffUnmetEpisodes`, and both are set from one local. Sending another
  generation's spelling is sending a member the instance discards.

## Capabilities per Whisparr generation

A capability a generation cannot honor is a role interface its backend does not implement. A caller
obtains the role or is refused before any request leaves. There is no `Supports*` probe and no
version-mismatch throw. Bind a role to behavior that was measured against a real instance, not to a
field the API documentation names.

## Where an e2e spec lives, and what its name says

`e2e/tests/` holds one directory per capability, named the way the C# folders and the UI slices are:
`monitoring`, `missing`, `scene`, `library`, `import`, `settings`. `host` is the exception and holds
the specs about the host contract rather than a capability of this extension. Playwright collects
the directory recursively, so a new one needs no configuration.

### What a spec's filename says

A name is a coverage claim, not a fixture choice. Every spec starts something. The suffix answers
one question: which generations is this capability driven on.

| Name                             | What it claims                                                                                                                                                                                                                    |
| -------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `*.shared.spec.mjs`              | A capability both generations hold, driven on both. One scenario body, collected once per generation, each execution starting only its own instance. The body holds no branch on the generation.                                  |
| `*.v2.spec.mjs`, `*.v3.spec.mjs` | A capability that generation holds alone, or its own refusal. Only that generation's instance starts.                                                                                                                             |
| `*.ui.spec.mjs`                  | The browser driven against answers the spec supplies itself. No instance starts.                                                                                                                                                  |
| `generation-switch.spec.mjs`     | The connected generation changes inside the test.                                                                                                                                                                                 |
| no suffix                        | No per-generation capability claim: the host contract, a spec that needs no instance, and the connection, the notification it registers and the acquire chain, which are the same product behaviour whichever generation answers. |

A spec that connects an instance resolves `e2e/lib/connected-fixture.mjs`. It owns one Cove
installation per test, addresses it from both the browser and the API client, and starts the one
instance its `generation` option names. What the two generations spell differently, and the reads a
scenario uses to check them, live in `e2e/lib/generation-adapter.mjs`.

`e2e/lib/v2-fixture.mjs` remains for the `*.v2` specs that need its seeded site, the scene under it
and the studio in Cove that names the site.

## Secrets

The Whisparr API key and the callback secret live in a table this extension owns, never in the
extension store. Cove's bulk extension-data route returns every stored value whole. Everything else
stays in the one fixed-size options blob.

## Docs

Every user-facing document, including the manifest description, describes only what exists. A
capability described before it ships is a defect.
