# Writes frame-anchored scripted-pad routes (graphics-remaster ticket 03) into $Dir as p1.txt..pN.txt:
#   4way  Fatal 4 Way, four scripted pads (--wwe_input_script_p2..p4), all still once the arena is picked
#   1v1   1 vs. 1 with two scripted pads, both still
#   cas   Create-a-Superstar: Create New (needs --headless=true --headless_keyboard_text=<name>), the six
#         clothing templates cycled three times, then the HEAD tab's face close-up from ~frame 13200
# p1.txt goes to session.ps1 input; the others are passed as --wwe_input_script_pN. Run with --headless=true:
# the game asks extra pads to sign in to Xbox LIVE, and the answer dialog otherwise pauses the match.
param([string]$Dir, [ValidateSet('4way', '1v1', 'cas')][string]$Kind = '4way')
$root = Split-Path $PSScriptRoot -Parent
$menu = @(Get-Content "$root/scripts/input/menus-frames.txt" | Where-Object { $_ -and $_ -notmatch '^#' })
# menus-frames ends on the STANDARD menu (1 vs. 1 highlighted) by frame ~5400.
if ($Kind -eq '4way') {
    $p1 = $menu + @('at 5500', 'press DOWN 6f', 'at 5560', 'press DOWN 6f', 'at 5620', 'press A 6f',
        'at 5950', 'press LEFT 6f', 'at 6000', 'press LEFT 6f', 'at 6250', 'press A 6f',
        'at 6600', 'press A 6f', 'at 7000', 'press A 6f')
    $others = @(
        @('at 5950', 'press LEFT 6f', 'at 6600', 'press A 6f'),
        @('at 5950', 'press RIGHT 6f', 'at 6600', 'press A 6f'),
        @('at 5950', 'press RIGHT 6f', 'at 6000', 'press RIGHT 6f', 'at 6600', 'press A 6f'))
} elseif ($Kind -eq 'cas') {
    # Main menu on EXHIBITION at 4600 (after UP); CREATE A SUPERSTAR is four down.
    $exhibition = $menu.IndexOf('at 4800')
    if ($exhibition -lt 0) { throw "scripts/input/menus-frames.txt has no 'at 4800' (EXHIBITION) line to cut the CAS route at." }
    $p1 = @($menu | Select-Object -First $exhibition) + @('at 4700', 'press DOWN 6f', 'at 4760', 'press DOWN 6f',
        'at 4820', 'press DOWN 6f', 'at 4880', 'press DOWN 6f', 'at 5000', 'press A 6f', 'at 6400', 'press A 6f',
        'at 7400', 'press RB 6f', 'at 7600', 'press DOWN 6f', 'at 7700', 'press DOWN 6f', 'at 7800', 'press A 6f')
    # Cycle the clothing templates: each loads a whole attire.
    # Down to template 6 and back up, three times: 30 attire loads, one per 150 frames (8100-12450).
    $f = 8100
    foreach ($pass in 1..3) {
        foreach ($move in @('DOWN') * 5 + @('UP') * 5) { $p1 += @("at $f", "press $move 6f"); $f += 150 }
    }
    # Out of the list and on to HEAD: the face close-up (from ~13200).
    $p1 += @('at 12700', 'press B 6f', 'at 12900', 'press RB 6f')
    $others = @()
} else {
    $p1 = $menu + @('at 5620', 'press A 6f', 'at 5950', 'press LEFT 6f', 'at 6250', 'press A 6f',
        'at 6600', 'press A 6f', 'at 7000', 'press A 6f')
    $others = @(, @('at 5950', 'press RIGHT 6f', 'at 6600', 'press A 6f'))
}
New-Item -ItemType Directory -Force $Dir | Out-Null
Set-Content "$Dir/p1.txt" $p1
for ($i = 0; $i -lt $others.Count; $i++) { Set-Content "$Dir/p$($i + 2).txt" $others[$i] }
