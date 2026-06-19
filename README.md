# BUGS ARE BEING WORKED OUT, PLEASE BE PATIENT

# Basement to the Sky Demo - Linux Workaround

This repository contains tools and patches to fix the video upload mechanics for Linux/Proton users of the *Basement to the Sky* Demo.

## What this fixes
1. **VideoKit crashes/0-byte files:** The game normally records a video of your launch using VideoKit, which silently fails on Linux and produces 0-byte MP4 files. This causes the MyTube UI to crash or fail to upload.
2. **Aggressive save wiping:** If the game fails certain checks, it wipes your save folder completely.
3. **Upload Queuing:** Normally, if you launch multiple times without uploading, you lose all previous rewards and only get the reward for your last flight. This patch introduces a **Queue System** that stores every flight's score. You can upload multiple times back-to-back to claim all your earned money and science!

## Files included
- `Patcher.cs` and `patcher.csproj`: A Mono.Cecil patcher that modifies the game's `Assembly-CSharp.dll` to fix paths, bypass wipe logic, and inject the queue mechanics.
- `ModHelper.dll`: A custom library we inject into the game that handles the persistent score queue (`PendingScores.txt`).
- `fix_videos.sh`: A background bash script that creates fake black videos so the game thinks the upload succeeded.

## Installation

### Step 1: Copy ModHelper
Copy the included `ModHelper.dll` directly into the game's `Managed` directory:
```bash
cp ModHelper.dll "$HOME/.local/share/Steam/steamapps/common/Basement to the Sky Demo/Basement to the Sky Demo_Data/Managed/ModHelper.dll"
```

### Step 2: Run the Patcher
You will need the `.NET 8.0 SDK` installed on your machine.
Back up your original DLL and run the patcher from this directory:

```bash
MANAGED="$HOME/.local/share/Steam/steamapps/common/Basement to the Sky Demo/Basement to the Sky Demo_Data/Managed"
cp "$MANAGED/Assembly-CSharp.dll" "$MANAGED/Assembly-CSharp.dll.bak"

dotnet run "$MANAGED/Assembly-CSharp.dll"
```
You should see output confirming the patches were applied.

### Step 3: Run the Watcher
Before launching the game, open a terminal and run the `fix_videos.sh` script:
```bash
./fix_videos.sh
```
Leave it running! This script dynamically feeds dummy videos to the game so the upload flow completes smoothly.

## How to use the Queue Feature
1. With the watcher running, launch the game.
2. Build and launch a rocket. Your score is automatically queued.
3. Launch another rocket. It is also queued!
4. Go to the laptop and click "Upload". You will get the reward for your *first* launch. The upload button will **stay visible**.
5. Click it again to get the reward for your *second* launch! The button disappears when the queue is empty.

*Note: All uploaded videos will show the same black 5-second preview in the MyTube interface, but your rewards will correctly reflect your flights!*
