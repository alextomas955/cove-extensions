# Whisparr Sync

Cove extension `com.alextomas955.whisparrsync`. It synchronizes Cove with the user's Whisparr
instance. Cove holds the Whisparr API key server-side and calls outward. Whisparr downloads on its
own machine, and nothing downloads that the user did not ask for.

The repo-root `CLAUDE.md` rules apply here. This file adds only what is specific to Whisparr Sync.

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
  is the older one, so the host renders no wrapper element for them. A component returning null
  leaves the host's in-card box behind, which is a different observable result.
- The stored generation reaches the manifest through a volatile field filled at load and refreshed
  after a settings save, because `GetUIManifest` is synchronous and cannot read the store. A
  generation not established keeps every surface.
- The host mounts its card slot in the grid display mode only, and the toolbar slot in every mode. So
  a control that gates card badges can be pressed where no badge can mount, and the disclosure of
  that is on the control.

## Settings component name

The name the C# UI manifest advertises and the key in the bundle's `defineExtension` components map
must match byte for byte. On a mismatch the host renders the tab and heading and an empty component,
with no error. The test that checks it reads both files and compares them to each other, never to a
literal.

## Which half of the outbound seam a request belongs in

Each generation's requests are composed by its own generated package: `Whisparr3.Net` reached
through `Whisparr3Gateway`, `Whisparr2.Net` reached through `Whisparr2Gateway`. Both gateways hold an
instance of `GeneratedClientRegistry<TTarget>`, so the registration cache, the reach counter, the cap
and the eviction loop are declared once.

- A new request on either generation goes through that generation's generated client. Add it
  hand-composed only when the generated model cannot express the body, and say which member it
  cannot express.
- Three requests stay hand-composed for that reason, and each is sent through the held `HttpClient`.
  Their routes are the only `api/` literals on `WhisparrClient`, and a test asserts that set exactly.
- The notification create and update carry a body built from the schema the instance itself returned.
  Its flags differ per instance and per generation, so a fixed member set cannot express it.
- The v3 studio scope change re-sends the resource it just read with two members changed. A fixed
  member set would drop whatever else the instance answered with.
- The exclusions read is reduced row by row as it arrives, because the answer's row count grows with
  the library and nothing this extension holds may grow with it.
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

## Secrets

The Whisparr API key and the callback secret live in a table this extension owns, never in the
extension store. Cove's bulk extension-data route returns every stored value whole. Everything else
stays in the one fixed-size options blob.

## Docs

Every user-facing document, including the manifest description, describes only what exists. A
capability described before it ships is a defect.
