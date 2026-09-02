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

## Settings component name

The name the C# UI manifest advertises and the key in the bundle's `defineExtension` components map
must match byte for byte. On a mismatch the host renders the tab and heading and an empty component,
with no error. The test that checks it reads both files and compares them to each other, never to a
literal.

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
