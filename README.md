# Persistent Pots Tracker

A Dalamud plugin for tracking Pot FATEs in FFXIV's Occult Crescent.

Pulls data from [FFXIVTracker](https://tracker.xivstats.com/) to show upcoming pot spawns, countdowns, and instance activity in-game. I made this so I wouldn't have to keep checking the tracker website while playing.

## Features

- **Pot timers** — See the next predicted pot spawn and countdown for South Horn and North Horn.
- **Datacenter filtering** — Supports Aether, Primal, Crystal, Dynamis, and Materia.
- **Instance matching** — Attempts to identify your current instance using active FATEs and Critical Engagements. Still experimental, so matches aren't always correct.
- **Instance highlighting** — Highlights your matched instance in the tracker table.
- **Lifestream integration** — Travel directly to the selected datacenter and Horn using Lifestream.
- **Map markers** — Open the in-game map at the predicted pot location, either manually or automatically after traveling.
- **Overlay** — Displays the next upcoming pot for your current datacenter without needing the main window open.
- **Sortable table** — Sort instances by datacenter, zone, FATE/CE activity, next pot, or countdown.
- **Automatic refreshing** — Fetches updated tracker data every 45 seconds while the window is open, with additional refreshes based on in-game activity.

## Supported FATEs

| Area | FATEs |
|---|---|
| South Horn | Persistent Pots, Pleading Pots |
| North Horn | Daylight Pottery, In a Pot of Bother |

## Installation

Persistent Pots Tracker is a third-party Dalamud plugin and isn't available through the official plugin repository.

A custom repository URL and installation instructions will be added here when the first release is ready.

## Usage

Type `/pots` in-game to open the tracker.

### Main Window

The main window lists reported instances from your selected datacenters, including their latest FATE and CE activity, predicted next pot, and countdown.

You can sort the table, change datacenter filters, and refresh the data manually.

Select an instance to access the travel options:

- **Show on Map** — Opens the map with a flag at the predicted pot location.
- **Travel** — Sends a travel request through Lifestream to the selected datacenter and Horn.
- **Clear** — Clears your selection.

You can also double right-click an instance to travel directly.

The Refresh button is temporarily disabled while a request is running or during the API cooldown. Failed requests may take longer to retry.

### Lifestream Integration

Travel requires the [Lifestream](https://github.com/NightmareXIV/Lifestream) plugin to be installed and enabled.

Persistent Pots Tracker uses Lifestream to handle travel between datacenters and to North Horn or South Horn. When returning to your home datacenter, it attempts to use your home world.

After arriving, the plugin waits for the area to finish loading before opening the map at the predicted pot location.

Lifestream handles the actual travel process. Persistent Pots Tracker only sends the request.

### Overlay

The overlay shows the next predicted pot for your current datacenter, along with its zone and countdown.

You can keep it open while playing instead of checking the full tracker window. Clicking the overlay opens the main window.

### Automatic Instance Matching

The API uses instance identifiers that aren't directly available through Dalamud.

To work around this, the plugin compares the FATEs and Critical Engagements currently active in your instance against the activity reported by FFXIVTracker.

When it finds a unique match, that instance is highlighted in the table.

**This feature is experimental.** It can take some time to find a match, and sometimes it picks the wrong instance. If you're relying on a particular spawn, double-check the instance against the website.

## Things to Know

- Spawn times are estimates based on community-reported data, not guaranteed spawn times.
- Tracker information can be delayed or incomplete.
- Automatic instance matching isn't always accurate.
- Travel requires Lifestream, but the rest of the plugin can be used without it.
- An internet connection is required to retrieve tracker data.
- The plugin makes requests to a public community API and may temporarily stop refreshing if the API is unavailable or rate-limits requests.

## Credits

- [FFXIVTracker](https://tracker.xivstats.com/) — Tracker data and spawn information.
- [Dalamud](https://github.com/goatcorp/Dalamud) — Plugin framework.
- [Lifestream](https://github.com/NightmareXIV/Lifestream) — Optional travel integration.

This is an independent community project and isn't affiliated with Square Enix or FFXIVTracker.