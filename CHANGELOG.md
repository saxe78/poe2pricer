# Changelog

All notable changes to poe2pricer, newest first. The release notes on GitHub say the same
with more words: https://github.com/saxe78/poe2pricer/releases

## 1.4.0 — 2026-10-29

### Fixed
- Prices that blinked every second on Windows 10 ([#6](https://github.com/saxe78/poe2pricer/issues/6)).
  The labels are now drawn by a window of their own that Windows 10 and 11 both keep out of
  screen captures, so the program no longer reads its own labels back from the screen. The
  window stays transparent and lets clicks through as before; where it cannot be built, the
  previous one is used and the log says so.
- Stash prices that vanished when changing tabs and came back only with the next automatic
  scan. The frame the game draws while the next tab loads (wooden boards on the controller UI,
  dark cloth with keyboard and mouse) no longer counts as a closed stash: the prices go at
  once, and the next tab is read as soon as it is drawn.
- A remnant list opened right after another one could carry the arrow and top price of the one
  before, when its rows (common currency rows) also stood in that list. After a list is opened
  again, the arrow to a better reward out of view appears only once scrolling confirms that it
  is the same list; the prices beside the visible rows are not affected.

### Changed
- After a stash tab, the Ritual window, the remnant list or a tooltip is lost, the program
  looks again right away instead of waiting for the next automatic scan, which could be the
  whole scan interval away.
- The quick check that hides the prices once a window closes paces itself by its typical
  capture time instead of a running mean; at 3840×2160 single slow captures had held it back,
  and the prices stood longer after the window closed.
- The status line counts the download of an update in percent. Before it said only
  "downloading", which with a package of some 170 MB looked as if nothing happened.

## 1.3.1 — 2026-10-24

### Added
- Russian program interface, including tooltips, status and tray messages, with Russian
  plural forms. The game languages supported by recognition remain English and German
  (contributed by OtShellNick, [#5](https://github.com/saxe78/poe2pricer/pull/5)).
- A red line in the window, a tray notice and a log line when the game's settings file names
  a language the program does not read (it reads English and German): nothing is recognised
  then, whatever language the window is in. Before, such a client was silently taken for
  English.
- Russian help guide with localized program screenshots, opening in the selected program
  language; English and German remain available in the guide's language switcher.
- The first line of the log names the Windows build.
- A log line says whether Windows accepted keeping the overlay out of screen captures, or
  refused with which error; a refusal would leave the price labels in the program's own
  reading of the screen ([#6](https://github.com/saxe78/poe2pricer/issues/6)).

### Changed
- Every request introduces itself as `poe2pricer/<version> (+https://github.com/saxe78/poe2pricer)`,
  the form site operators know; before, the User-Agent named the program and version only.
- The community data (rumour tables) is fetched with the same 20 s time-out as every other
  request; before, a connection that stalled could hold "Loading community data…" for 100 s.

### Fixed
- poe.ninja, the trade site and GitHub are reached when the first address of the host does
  not answer (a dead IPv6 route: "poe.ninja: not reachable … Timeout of 20 seconds"): the
  next address is tried after 250 ms, the way browsers do it, instead of waiting out the
  time-out on the first.

## 1.3.0 — 2026-10-23

### Added
- A notice before the first start, once: what the program does and does not do, and that GGG
  approves no third-party tool; "Run" or "Quit". Copies from before see it once at the first
  start after the update. README and Help: "Third-party tools and GGG", with GGG's statements,
  their sources, and where the program stands to them.
- Settings → Prices and display: "Capture the game window only" (on). The game is read from
  its own window through Windows.Graphics.Capture instead of the screen, so the overlay no
  longer has to hide from screen captures: the NVIDIA app, OBS and Game Bar record the desktop
  again, and the prices are in the video. The game gets no message for it. Needs Windows 11;
  on Windows 10, and after a missing or black picture, the screen is read for the run, with a
  line in the window. Off reads the screen as before.

### Fixed
- Ritual with a controller: the tribute dialog ("Offer all remaining Tribute to the king?") is
  recognised at UI Text Size Large and Extra Large too, so the items under it get no price
  on its text ([#1](https://github.com/saxe78/poe2pricer/issues/1)).
- Ritual: a dialog that opens while the prices are shown (tribute, reroll) is found at once
  even when it does not dim the window behind it; before, the prices stayed on its text.
- Stash tabs at UI Text Size Large and Extra Large: items with a tier mark, and stacks with a
  large number or mark, get their price ([#1](https://github.com/saxe78/poe2pricer/issues/1)).
- Waystone and tablet tooltips over a stash tab: lines garbled by the stash showing through
  the tooltip are read all the same, and a tooltip the stash cut short keeps its mods.
- Waystone and tablet tooltips: a number the text recognition leaves out (above all the roll
  shown with Alt held, "32(30-40)%") is read from the game's own digits; a value read as 0 is
  not taken.
- Waystone and tablet tooltips: a mod wrapped onto two lines counts once.
- Tablets tab, Overseer Tablet: a group that asks for both wordings of a twin pair (the prefix
  "Map contains an additional Essence" and the suffix "Map contains # additional Essences")
  means both, in the stash search, the trade query and the judgement at the tooltip; in
  separate groups a row still takes the other wording too.
- Tablets tab: the kinds without a profile line up with the marked ones in the kind list.
- The warning about a "force live price" key that is off because another key holds it names
  the key as the settings file has it.

### Changed
- No more warning about the game's UI Text Size: Large and Extra Large are checked at 1080,
  1440, 2160 and 3440, every window ([#1](https://github.com/saxe78/poe2pricer/issues/1)). Only a size the program
  does not know is noted at the start, as a closable line in the window's brass colour.
- Settings: the "Display" box is "Prices and display" and holds the league too, in a row like
  the two below it, with a small refresh sign in place of the "Refresh prices" button.
- The build's inputs are fixed: `global.json` names the SDK version, `packages.lock.json`
  files the package versions with their hashes, and the release workflow restores in locked
  mode, so no package can change under a commit.

## 1.2.0 — 2026-10-21

### Added
- Waystones tab: profiles (summary values, tier, revives, corrupted, mods in "and" / "or" /
  "not" groups), the search text for the stash search, and the profile's price on the trade
  site ("Refresh price").
- Tablets tab: the same for each kind of tablet, with the uses left.
- Live price at the tooltip of a waystone or tablet, from the trade site of pathofexile.com,
  asked in the words of the closest profile with the item's own values (without a profile,
  exactly as the item is): Settings → Display, "Live price from the trade site" and "incl.
  premium stash (online)". `Alt+D` ("Force live price") asks for the item exactly as it is,
  past the profiles.
- Waystones and tablets in the English and the German game client; the search text and the
  mod lists follow the client's language.
- Stash tabs inside a stash folder are found in the keyboard UI (contributed by
  gazevans-pixel, [#2](https://github.com/saxe78/poe2pricer/issues/2)).
- The log's start line names the date.

### Changed
- Updates are downloaded and installed only after a click on the message; before, they were
  downloaded in the background and installed when you quit.
- A UI Text Size other than medium gives a hint only, without a tray notice ([#1](https://github.com/saxe78/poe2pricer/issues/1)).
- When poe.ninja cannot be reached, the log names the reason (host, secure connection,
  time-out).
- Releases are built by GitHub Actions, with `SHA256SUMS.txt`.
- README and guide: waystones and tablets, with pictures of the program window.

### Fixed
- Favours: a tooltip over empty cells ended at the screen's edge at large text sizes and hid
  the items below it ([#1](https://github.com/saxe78/poe2pricer/issues/1)).
- Island Rumours: the island was not recognised when its ship marker is drawn dimmer ([#1](https://github.com/saxe78/poe2pricer/issues/1)).
- A diagnosis taken after the overlay hid itself reported the window shown before.

## 1.1.2 — 2026-10-20

### Fixed
- Item art is fetched only from web.poecdn.com: a poe.ninja answer naming another address is
  ignored instead of followed.
- The release page a zip copy opens on a click is taken from GitHub's answer only when it lies
  in this repository; anything else leads to the releases page.
- Answers from poe.ninja, the image server, the rumour table and GitHub are read up to 32 MB
  and dropped beyond.
- A diagnosis no longer holds the file paths from the settings (game settings file, data
  folder, the derived data path), which carry the Windows user name.

### Changed
- Item art is decoded by the Windows Imaging Component instead of the decoder in OpenCV, and
  only after a check of the file's header (PNG with alpha channel, at most 1024 pixels a
  side): the one place where data from the network met a parser shipped with the program.

### Added
- README: a "Trust" section (what the program reads, sends and runs; the keyboard hook; the
  unsigned updater; what a diagnosis contains).

## 1.1.1 — 2026-10-19

### Added
- The version number in the program window links to this changelog (opens in the browser).

### Fixed
- Two islands the community table has swapped ("Something Fishy" is Barren Atoll, "Bleak and
  Awful" is Bleached Shoals, as the game's own texts say) are shown as in the game, in the
  Rumours tab and under the line on the card.
- Island Rumours: a roll read right after a short absence of the panel counts; "complete" is
  lifted again when three rows return; the next island is no longer merged into the previous
  one in the controller UI when its first picture shows little of the chart; a reopening that
  shows less of the chart no longer replaces the richer reading the island is recognised by.
- The tier weights are saved even when the ratings file cannot be written.
- At 3840×2160 the quick check of an open window paced itself faster than its screen capture
  took; it and the automatic scan now keep to half the time at most.
- The program no longer writes pictures of its own into the diagnostics folder (1.0.0 and
  1.1.0 kept two full pictures of every reroll it detected in the Ritual window, without end).
  Only the diagnostics key writes there, unless "Extended diagnostics" (Settings → Hotkeys,
  off by default) is on; then at most three pictures of each kind stay.

## 1.1.0 — 2026-10-19

### Added
- Island Rumours are collected per island: the island is recognised by the sea chart around
  its ship marker; rumours no longer on the panel are listed under it, new ones are marked,
  and an expedition tier (the mean of the tiers seen, as a tier) sums the island up, with the
  number of rumours and rolls. Fewer than three on the panel means the island has no more;
  after enough rolls without anything new it says "probably complete". With three on the
  panel the card reminds you to move an item for more.
- Tier weights for the expedition tier in the Rumours tab.
- Prices in Exalted or Chaos: Settings → Display → "Prices in". From one Divine on prices
  stay in Divine either way; the price floor is compared in the unit shown.

### Known limits
- Dragging or zooming the Atlas between two openings of the panel starts a new collection
  for the same island; opening it without moving the Atlas is always recognised.

## 1.0.0 — 2026-10-18

First public release: prices from poe.ninja at the Ritual window (Favours), the Expedition
remnant list, the special stash tabs, and the community tiers of the Island Rumours beside
the panel. Setup with self-update, or zips.
