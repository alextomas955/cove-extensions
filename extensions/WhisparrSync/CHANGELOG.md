# Changelog

User-facing changes, newest first.

## 1.1.0 - Connect to Whisparr, take in what it imports, and monitor from an entity page

The **Whisparr Sync** tab under Settings → Extensions is a working connection page, deliveries from
Whisparr reach your library, and a Whisparr button sits on studio and performer pages and in those
lists' selection bars. This is the version in which Whisparr Sync starts changing both your library
and what your Whisparr instance monitors.

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
- **What Whisparr imports reaches your library.** A delivery through that callback is matched against
  your Cove library folders and the file is brought in. A delivery Cove cannot place is refused and
  counted, and the settings tab reports what is outstanding.
- **Monitor a studio or a performer from its own page, in one click.** A studio is offered Whisparr's
  own two scopes, Future Scenes and All Scenes, with Future Scenes as the default.
  **Read this before choosing All Scenes:** it marks every scene Whisparr already lists for the
  studio as wanted, which spends indexer traffic and disk, and on Whisparr v3 (Eros) changing the
  scope back to Future Scenes does not undo it. A performer is offered no scope, because Whisparr
  expresses no future-only option for one, so monitoring a performer covers everything it lists.
- **The menu marks the scope a monitored studio is actually set to** on Whisparr v3 (Eros), read
  from Whisparr each time you open the page. On Whisparr v2 that answer carries no scope, so no row
  is marked and the menu says it cannot tell which one is in force rather than marking one for you.
- **Unmonitoring stops new scenes and retracts nothing.** What All Scenes already made wanted stays
  wanted, and Whisparr will still acquire it. Nothing is deleted, in Cove or in Whisparr.
- **One action asks Whisparr to search, and it is the only one.** Monitoring, unmonitoring and
  changing a scope never start a search; what you mark wanted is what Whisparr then acquires on its
  own schedule. **Search all monitored**, in the entity's own menu, is the one thing here that asks
  Whisparr to go and download. It acts on one studio or one performer at a time, on what that entity
  is already monitoring, and it is deliberately not offered for a whole selection.
- **The same two gestures for a whole selection**, from the studios and performers selection bars.
  A selection runs as one background job, and its progress and its result for each entity appear in
  Cove's job list. One gesture takes at most 1000 entities; select more and Cove states the bound and
  changes nothing, so you can act on part of the selection at a time. An entity counts as succeeded
  only where Whisparr, read back afterwards, says it monitors it; an accepted request that left it
  unmonitored counts as failed.
- **Whisparr v2 cannot monitor a performer**, and no route on it registers a catalogue item. The
  button and the affected menu items are shown disabled with the reason rather than hidden.
- **On Whisparr v2, an entity that instance cannot identify now says so.** It reports that Cove
  holds no link the connected Whisparr can identify the entity by, which sends you to the entity's
  link chips, instead of reporting that Whisparr refused - which sent you nowhere.
- **Monitoring a studio on Whisparr v2 no longer depends on how much that instance holds.** Cove asks
  v2 about the one studio you pressed rather than about its whole catalogue. On a large v2 instance
  the button now works where it previously reported that Whisparr had refused.
- **Whisparr links the files you already own into place, by itself.** Turning monitoring on for a
  studio or a performer starts that work in the background with no second press and no dialog, and so
  does monitoring a whole selection, once per entity inside that selection's own run. **Reflect
  owned** in the menu runs it again at any time. Its progress appears in Cove's job list.
  Nothing is copied: Cove reads Whisparr's hard-link setting first, and with that setting off, or
  unreadable, it sends nothing and says so at the control rather than duplicating every matched file.
- **Register the scenes Cove holds that Whisparr does not.** **Add all missing**, in the entity's
  own menu, offers Whisparr every scene Cove holds under that studio or performer, one at a time,
  and then asks Whisparr to re-read the entity's catalogue. Nothing is downloaded, a scene Whisparr
  already holds is left alone, and nothing already wanted, queued or acquired is retracted. It runs
  in the background and reports in Cove's job list. Whisparr v3 (Eros) only.
- **A press Whisparr declines says so.** Whisparr declining, not answering, or offering no quality
  profile or no root folder now states that reason beneath the button instead of appearing to do
  nothing. Nothing is sent and nothing is changed, and the button stays pressable so you can fix the
  instance and try again. It applies to every item in the menu, and to monitoring, unmonitoring and
  changing a scope.
- **A press that could not complete names the right party.** Where it previously reported all three
  as Whisparr declining, it now says which one happened: Whisparr declined, Whisparr no longer holds
  the entry, or Whisparr's answer was larger than the extension reads at once. The second sends you
  to a reload rather than to your instance, and the third says your instance answered correctly.

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
