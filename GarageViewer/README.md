# Garage Viewer

FH6 C_ProfileData support in Forza Mod Tool 1.2.6. Motorsport save files are not supported.

1. Back up the entire save folder. Load an encrypted or decrypted C_ProfileData into Garage Viewer.
2. Expand manufacturer groups to browse owned cars and the stock catalog. Search by manufacturer, friendly name, year, car ID or garage instance ID; matching groups expand automatically. Names are shared with Car Editor; manufacturer grouping uses Data_Car.MakeID and List_CarMake, not guesses from the model name.
3. Ctrl-click or Shift-click to select multiple cars in either list. Add selected cars adds one stock instance per selected model in one transaction; an invalid configuration or failed insert rolls back the entire batch. Manufacturer headers offer Select all (catalog) or Select removable (garage, skipping protected/unknown models). Duplicate and Set as current car remain single-selection actions. All edits are on a private working copy.
   Set as current car updates /Main/CareerCar using the selected garage instance ID, not its model ordinal or general Flags value. Pending-car references remain protected and unchanged. Revert restores the original selection.
4. Export edited save writes a new file in the input's encrypted/decrypted format. Existing files are never overwritten. Revert all edits restores the original snapshot taken at load.
5. Close the game before installing any exported save, and test carefully. Keep the original complete backup.

Load GameDB catalog accepts a full FH6 .slt/.sqlite for newer or custom cars and matching stock part IDs. Version 1.2.6 defaults to the verified 682-model FH6 catalog and retains the older 671-model clean reference. Future game updates or custom configurations may still require your matching catalog. Incomplete or ambiguous stock configurations are rejected, not guessed. Duplicate retains the selected instance's tuning/configuration and purchased parts, but creates a new instance ID and VIN; shared tune/livery files are not copied or deleted.

## Actual garage thumbnails

Drag the divider above the selected-car fields up/down to resize the owned list. Drag the divider beside the stock catalog left/right to resize its width. Collapsing the fields gives their space to the list; reopening them restores the resized split for this session. Both scrollbar orientations use the same slim, rounded dark treatment and pink drag highlight as Profile Editor.

The Preview column and selected-car panel read the game's local `CacheThumbnails` folder. By default this is `%LOCALAPPDATA%\ForzaHorizon6\LocalStorage_Cache\CacheThumbnails`; **Thumbnail cache folder…** selects a different location. The tool matches each saved `Career_Garage.Thumbnail` CTN reference to the cache manifest and its WebP file. This shows cached modifications/paint/designs, not a guessed stock image based on model ID. Duplicate models can have different previews.

Only visible/selected previews are decoded, at a bounded display size. The cache is read-only and its images are not copied into exported saves or the release ZIP. Missing/stale thumbnails show a placeholder; newly added cars may need the game to generate their preview. Only the verified version-2 manifest lookup is supported. A Windows WebP decoder is required for images; if unavailable, garage editing still works without previews. The tool does not render new thumbnails from a car's configuration.

Selecting one car displays a larger, higher-detail preview beneath its fields. It grows with the window and selected-car panel; drag the divider above the fields upward for more image space. The large preview has its own bounded cache, so the list does not retain full-size images for every car. Multi-selection clears the single-car preview.

Small and large previews preserve the WebP's original transparency, including soft shadows and translucent edges. No black color-keying is applied to the car's paint or glass; the cache files are never changed.

## Selected-car fields

Select one or more garage instances to edit **Original Owner**, **Distance driven**, and **Top speed** from `Career_Garage`. Original Owner is the stored text label, not the save/account identity. Distance Driven is a non-negative whole number up to 2147483647. Top Speed is a finite non-negative decimal and accepts either a dot or comma on either regional layout. Both numeric fields show **raw stored DB units**, matching the advanced Profile Editor rather than assuming a unit conversion.

For multiple selected cars, check only the fields you want replaced on every selected instance. Typing into a batch field checks it automatically; uncheck it to leave each car's existing value alone. Mixed values show **Mixed — unchanged** until chosen. An explicitly checked empty Original Owner clears that label; empty numeric fields are invalid. **Apply checked fields** uses one transaction: a validation or write failure cancels the entire batch. Duplicate and Set as current car remain single-selection actions.

**Apply car fields** saves to the private working copy. Selecting another car or exporting also validates/applies a pending draft to its original selection. Invalid input blocks that action and retains both the draft and selection. **Export edited save** is still required to create the output file. **Revert all edits** restores the loaded snapshot. Unknown car-detail storage types disable these fields without guessing how to write them.

## Car Editor resize handles

Car Editor also has vertical dividers between its car picker, options and activity log. Drag the grip immediately below the engine/motor list to resize its height. Minimum sizes keep the panels usable; these layout adjustments last for the current session and do not change database contents.

Removal deletes only selected Career_Garage rows and their Career_PurchasedParts records. Current/pending car references, barn-find VINs and GameDB removal locks are protected. Unknown models require a matching catalog before UI removal. Unknown garage-linked schemas disable editing. Existing orphan part history and other progression tables are left alone.

Export verifies payload round-trip and byte-exact preservation of account/typed-profile, BXML and binary-career sections, except for an explicitly selected /Main/CareerCar leaf. Reversing that one leaf on a verification copy must reproduce the original profile bytes exactly. Garage counts cached in progress, collection achievements/rewards and VIN-keyed perks are not modified. JXRDN confirmed garage Add/Remove, changing the current car, and Original Owner/history-field edits working in game. This does not guarantee compatibility with every save layout or manual edit; keep full untouched backups.

## Recovery after incompatible SQL/GameDB edits

For a save that fails to load because an owned garage car references an incompatible modified configuration, work on a copy, load a matching full GameDB catalog, select a healthy current car if needed, and remove the affected removable instance. Export an encrypted copy and test it with a compatible GameDB after backing up the entire live save. A decrypted full-profile export must first be re-encrypted in Crypto.

This can help with bad garage-car references caused by SQL Live Editor or GameDB edits, not every kind of save corruption. It cannot repair unreadable containers, damaged SQLite, missing data, unrelated progression corruption, or protected references it cannot safely remove. Restore a known-good full backup if the tool cannot load the save.

Temporary work prefers A:\Forza Mod Tool Temp\Work when A: exists. Use the bundled Start Forza Mod Tool.cmd for pre-start .NET bundle extraction on that drive too. FORZA_MOD_TOOL_TEMP selects another absolute workspace root; process TEMP/TMP are isolated from Windows' global settings.
