# Changelog

User-facing changes, newest first.

## 1.7.0 - The studio and performer Whisparr controls are named Whisparr

- **The button on a studio or performer page, and the button in those lists' selection bars, are
  now called Whisparr.** Each opens a menu of several actions, so neither is named after one of
  them any more. The rows inside each menu keep the names they had.
- **The page button still says whether Whisparr monitors the entity.** It carries the Whisparr mark
  and no word, so its name reads "Whisparr, monitored" or "Whisparr, not monitored", and it still
  carries its reason when it cannot be pressed.

## 1.6.0 - Compact Whisparr menus, and a confirmation before All Scenes

- **The three Whisparr menus are compact.** Every row draws one icon and its own name. The two
  selection menus open as a small popover headed by the Whisparr mark, the word Whisparr and how many
  things you selected.
- **The sentence under each row is gone.** What each row does, and what it costs, is in the
  monitoring and scenes pages of the documentation. Nothing a row does has changed.
- **All Scenes asks you to confirm.** Choosing it, for one entity or for a whole selection, opens a
  confirmation naming how many entities it covers and what it marks wanted. Cancelling sends nothing.
- **A dimmed row still says why it cannot be pressed**, and a refused gesture still states its reason.

## 1.5.0 - A shorter Whisparr tab on a scene's page

- **The tab leads with a header.** The Whisparr mark and the word Whisparr sit on the left, and the
  scene's state sits on the right where the first fact row used to be.
- **The facts are a compact card, and it leaves out what your Whisparr names nothing for.** A scene
  it holds no file for draws no Quality row, and a scene it has no entry for draws no card at all.
  The state in the header reads for every scene.
- **The sentence under each control is gone.** What each control does is in the scenes page of the
  documentation, and nothing a control does has changed.
- **Each control is a full-width bar on its own row**, one under the next, instead of four buttons
  wrapped across the panel.

## 1.4.0 - Operate one scene, or a selection of scenes

A **Whisparr** tab joins a scene's page in Cove. It states what your Whisparr holds for that scene
and gives you four controls over it. A Whisparr button in the videos selection bar applies one of
five actions to every scene you selected, as one background job.

**What this costs.** The tab makes three requests to your Whisparr each time you open it: one for the
scene, one for the exclusion list, and one for the quality profile the scene names. Nothing is
cached. **Search now** is the only control and the only selection row that can download a file, and
over a selection it becomes one search per selected scene against every indexer your Whisparr has.

- **The tab states four facts:** the state, the quality of the file your Whisparr holds, the quality
  profile it applies, and that profile's cutoff. A fact with no value says so in the value's own
  place, so a scene with no file reads differently from a read that failed.
- **Nothing is asked of your Whisparr until you open the tab.** Opening a scene's page sends no
  request, and the extension draws nothing else on that page.
- **Four controls:** Add to Whisparr, Monitor in Whisparr, Search now and Exclude from Whisparr.
  Monitor and Exclude each become their own reverse once they apply, so a scene excluded by mistake is
  fixed on that same tab.
- **Search now is the only control that downloads.** It reports that Whisparr has the search, read
  back off your instance by the command's own identifier, and claims nothing about a download. What
  it finds arrives the way every other import does.
- **A monitored scene already takes better files up to its cutoff**, which the tab says beside Search
  now. There is no separate control for it.
- **A control that cannot act is disabled and says why**, in one reason rather than several: your
  Whisparr has no entry for the scene, it already holds it, the scene is excluded, or the state could
  not be read.
- **A selection of scenes offers five rows in one fixed order:** Add, Monitor, Stop monitoring,
  Search now, Exclude. Choosing one runs a background job whose counts appear in Cove's job list.
- **One gesture takes at most 1000 scenes, and at most 100 for Search now.** A selection over the
  limit sends nothing at all, states the limit that applied, and keeps your selection.
- **On a Whisparr v2 connection neither surface exists.** The tab is absent from a scene's page and a
  scene selection offers no Whisparr button. A studio or performer selection still offers one, which
  explains what it cannot do there.

## 1.3.0 - See what Whisparr holds for every card in a list

A button in the toolbar of Cove's videos, studios and performers lists puts a small badge on every
card saying what your Whisparr holds for that scene, studio or performer.

**A full page costs one request to your Whisparr per card**, one after another, so the badges take a
moment to fill in. Nothing is asked until you press the button, and nothing is cached between pages.
Any page size Cove offers is covered, however large.

- **Turn the badges on from the list toolbar.** The button carries Whisparr's mark and names itself
  on hover. It is off after every page load and nothing about it is stored in your browser.
- **Each badge reads Monitored, Unmonitored, Not added or Excluded**, in the same words the Whisparr
  button and the Missing tab use. **Not added** means your Whisparr has never heard of the entity;
  **Unmonitored** means it holds it and is leaving it alone.
- **Pressing it changes nothing.** Nothing is added to Whisparr, nothing is monitored, nothing is
  downloaded and nothing in your library is touched.
- **The badges appear in the Grid display mode only**, because that is the only mode where Cove
  keeps a place on a card for them. Press the button in another mode and it says so on hover.
- **A card Cove holds no Whisparr link for shows no badge**, rather than a state that would claim
  your Whisparr has no entry for it.
- **A status that could not be read is reported once, on the button**, and no card claims anything.
  The button says which of the four reasons applies: nothing is connected, the connected Whisparr
  keeps no record of that kind of card, your instance did not answer, or the read did not complete.
- **On a Whisparr v2 connection the button and the badges are absent from the videos and performers
  lists.** That generation keeps no per-scene records and has no performer entity. The studios list
  keeps both. Switching the connected generation takes effect on the next page load.

## 1.2.0 - Browse what you do not own, from a studio, performer or tag page

A **Missing** tab joins studio, performer and tag pages. It lists the scenes your metadata source
knows about for that entity and your library does not hold, and tells you what your Whisparr holds
for each one.

**Read this before the number confuses you.** The figure beside the tab name, and the one beside
the grid, is the size of the catalogue your metadata source lists for that entity. **It is not the
number of scenes you are missing.** A studio with four thousand scenes reads four thousand whether
you own all of them or none.

**One source will not list past ten thousand scenes for one entity.** Where that limit is reached
the count shows a plus, such as `10,000+`, and the pages stop there. The real figure is larger.

- **Browse the catalogue on a studio, performer or tag page.** Each scene is a card with its cover,
  title, date, studio, performers and description, and a pill saying whether Whisparr has no entry
  for it, is wanting it, holds it unmonitored, or could not be read.
- **Search, sort, filter and page through it.** Every one of those travels in the page address, so
  the link you copy shows someone else exactly what you were looking at.
- **Mark one scene wanted**, from its own card. Nothing is downloaded.
- **Mark a page of scenes wanted at once.** Tick the cards you want, or use Cove's own select-all,
  select-none and invert gestures, then press **Monitor**. It runs as one background job that
  reports in Cove's job area with a count of how many were registered, how many Whisparr already
  held and how many it refused. Nothing is downloaded. A selection covers the page you are looking
  at, and changing page clears the ticks.
- **Ask Whisparr to search for one scene**, from its card. **This is the only action on the tab that
  downloads**, it is named on its own button, and it acts on that one scene rather than on
  everything the entity monitors.
- **A control the metadata source cannot honour is left out rather than shown greyed out**, so which
  sorts and filters you are offered depends on which source your Whisparr generation reads from.
- **A scene you excluded in Whisparr does not appear.** To hide one for good, exclude it in
  Whisparr; nothing on this tab writes to that list.
- **Whisparr v2 keeps no per-scene records**, so on a v2 connection the catalogue lists but every
  pill reads Status unknown and the per-scene actions cannot take. A line above the grid says so.
- **Cove now reaches a second host on your behalf.** Filling this tab reads catalogue listings from
  the metadata source Cove is already configured with, using the key Cove holds for it, and your
  browser loads each cover image straight from that source. Whisparr Sync asks you for no new key.
- **Every button now shows a keyboard user where it is.** The focus ring was missing throughout and
  is fixed, which also changes the appearance of the Renamer's buttons.

## 1.1.0 - Connect to Whisparr, take in what it imports, and monitor from an entity page

The **Whisparr Sync** tab under Settings → Extensions is a working connection page, deliveries from
Whisparr reach your library, and a Whisparr button sits on studio and performer pages and in those
lists' selection bars. This is the version in which Whisparr Sync starts changing both your library
and what your Whisparr instance monitors.

**Needs Cove 1.4.0.** The floor moves up from 1.3.1, so upgrade Cove before installing this
release. Cove refuses to install an extension that asks for a newer host than the one you are
running, and the extension listing hides a version your Cove is below, so on an older Cove this
release is not offered to you at all.

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
  unreadable, it sends nothing rather than duplicating every matched file. It names which of the two
  it was - beneath the button for a press you made, and on the background run's own line in Cove's
  job list when turning monitoring on started it. A selection's run does not report it, so run
  **Reflect owned** on one entity to see.
- **Whisparr names the files it links.** A file Whisparr links is Whisparr's to name from then on,
  by its own naming rules. Cove and Whisparr read the same library folders, so a later rename inside
  Whisparr can reach a file Cove holds. Whether such a rename changes the library's own entry, or
  only Whisparr's own link to that file, is not known.
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
- **A press that could not complete names the right party.** Where it previously reported all four
  as Whisparr declining, it now says which one happened: Whisparr declined, Whisparr no longer holds
  the entry, Whisparr's answer was larger than the extension reads at once, or Whisparr accepted the
  change and does not report it. The second sends you to a reload rather than to your instance, the
  third says your instance answered correctly, and the fourth is the only one of the four that does
  not tell you nothing was changed, because after an accepted change that would not be true.
- **Every item in the menu is reachable with the arrow keys**, including on Whisparr v2, where the
  item Whisparr cannot carry out is present and dimmed. The arrow keys used to stop on that dimmed
  item and go no further, which put **Reflect owned** and **Search all monitored** out of reach
  without a pointer. While a gesture is on its way and every item is dimmed, an arrow press now
  leaves your place alone instead of moving nowhere.
- **A long menu scrolls instead of being cut off, and the message about your last press sits below
  the menu, on screen, rather than over it.** A menu taller than the space under the button used to
  lose its last items with nothing to see, and the sentence saying what the last press did used to
  cover the items it was about. The menu now takes the space the message needs out of its own, so a
  menu of any length scrolls and the message keeps its place below it. In a window too short to hold
  both under the button, the menu keeps a readable height instead of collapsing to a sliver.

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
