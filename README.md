# Retainer Repricer

Fully automated Dalamud plugin for Final Fantasy XIV that reprices and lists new items on the marketboard.

---

## Overview

Retainer Repricer handles the tedious work of repricing your retainer sales at the market. It works through each enabled retainer, opens the sell menus, checks current market prices, and lists or reprices items based on your settings. Just stand at a summoning bell, open your retainer list, and hit start—the plugin handles the rest.

---

## Key Features

- Automatically visits every enabled retainer and handles repricing or listing new items.
- Optionally runs for the retainer AutoRetainer has just processed, then returns control to AutoRetainer.
- Pulls live market data from Universalis to set competitive prices, with a configurable undercut amount.
- Smart Sort keeps your Sell List organized by prioritizing items that sell fast and for good prices—you can trigger it manually or let it run on a schedule.
- Settings apply instantly—no need to reload or restart anything.
- Right-click items in your inventory to add them to your Sell List directly from the game, with an optional modifier-key quick add.
- Checks that each game menu is ready before acting, keeping everything running smoothly.
- Built-in log viewer (`/repricer logs`) shows runtime messages for troubleshooting.

---

## Requirements & Setup

- Final Fantasy XIV running through XIVLauncher with Dalamud installed.
- Stand at a summoning bell and open your Retainer List before starting.
- Your retainers must already be unlocked and have available sell slots.
- Optional: enable Universalis in Settings for better pricing decisions and smart sorting.

### Building from source

Install a stable .NET 10 SDK and run XIVLauncher with Dalamud at least once so its development assemblies are available at `%APPDATA%\XIVLauncher\addon\Hooks\dev`.

```powershell
git submodule update --init --recursive
dotnet restore Parnell.sln --locked-mode
dotnet build Parnell.sln --configuration Release -p:Platform=x64 --no-restore
```

Open `Parnell.sln` in your IDE. The plugin is built to `RetainerRepricer/bin/x64/Release/RetainerRepricer.dll`; add that DLL to Dalamud's Dev Plugin Locations to test it in game.

For a custom Dalamud installation, pass `-p:DalamudLibPath="C:\path\to\Dalamud/"` to both restore and build so every project uses the same assemblies.

Both dependencies are pinned Git submodules. Repository-level overrides in `Directory.Build.targets` make AutoRetainerAPI use the same ECommons project as the plugin, update the analyzer package, and store dependency lock files under `build/locks`. After intentionally updating dependencies, run `dotnet restore Parnell.sln --force-evaluate` and commit the updated submodule pointers and lock files.

---

## MB Sell List

The items you want the plugin to list or reprice live here.

- Use the search box to find items by name or ID.
- Clear list requires clicking twice so you don't accidentally wipe everything.
- Each row shows priority, item name, how many you need in inventory before listing, and a remove button.
- When Smart Sort is active, priority numbers are just for reference—the plugin reorders them automatically.
- The Smart Sort button reorder your list based on how fast items sell and their average price. If it's disabled, check that Universalis is enabled in Settings.
- With per-retainer caps enabled in Settings, each Sell List item can have different listing limits per retainer, and you can set how many of an item to list in a single transaction.

---

## Retainers

- Shows every retainer currently visible in your Retainer List.
- Each retainer has three checkboxes:
  - **Enabled** – Whether the plugin touches this retainer at all.
  - **Allow reprice** – Whether to reprice existing listings.
  - **Allow sell** – Whether to list new items from your Sell List.
- Uncheck any retainer you don't want the plugin to touch.
- Your choices save right away.

---

## Settings

- **Plugin enabled** – Master toggle to enable or disable all automation, overlay, and context menu features.
- **Run after AutoRetainer processes a retainer** – Opts into single-retainer repricing and selling during AutoRetainer's post-processing step.
- **Show tooltips** – Turns on helpful tooltips when hovering over settings controls.
- **Close RetainerList on finish** – Automatically closes the Retainer List when a run completes.
- **Overlay** – Toggle the overlay that appears near bell menus (Start, Stop, Config buttons).
- **Quick add modifier key** – Lets you hold a configured modifier while right-clicking an inventory item to add it directly to the Sell List.
- **Universalis options** – Enable or disable price floor, Smart Sort, and fallback pricing when the board is empty.
- **Advanced** – Flip on this toggle to see more options if you need them.

---

## Advanced

These are fine-tuning options for users who want more control:

- Move the overlay around more precisely.
- Adjust how quickly the plugin clicks through menus.
- Tweak how Smart Sort calculates priority between sales speed and average price.
- Reset everything back to defaults if things feel off.

---

## Inventory Context Menu

When the plugin is on, right-clicking items in your inventory gives you extra options:

- Add to Sell List – choose how many you need before listing and optionally set a priority.
- Hold your configured modifier while right-clicking to add an item directly without selecting the menu entry.
- Quick add supports either-side and left/right-specific variants for `Shift`, `Ctrl`, and `Alt`.
- Items you can't sell (like items bound to your character) show as "Not Sellable."
- Items already in your list show a remove option instead.
- If Smart Sort is enabled, adding a new item can automatically reorganize your list.

---

## How It Works

1. Open the Retainer List at a summoning bell, then hit Start on the overlay or use the chat command.
2. The plugin figures out which retainers you've enabled.
3. For each retainer:
   - Opens the player's inventory and sell list, then reprices any existing listings based on current market data.
   - Checks your Sell List and lists any new items if you have enough in your inventory.
   - Confirms each price change through the game dialogs.
4. Closes all the menus and moves to the next retainer.
5. When done, cleans up and goes back to idle, leaving the Retainer List open or closed based on your preference.
6. Tells you what happened through chat messages.

---

## Commands

- `/repricer` or `/rr` — show help or prefix a command.
- `/repricer start` — start the automation. Runs both repricing and listing by default.
- `/repricer start price` — only reprice existing listings.
- `/repricer start sell` — only list new items from your Sell List.
- `/repricer stop` — stop and close any open menus.
- `/repricer config` or `/repricer c` — open the settings window.
- `/repricer logs` or `/repricer log` — open the in-game log viewer.
The overlay near the retainer list has the same Start, Stop, and Config buttons.

---

## Safety & Limitations

- Works only inside the game through Dalamud—no external automation.
- Handles high quality and normal quality items completely separately to avoid mistakes.
- Other plugins that mess with the same menus might conflict—finish one before starting another.
- Keep the timing settings as-is unless you know what you're doing; changing them can cause the plugin to act too fast for the game to handle.

---

## Troubleshooting

- **Start button does nothing** – Make sure the plugin is enabled in Settings and you have at least one retainer checked in the Retainers tab.
- **Stops partway through** – Open `/repricer logs` and use **Copy Visible** to check the plugin log; messages starting with `[RR]` explain what went wrong (usually a menu didn't open in time or something blocked it).
- **Smart Sort button is grayed out** – Universalis or Smart Sort might be turned off in Settings, or a sort is already running.
- **Prices seem wrong** – Make sure Universalis is enabled in Settings and reachable; otherwise the plugin uses whatever price is currently listed on the marketboard.
- **Items not listing** – Check that you actually have enough of the item in your inventory (your min-count setting), that it's the right quality (HQ vs NQ), and that it is not currently market prohibited or spiritbond-bound.
- **Overlay isn't showing** – Make sure the plugin is enabled and the Retainer List is open.

---

## License

Retainer Repricer is distributed under the AGPL-3.0-or-later license. See `LICENSE.md` for full terms.
