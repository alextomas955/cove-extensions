# Changelog

User-facing changes, newest first.

## 1.1.0 - Connect to Whisparr

The **Whisparr Sync** tab under Settings → Extensions is now a working connection page.

- **Connect Cove to your Whisparr instance.** Enter its address and API key and press **Test
  connection**. The answer names the version that instance reported and which generation it is, and a
  failure says which of the five things went wrong - nothing answered, the key was refused, something
  other than Whisparr's API answered, or the version is one this extension does not manage - so you
  are sent to the setting that is actually wrong.
- **Both Whisparr generations are first-class.** v3 (Eros) and v2 keep separate addresses, keys and
  recorded versions, and switching between them never overwrites the other's. Testing an address that
  turns out to be the other generation names what it found and stops rather than quietly changing
  which one Cove uses.
- **Register the import webhook in one click**, or copy the address and paste it into Whisparr
  yourself. The status line distinguishes "not checked", "not registered" and "registered but nothing
  has arrived" - the last of which is what a callback address Whisparr cannot reach looks like. If
  Whisparr reaches Cove at a different address from the one you use, correct it in the field and your
  correction is kept.
- **Nothing is imported yet.** A delivery from Whisparr is checked and acknowledged; it is not read,
  matched or applied to your library. The import path arrives in a later release and will use the
  registration you make now.

Two things worth knowing before you enter a key:

- **Your Whisparr API key stays on the server.** Cove stores it in a table this extension owns and
  never sends it to your browser.
- **One endpoint answers callers holding no Cove permission**, and only one: the import callback
  Whisparr posts to. It is authenticated by a secret Cove mints and keeps server-side rather than by a
  Cove permission, and a delivery without that secret is refused. Registering from the page keeps that
  secret out of the address, where proxies would record it; an address you paste by hand carries it in
  the address, and the page tells you so until you register from it.

## 1.0.0 - First release

Whisparr Sync connects Cove to the Whisparr instance you run, so the two agree about what your
library holds. It calls out to Whisparr over the network and authenticates with an API key you
supply, which Cove keeps server-side and never sends to your browser.

**Needs Cove 1.3.1.** Cove refuses to install an extension that asks for a newer host than the one
you are running, and the extension listing hides a version your Cove is below, so on an older Cove
this release is not offered to you in the first place. Upgrade Cove, then install. The floor sits at
1.3.1 because that is the Cove release Whisparr Sync is built and tested against, and an extension
cannot honestly advertise a host it has never run on.

- **There is nothing to configure yet.** Installing this release adds a **Whisparr Sync** tab under
  Settings → Extensions, and that tab says setup arrives in a later release. There is no connection
  form, no matching and no syncing behind it, and nothing in it reads or changes your library. Every
  claim above about what Whisparr Sync talks to is the contract it binds itself to as those surfaces
  arrive, not a description of behaviour you can use today.
