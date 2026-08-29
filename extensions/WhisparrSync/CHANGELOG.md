# Changelog

User-facing changes, newest first.

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
