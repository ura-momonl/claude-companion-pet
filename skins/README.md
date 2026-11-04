# Skins

A skin is a folder: `skins/<id>/<state>/*.png` (states: `idle busy approval answer done error quota`, plus the behaviour states `lift held drop happy sleepy asleep wake`) and an optional `skin.txt` with `key=value` lines. Drop a folder into `skins/` next to `pet.exe` (or into `%APPDATA%\claude-companion-pet\skins\`) and it appears in the right-click menu. No rebuild needed.

The header comment of `cs/Skin.cs` lists every `skin.txt` key (frame time, loop / ping-pong / once, gaps between animations, feet alignment, `<state>.from=` to reuse pictures of another folder, `<state>.rest=<folder>` for a looping rest animation between two animations).

Shipped: `robot` (default, a small lantern robot, 30x19 px pictures) and `sol` (a 128x128 pixel girl with short animations and a breathing rest loop; generated with the project owner's own PixelLab account and edited by hand).
