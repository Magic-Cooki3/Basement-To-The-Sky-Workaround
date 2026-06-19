# BUGS STILL BEING FIXED, PLEASE REPORT ANYTHING YOU FIND

# Basement to the Sky Demo - Linux Workaround

> Forked from [Magic-Cooki3/Basement-To-The-Sky-Workaround](https://github.com/Magic-Cooki3/Basement-To-The-Sky-Workaround). This fork adapts the tools for Linux/Proton (path auto-detection, save-freeze fix, and patches for the missing video-callback and quest-progression bugs - see "What this fixes" below).

This repository contains tools and patches to fix the video upload mechanics for Linux/Proton users of the *Basement to the Sky* Demo.

## What this fixes
1. **VideoKit crashes/0-byte files:** The game normally records a video of your launch using VideoKit, which fails completely on Linux/Proton (`DllNotFoundException`), so the success callback that sets the internal `lastVid` reference never fires. Without it, `MyTubeUI.VideoUpload()` silently bails out immediately and the Upload button never works. The patch injects this missing step manually.
2. **Windows path separators:** Several file-path lookups (`MyTubeUI.VideoUpload`, `MyTubeUI.VidSelected`) hardcode a `/` → `\` replacement that breaks under Wine/Proton. The patch swaps the replacement direction so paths resolve correctly on Linux.
3. **Aggressive save wiping:** If the game fails certain checks, it wipes your save folder completely.
4. **Upload Queuing:** Normally, if you launch multiple times without uploading, you lose all previous rewards and only get the reward for your last flight. This patch introduces a **Queue System** that stores every flight's score. You can upload multiple times back-to-back to claim all your earned money and science! Only flights with a camera installed are queued.
5. **Quest progression / double-completion:** A guard flag prevents the video-upload quest from completing more than once, without relying on cross-runtime delegate references that crash under Unity's Mono runtime.

## Files included
- `Patcher.cs` and `patcher.csproj`: A Mono.Cecil patcher that modifies the game's `Assembly-CSharp.dll` to fix paths, bypass wipe logic, and inject the queue mechanics.
- `ModHelper.dll`: A custom library we inject into the game that handles the persistent score queue (`PendingScores.txt`).
- `fix_videos.sh`: A background bash script that creates fake black videos so the game thinks the upload succeeded.

## Installation

### Step 1: Copy ModHelper
Copy the included `ModHelper.dll` directly into the game's `Managed` directory. Adjust the path below to wherever your Steam library actually is (default Steam location shown, but any library works, e.g. `/games/SteamLibrary/...` - the patcher in Step 2 will find it automatically either way):
```bash
MANAGED="$HOME/.local/share/Steam/steamapps/common/Basement to the Sky Demo/Basement to the Sky Demo_Data/Managed"
cp ModHelper.dll "$MANAGED/ModHelper.dll"
```

### Step 2: Run the Patcher
You will need the `.NET 8.0 SDK` installed on your machine.

The patcher auto-detects your Steam library and the game install (same detection logic as `fix_videos.sh`), and handles backups itself - it creates `Assembly-CSharp.dll.bak` on first run, and automatically restores from it on every subsequent run before patching, so you can just re-run it any time without worrying about double-patching:

```bash
dotnet run
```

If auto-detection fails, pass the path explicitly:
```bash
dotnet run -- "/path/to/.../Managed/Assembly-CSharp.dll"
```

You should see output confirming 4 patches were applied:
```
  [PATCH 1] RecordUI: Score save -> AddScore with camera guard + NewVidRercorded
  [PATCH 2a] MyTubeUI: Score load -> PopScore
  [PATCH 2b] MyTubeUI: Conditioned uploadPannel hide
  [PATCH 2c] MyTubeUI: Fixed 1 path separator(s) for Linux
  [PATCH 3] QuestManager: Added _videoQuestDone guard
  [PATCH 4] MyTubeUI.VidSelected: Fixed 1 path separator(s)

Applied 4 patches. Saving...
Done.
```
(2a/2b/2c count as one combined fix-area but log individually; the exact count of path-separator fixes may vary by game version.)

### Step 3: Run the Watcher
Before launching the game, open a terminal and run the `fix_videos.sh` script:
```bash
./fix_videos.sh
```
It auto-detects your Steam library and the game's compatdata folder. Leave it running! This script dynamically feeds dummy videos to the game so the upload flow completes smoothly.

If auto-detection fails (e.g. an unusual Steam install location), override it manually:
```bash
GAMEDIR="/path/to/steamapps/compatdata/4385770/pfx/drive_c/users/steamuser/AppData/LocalLow/Orange3000k/Basement to the Sky Demo" ./fix_videos.sh
```

## How to use the Queue Feature
1. With the watcher running, launch the game.
2. Build and launch a rocket (with a camera installed - flights without one aren't queued). Your score is automatically queued.
3. Launch another rocket. It is also queued!
4. Go to the laptop and click "Upload". You will get the reward for your *first* launch. The upload button will **stay visible**.
5. Click it again to get the reward for your *second* launch! The button disappears when the queue is empty.

*Note: All uploaded videos will show the same black 5-second preview in the MyTube interface, but your rewards will correctly reflect your flights!*

## Troubleshooting

**Game freezes on "Save & Quit":** Make sure `BlockSave.BE2` in your save directory is NOT read-only. Older versions of `fix_videos.sh` set it to `chmod 444`, which blocks the normal save write and hangs the game. Fix with:
```bash
chmod 644 "<your save directory>/BlockSave.BE2"
```

**Upload button never appears:** Check the watcher terminal output and `Player.log` for clues. Confirm `ModHelper.dll` was actually copied into `Managed`, and that the patcher reported all 4 patches applied (not fewer - see the re-patching note in Step 2 above).

## Credits

Original tools and Windows/Proton groundwork by [Magic-Cooki3](https://github.com/Magic-Cooki3) ([Basement-To-The-Sky-Workaround](https://github.com/Magic-Cooki3/Basement-To-The-Sky-Workaround)). This fork builds on that work with Linux-specific fixes.
