#!/usr/bin/env bash
# Basement to the Sky Demo - Linux Video Fix Watcher
# Run this BEFORE launching the game. Press Ctrl+C to stop.

GAMEDIR="$HOME/.local/share/Steam/steamapps/compatdata/4385770/pfx/drive_c/users/steamuser/AppData/LocalLow/Orange3000k/Basement to the Sky Demo"
FAKE_MP4="$HOME/fake_rocket.mp4"

if [ ! -f "$FAKE_MP4" ]; then
    echo "[!] Creating dummy MP4..."
    ffmpeg -y -f lavfi -i color=c=black:size=1280x720:rate=30 -t 5 -c:v libx264 -pix_fmt yuv420p "$FAKE_MP4" 2>/dev/null
fi

if [ ! -d "$GAMEDIR" ]; then
    echo "[!] Game data directory not found. Launch the game at least once first."
    exit 1
fi

# Ensure BlockSave.BE2 exists and is read-only
if [ ! -f "$GAMEDIR/BlockSave.BE2" ]; then
    touch "$GAMEDIR/BlockSave.BE2"
    chmod 444 "$GAMEDIR/BlockSave.BE2"
    echo "[+] Created read-only BlockSave.BE2"
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
