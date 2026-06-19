#!/usr/bin/env bash
# Basement to the Sky Demo - Linux Video Fix Watcher
# Run this BEFORE launching the game. Press Ctrl+C to stop.
#
# Auto-detects your Steam library. Override by exporting GAMEDIR yourself
# before running, e.g.: GAMEDIR="/custom/path/.../Basement to the Sky Demo" ./fix_videos.sh

APP_ID="4385770"
GAME_SUBPATH="AppData/LocalLow/Orange3000k/Basement to the Sky Demo"
FAKE_MP4="$HOME/fake_rocket.mp4"

find_gamedir() {
    # 1. Respect an explicitly exported GAMEDIR
    if [ -n "$GAMEDIR" ] && [ -d "$GAMEDIR" ]; then
        echo "$GAMEDIR"
        return 0
    fi

    # 2. Collect all known Steam library paths from libraryfolders.vdf
    local steam_roots=(
        "$HOME/.local/share/Steam"
        "$HOME/.steam/steam"
        "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam"
    )

    local libraryfolders=""
    for root in "${steam_roots[@]}"; do
        if [ -f "$root/steamapps/libraryfolders.vdf" ]; then
            libraryfolders="$root/steamapps/libraryfolders.vdf"
            break
        fi
    done

    local candidates=()
    if [ -n "$libraryfolders" ]; then
        while IFS= read -r path; do
            candidates+=("$path")
        done < <(grep -oP '"path"\s*"\K[^"]+' "$libraryfolders")
    fi
    # Always also consider the default roots themselves
    candidates+=("${steam_roots[@]}")

    for lib in "${candidates[@]}"; do
        local candidate="$lib/steamapps/compatdata/$APP_ID/pfx/drive_c/users/steamuser/$GAME_SUBPATH"
        if [ -d "$candidate" ]; then
            echo "$candidate"
            return 0
        fi
    done

    return 1
}

GAMEDIR="$(find_gamedir)"

if [ ! -f "$FAKE_MP4" ]; then
    echo "[!] Creating dummy MP4..."
    ffmpeg -y -f lavfi -i color=c=black:size=1280x720:rate=30 -t 5 -c:v libx264 -pix_fmt yuv420p "$FAKE_MP4" 2>/dev/null
fi

if [ -z "$GAMEDIR" ]; then
    echo "[!] Could not auto-detect the game data directory."
    echo "    Make sure you've launched the game at least once, or set it manually:"
    echo "    GAMEDIR=\"/path/to/.../$GAME_SUBPATH\" ./fix_videos.sh"
    exit 1
fi

# Ensure BlockSave.BE2 exists (must NOT be read-only - that freezes Save & Quit)
if [ ! -f "$GAMEDIR/BlockSave.BE2" ]; then
    touch "$GAMEDIR/BlockSave.BE2"
    echo "[+] Created BlockSave.BE2"
fi

# Fix existing 0-byte recordings
for f in "$GAMEDIR"/recording_*.mp4; do
    [ -f "$f" ] && [ ! -s "$f" ] && cp "$FAKE_MP4" "$f" && echo "[+] Fixed: $(basename "$f")"
done

# Ensure RocketLaunch.mp4 exists (the game looks for this specific file on upload)
if [ ! -f "$GAMEDIR/RocketLaunch.mp4" ]; then
    cp "$FAKE_MP4" "$GAMEDIR/RocketLaunch.mp4"
    echo "[+] Created RocketLaunch.mp4"
fi

echo ""
echo "=== Watcher active ==="
echo "Monitoring: $GAMEDIR"
echo "Press Ctrl+C to stop."
echo ""

while true; do
    # Replace any new 0-byte recording files
    for f in "$GAMEDIR"/recording_*.mp4; do
        [ -f "$f" ] && [ ! -s "$f" ] && cp "$FAKE_MP4" "$f" && echo "[+] Replaced: $(basename "$f")"
    done

    # Re-create RocketLaunch.mp4 if the game consumed it during an upload
    # (VideoUpload moves it to "Flight N.mp4", so it disappears after each upload)
    if [ ! -f "$GAMEDIR/RocketLaunch.mp4" ]; then
        cp "$FAKE_MP4" "$GAMEDIR/RocketLaunch.mp4"
        echo "[+] Re-created RocketLaunch.mp4 (consumed by upload)"
    fi

    sleep 0.2
done
