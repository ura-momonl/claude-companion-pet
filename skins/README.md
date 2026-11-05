# Make your own skin in 3 minutes

Any GIF works. You do not need to draw anything new or write any code.

1. **Have a GIF (or PNG pictures) per mood.** Name each file after the state it is for: `idle.gif` (always needed: it is shown whenever a state has no pictures of its own),
   `busy.gif` (Claude/Codex is working), `approval.gif` (waiting for you to approve), `answer.gif` (waiting for your answer), `done.gif`, `error.gif`, `quota.gif`.
   Only `idle` is required; add the others whenever you like. A second animation for the same state: `idle-stretch.gif`, `busy-2.gif` (the pet plays one, rests, plays another).
   A folder of PNG pictures works too (`busy\` with `0001.png`, `0002.png` ...). Transparent backgrounds are kept.
2. **Drag the files onto `Make-skin.cmd`** (next to `pet.exe`), type a name, press Enter. It makes `%APPDATA%\claude-companion-pet\skins\<name>\` (pictures cut out of the GIF, speed read from the GIF, big pictures shrunk to 160 px).
   From a terminal: `powershell -File make-skin.ps1 -Name mycat idle.gif busy.gif done.gif` (`-Size 128`, `-Pixel` for sharp pixel art with `-Scale 3`, `-FrameMs 100`; the header of the script lists them).
3. **Right-click the pet > Skin > your name.** Done. Too fast or slow: open `skin.txt` in the new folder and change `frame_ms`. Wrong feet height: see `align` below.

Prefer to start from a working example? Copy the **`skin-template`** folder (2 simple pictures per state) into the `skins` folder, rename it, replace the pictures, edit `skin.txt`.
The pictures only need to be PNG with a transparent background, about the same size within one skin, the figure standing on the bottom edge. Everything below is optional detail.

Share a skin: zip the folder and send it. Whoever receives it drops the folder into their `skins` folder; no program change is involved.

---

# Skins

A skin is a folder: `skins/<id>/<state>/*.png` (states: `idle busy approval answer done error quota`, plus the behaviour states `lift held drop happy sleepy asleep wake`) and an optional `skin.txt` with `key=value` lines. Drop a folder into `skins/` next to `pet.exe` (or into `%APPDATA%\claude-companion-pet\skins\`) and it appears in the right-click menu. No rebuild needed.

The header comment of `cs/Skin.cs` lists every `skin.txt` key (frame time, loop / ping-pong / once, gaps between animations, feet alignment, `<state>.from=` to reuse pictures of another folder, `<state>.rest=<folder>` for a looping rest animation between two animations).

Shipped: `robot` (default, a small lantern robot, 30x19 px pictures) and `sol` (a 128x128 pixel girl with short animations and a breathing rest loop; generated with the project owner's own PixelLab account and edited by hand).
