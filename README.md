# Skateboard Art 0.2.3 for BOXROOM

Adds editable custom artwork to BOXROOM's three wall-mounted skateboard placeables.

## Use

1. Place or look at a wall-mounted skateboard.
2. Use **Alternate Interact** (the same control BOXROOM uses for custom-image frames).
3. Choose **Change Art**. The editor hands off to BOXROOM's native Custom Image browser and painter path.
4. Adjust **Move X**, **Move Y**, and **Zoom**.
5. Select **Save & Close**.

Each skateboard stores its own image choice and crop settings in the BOXROOM room save. Imported images are copied to `UserData\SkateboardArt\Images` so moving the original file does not break the room.

Press Escape or choose Cancel to discard changes made during the current editing session. **Reload Saved** restores the last values saved in the editor. **Use Stock Art** removes the custom image from that skateboard.

## Install

Install through BoxMate, or copy `BR_SkateboardArt.dll` into the BOXROOM `Mods` folder. MelonLoader and ModsPanel 2.7.0 or newer are required; BoxMate installs the dependency automatically.

The editor is rendered by ModsUi using its compact draggable-window mode, so it retains BOXROOM styling, controller focus, cursor ownership, and restoration of the input map that was active before opening it.

## Current validation

The complete 0.2.2 workflow has been confirmed in BOXROOM: Change Art uses the native custom-image route, Move X/Y and Zoom update the live skateboard, and room save/load restores both the selected art and its transform values.

Version 0.2.1 binds transforms to the native painter's renderer/material slot and applies pan/zoom through a per-renderer property block, so the controls continue targeting the live material instance created by BOXROOM.

Version 0.2.2 extends the native `GameImagePainter` save payload with the per-skateboard X/Y and zoom values. Older saves retain their selected artwork and begin persisting transforms after the artwork is adjusted and the room is saved again.

Version 0.2.3 keeps the editor bound to the live image painter that opened it and reacquires a valid painter when necessary, so **Change Art** continues to work when BOXROOM has replaced an earlier collider painter.
