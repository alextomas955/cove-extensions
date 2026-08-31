---
id: settings
title: Settings reference
sidebar_position: 2
---

Every Whisparr Sync setting, grouped by the section it appears in on the **Whisparr Sync** settings
tab (Settings → Extensions → Whisparr Sync), in the order the page shows them. Defaults are what a
fresh install uses.

The tab has three sections. Each generation keeps its own connection, so every setting under
**Connection** below belongs to the generation card you are editing, not to the extension as a whole.

## Whisparr generation

Two cards, one per generation. Each shows what is stored for that generation and nothing from the
other.

| Card               | What it is                                                 |
| ------------------ | ---------------------------------------------------------- |
| Whisparr v3 (Eros) | The newer generation. The generation a fresh install uses. |
| Whisparr v2        | The older generation.                                      |

A card is marked **In use** when it is the generation Cove acts on, and **Editing** when it is the
one the **Connection** section below is showing. The two differ from the moment you press **Switch**
until your next save.

**Switch** shows the other card. It saves nothing and asks nothing: anything you typed and did not
save is discarded. Saving while a card that is not in use is showing makes that generation the one
Cove uses, and reloads the page.

## Connection

The Whisparr instance Cove keeps in step with.

| Setting          | What it does                                                                                      | Default     | Valid values                                                                                                                                                                                            |
| ---------------- | ------------------------------------------------------------------------------------------------- | ----------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Whisparr address | The address Cove itself reaches Whisparr on.                                                      | Empty       | An absolute `http` or `https` URL, including the port, for example `http://whisparr:6969`. No scheme is added for you: an address without one counts as no address, and the page asks you to enter one. |
| API key          | The key Cove authenticates to that instance with. Read it from Whisparr's own Settings → General. | None stored | Whisparr's API key, as that instance shows it. Leave the field blank to keep the key already stored.                                                                                                    |

The address is the one **Cove** reaches Whisparr on, which is not always the one your browser uses.
If Cove runs in a container, that is usually the container name and port rather than `localhost`.

### What the section tells you

Two lines above the fields, which measure different things and are never merged:

- **Whisparr reported \<version\> · verified \<when\>** - the version string that instance sent, and
  when a test against the stored address last read it. It reads _Whisparr version not verified yet_
  until a test succeeds.
- **Whisparr last reachable \<when\>** - when the instance last answered anything at all. It reads
  _Whisparr has not answered yet_ until one does.

A version verified last week beside an instance reached a minute ago is honest, not contradictory.

Beside the API key, one of four states: _Key is set_, _Key not stored_, _New key will be saved_, or
_Key will be removed when you save_.

### Controls

| Control          | What it does                                                                                                                                                                                                                                                                                                                                                               |
| ---------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Test connection  | Asks the instance who it is and reports the answer. When a key is already stored and you have changed nothing, it tests the **stored** connection, and that is the only test allowed to update the two recorded lines. When you have edited the address or typed a key, it tests that pair and records nothing, because the instance it reaches may not be the stored one. |
| Save connection  | Stores the address, and the key if you typed one or cleared it.                                                                                                                                                                                                                                                                                                            |
| Clear stored key | Marks the stored key for removal. It is removed when you save, not when you press this.                                                                                                                                                                                                                                                                                    |
| Keep stored key  | Undoes **Clear stored key** before you save.                                                                                                                                                                                                                                                                                                                               |

Editing the address clears a test result, because the result described the address that was in the
field when it ran. Trailing slashes and letter case do not count as an edit.

If a test reaches the **other** generation, the page names the version it found and stops. Nothing is
saved and the other generation's stored connection is untouched; switch to that card to configure it
there.

### What a failed test says

Five answers, kept apart on purpose, because each sends you somewhere different:

| The page says                                                                                                                      | What it means                                                                                                            |
| ---------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------ |
| Enter the Whisparr address and API key above…                                                                                      | Nothing was entered, so nothing was tried.                                                                               |
| Nothing answered at \<address\>…                                                                                                   | The request left Cove and got no answer. Check the address, and that Cove can reach it.                                  |
| Whisparr turned that API key down…                                                                                                 | Something answered and refused the key. Check the key, not the address.                                                  |
| \<address\> answered, but not as a Whisparr API…                                                                                   | Something is at that address, but it is not Whisparr's API.                                                              |
| That instance is \<app\> \<version\>, not Whisparr… / That instance is Whisparr \<version\>, which this extension does not manage. | Whisparr's API answered on a version this extension does not manage, or another \*arr application answered in its place. |

## Import webhook

The address Whisparr calls when it finishes an import.

| Setting          | What it does                          | Default                                             | Valid values                                                                                                                                        |
| ---------------- | ------------------------------------- | --------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------- |
| Callback address | The address Whisparr is told to call. | Built from the address your browser reached Cove on | An absolute `http` or `https` URL. Only its scheme, host, port and path prefix are used; the route after that and the secret are always Cove's own. |

Correct the address when Whisparr reaches Cove somewhere other than you do - a different host name
inside a container network, or Cove behind a reverse proxy on a subpath. Your correction is stored
when you register, so it survives a page refresh.

The address shown carries the callback secret, because that is the form you would paste into Whisparr
by hand. The address **Register in Whisparr** writes carries no secret at all: the secret goes in a
request header on v3 and as Basic authentication on v2.

### Controls

| Control              | What it does                                                                                                            |
| -------------------- | ----------------------------------------------------------------------------------------------------------------------- |
| Copy URL             | Copies the address as shown, secret included, for pasting into Whisparr yourself.                                       |
| Register in Whisparr | Writes the callback into the connected instance, then reads its notification list back and reports what it found there. |

Registering twice leaves one entry: the second registration moves the existing one rather than adding
a second.

### What the status line says

| The page says                                              | What it means                                                                                                 |
| ---------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------- |
| Cove has not checked this instance for its callback yet.   | Nothing has asked this generation. Not the same as absent.                                                    |
| Cove's callback is not registered on this instance.        | Cove looked, and the callback is not there.                                                                   |
| Registered, but no import has reached Cove through it yet. | The registration is there. Nothing has arrived through it, which is what a wrong callback address looks like. |
| Registered, and imports are reaching Cove through it.      | Deliveries are arriving.                                                                                      |

A standing note appears while deliveries carry the secret in the address, where proxies and load
balancers record it. Registering again from the page moves it out of the address, and the note clears
itself once a delivery arrives that way. There is no dismiss control: it goes when the fact goes.

## Options with no control in the page

Four options exist in Whisparr Sync's stored settings and have **no control on the settings tab**.
They are listed here because they are part of what the extension stores, not because you can set them
from the page.

| Option                      | What it is for                                                                                                                                                        | Default                                  |
| --------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| Path translation            | A prefix-rewrite table for setups where Cove and Whisparr genuinely see the library at different paths. First matching rule wins, matched at a path-segment boundary. | Empty - paths are identical              |
| Default monitor scope       | The scope used when a caller does not ask for one. Both scopes are non-grabbing.                                                                                      | New releases only                        |
| Metadata provider endpoints | Which metadata provider configured in Cove counts as the identity source, per generation.                                                                             | Blank - each provider's standard address |
| Callback host               | The host the callback address is built on before a registration exists.                                                                                               | Blank - derived from the request host    |

**Callback host** is the one of the four with an effect today, and you never type it directly: it is
written for you from the callback address you edit, which is what makes that edit survive a refresh.

**The other three are stored and nothing reads them yet.** They belong to the import path, which is
not built. Setting one today changes no behaviour. They are documented here so that when that path
arrives, its defaults are the ones stated above rather than values chosen silently.

Saving the settings tab never touches any of the four: the page submits no value for them, and a save
leaves whatever is stored exactly as it was.
