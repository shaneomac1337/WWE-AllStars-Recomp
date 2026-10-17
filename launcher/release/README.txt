WWE All Stars Recomp - PC (unofficial community build)
=====================================================

An unofficial PC version of WWE All Stars, made by statically recompiling
the Xbox 360 game. It does NOT include the game, its update or any DLC. You
need your own disc image (.iso) of WWE All Stars for Xbox 360, the
USA/Europe release, and the game's Title Update 2, both from your own
console.


WHAT YOU NEED
-------------
- Windows 10 or 11, 64-bit
- A graphics card with Vulkan support and an up-to-date driver
- About 8 GB of free disk space for the game files and shaders
- Your own WWE All Stars (USA, Europe) Xbox 360 disc image (.iso)
- Title Update 2 for it, dumped from your own console (see below)
- Optional: DLC packs you bought and dumped from your own console
- A controller is recommended


WHY TITLE UPDATE 2
------------------
Since version 1.0.0 the port is built on Title Update 2, the free
"Compatibility Pack" update WWE All Stars got on Xbox 360. It is the game's
last update, and the DLC wrestlers need it: the disc alone cannot load
DLC. The port only runs on the updated game, so the launcher asks for the
update during setup. It is not included and not downloaded: you dump it
from your own console, like the disc. It is the file tu00000005_00000000
(in Content\0000000000000000\54510866\000B0000 on a console drive or USB
stick), or a file whose name starts with TU_ in the console's Cache
folder. The launcher also takes a folder with it inside (a whole USB
Content folder works) or a zip, 7z or rar.


FIRST RUN
---------
1. Extract the whole zip to a normal folder, for example C:\Games\WWEAllStars.
   Do not run it from inside the zip, and avoid Program Files.
2. Double-click WWEAllStarsLauncher.exe.
3. Choose your .iso file (or drag it onto the window), then Title Update 2.
   Optionally add DLC packs too (you can also do that later). The launcher
   checks that the disc and the update are the supported ones, copies the
   game files into the "gamedata" folder and the update into "updatedata",
   verifies every file and prepares the shaders. This takes a few minutes
   and happens only once. Your files are never changed; you can keep them
   as a backup or delete them afterwards.
4. Choose Play.

After setup you can also start game\wweallstars.exe directly, for example
from a shortcut.


"WINDOWS PROTECTED YOUR PC"
---------------------------
This build is not code-signed, so Windows SmartScreen may show "Windows
protected your PC" the first time you start the launcher. Choose
"More info", then "Run anyway". You only need to do this once.


SETTINGS
--------
The launcher's Settings screen holds the options that apply when the game
starts: graphics preset, renderer, render resolution, supersampling,
texture filtering, colour, display mode, graphics card, language and online
play. Everything else
(VSync, sharpening, vibration, sound) is on the in-game PC SETTINGS page
(F4). Both use the same settings file, so they always agree.


ONLINE PLAY (EXPERIMENTAL)
--------------------------
1. In the launcher, open Settings and find ONLINE.
2. Turn "Online play" On. The launcher asks for a player name first: 1 to
   15 letters, digits and spaces, starting with a letter. Other players see
   it in the match lobby.
3. Choose Play, then Xbox LIVE > Player Match in the game's main menu.

The "Game server" is the project's server by default. To play on a
friend's server, type its address there (a host, host:port or an http(s)
URL); "Default" puts the project's server back. Under Play, the launcher
says whether the game server answers. If it doesn't, you can still play
offline.

The first time you play online, Windows may ask whether to let
wweallstars.exe through the firewall. Either answer works: if you decline,
or your router blocks other players, matches go through the game server's
relay instead. You never need to open ports. UPnP (Settings > ONLINE) is
optional and off by default; matches work without it.

You and your friend see each other's matches only when you both run the
same release (About shows the version and commit). The game server keeps
your player name, an ID made for this install and your addresses, in
memory only; they are gone when the server restarts.


DLC
---
Choose DLC on the launcher's home screen to add the DLC packs you bought,
dumped from your own console: the package files (in
Content\0000000000000000\54510866\00000002), a folder with them, or a zip,
7z or rar. Each pack is checked block by block before it is added; a
damaged copy is refused. The DLC wrestlers then appear behind the roster's
DLC slot. Remove takes a pack out again. Saves made with a DLC wrestler
still load without the pack; the game asks you to pick another wrestler.


MODS
----
Choose MODS on the launcher's home screen to add mods: drop a zip, 7z or
rar, or a folder, onto the page, or use "Add mod..." or "Add folder...".
New mods start off; turn them on with their toggle. They are used the next
time you choose Play, and your game files are never changed. When two mods
replace the same file, the one lower in the list wins. With online play on,
only mods that change looks load. Remove moves a mod to the Recycle Bin.

To make a mod, read the modding guide and the example mod on the project's
GitHub page:
https://github.com/shaneomac1337/WWE-AllStars-Recomp/blob/main/docs/modding.md


IF SOMETHING GOES WRONG
-----------------------
- "Repair" in the launcher re-checks your game files and Title Update 2 and
  copies back any that are damaged (it asks for your .iso or the update
  again only when one of their files is bad).
- "Open logs" opens the folder with the newest game session's log. Attach
  it to a bug report on the project's GitHub page. A log can contain folder
  paths that show your Windows user name; check it before posting it.


FOLDERS
-------
WWEAllStarsLauncher.exe   the launcher
game\                     the PC program
xenosrecomp\              the shader converter setup uses
gamedata\                 game files from your disc (created by setup)
updatedata\               Title Update 2's files (created by setup)
userdata\                 your saves and launcher state
cache\                    shaders (rebuilt by the launcher if deleted)
logs\                     logs of recent game sessions
mods\                     your mods (created when you add one)
licenses\                 licences of this project and its components

To move to a newer version, keep your "userdata" folder (your saves) and
copy it into the new folder.


LEGAL
-----
This project is not affiliated with or endorsed by WWE, THQ, Yuke's or
Microsoft. No game data is included: you must own the game and use your own
disc image, update and DLC. Do not share the gamedata or updatedata
folders, a disc image, the update or DLC packs.

WWE All Stars Recomp is BSD-3-Clause licensed. Licences of the components
it uses are in the "licenses" folder.
