# Fix: Video Upload on Linux/Proton (Basement to the Sky Demo)

The in-game MyTube laptop never shows the upload button on Linux. This is caused by three separate issues that all need to be fixed together.

## Root Cause

**1. VideoKit recording produces 0-byte MP4 files.** The game uses a Unity package called VideoKit to record rocket flights. VideoKit phones home to videokit.ai to validate a license token before it will record anything. Under Proton this validation either fails silently or never completes, so the recorder creates an empty file and never writes any frames to it.

**2. The "new video recorded" callback never fires.** Because the recorder never actually starts, the callback chain that tells the MyTube UI "a new video is ready to upload" never executes. The upload panel is gated behind a boolean (`isNewVid`) that only gets set when `GameManager.S.NewVidRercorded()` is called at the end of a successful recording. Since recording fails, this call never happens, and the upload button never appears.

**3. Path separators are hardcoded to Windows backslashes.** Several methods in MyTubeUI call `text.Replace("/", "\\")` on file paths before checking `File.Exists()`. Under Proton, forward slashes work fine, but replacing them with backslashes can break path resolution depending on the Wine/Proton version.

**4. The game wipes save data aggressively.** On every new game start or version mismatch, `MainMenuUI.ResetAllDataExceptEssential()` deletes every file and folder in the save directory that is not in a small whitelist. This kills your recordings, your save file, and your rocket launch thumbnail.

## Prerequisites

- .NET SDK 8.0 (used to build the patcher, not needed at runtime)
- ffmpeg (to generate a dummy MP4)

Install the .NET SDK locally without root:

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
chmod +x /tmp/dotnet-install.sh
/tmp/dotnet-install.sh -c 8.0 --install-dir ~/.dotnet
export DOTNET_ROOT=~/.dotnet
export PATH=$DOTNET_ROOT:$PATH
```

## Step 1: Build the Patcher

Create a C# console project that uses Mono.Cecil to rewrite the game's compiled code:

```bash
mkdir -p /tmp/patcher && cd /tmp/patcher
dotnet new console
dotnet add package Mono.Cecil
```

Replace the contents of `/tmp/patcher/Program.cs` with:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Patcher
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: Patcher <path to Assembly-CSharp.dll>");
                return;
            }

            string targetFile = args[0];

            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(System.IO.Path.GetDirectoryName(targetFile));

            using (var assembly = AssemblyDefinition.ReadAssembly(targetFile,
                new ReaderParameters { ReadWrite = true, AssemblyResolver = resolver }))
            {
                var module = assembly.MainModule;
                int fixes = 0;

                // PATCH 1: Fix path separators in MyTubeUI
                // Replace("/", "\\") becomes Replace("/", "/") which is a no-op
                foreach (var type in module.Types)
                {
                    foreach (var method in type.Methods)
                    {
                        if (!method.HasBody) continue;
                        var instructions = method.Body.Instructions;
                        for (int i = 1; i < instructions.Count; i++)
                        {
                            var inst = instructions[i];
                            if (inst.OpCode == OpCodes.Ldstr && (string)inst.Operand == "\\")
                            {
                                if (instructions[i - 1].OpCode == OpCodes.Ldstr
                                    && (string)instructions[i - 1].Operand == "/")
                                {
                                    Console.WriteLine($"  [PATH] Fixed separator in {type.Name}::{method.Name}");
                                    inst.Operand = "/";
                                    fixes++;
                                }
                            }
                        }
                    }
                }

                // PATCH 2: Inject NewVidRercorded("RocketLaunch") into
                // VideoKitTest.Rocket_OnRetriveRocketActive so the MyTube UI
                // gets notified even when VideoKit recording fails silently
                var videoKitTestType = module.Types.FirstOrDefault(t => t.Name == "VideoKitTest");
                var gameManagerType = module.Types.FirstOrDefault(t => t.Name == "GameManager");

                if (videoKitTestType != null && gameManagerType != null)
                {
                    var rocketMethod = videoKitTestType.Methods
                        .FirstOrDefault(m => m.Name == "Rocket_OnRetriveRocketActive");
                    var getSMethod = gameManagerType.Methods
                        .FirstOrDefault(m => m.Name == "get_S");
                    var newVidMethod = gameManagerType.Methods
                        .FirstOrDefault(m => m.Name == "NewVidRercorded");

                    if (rocketMethod != null && getSMethod != null
                        && newVidMethod != null && rocketMethod.HasBody)
                    {
                        var processor = rocketMethod.Body.GetILProcessor();
                        var retInst = rocketMethod.Body.Instructions.Last(i => i.OpCode == OpCodes.Ret);

                        processor.InsertBefore(retInst,
                            processor.Create(OpCodes.Call, module.ImportReference(getSMethod)));
                        processor.InsertBefore(retInst,
                            processor.Create(OpCodes.Ldstr, "RocketLaunch"));
                        processor.InsertBefore(retInst,
                            processor.Create(OpCodes.Callvirt, module.ImportReference(newVidMethod)));

                        Console.WriteLine("  [INJECT] Added NewVidRercorded call");
                        fixes++;
                    }
                }

                // PATCH 3: Neutralize file/directory deletion in
                // MainMenuUI.ResetAllDataExceptEssential to stop save wiping
                var mainMenuType = module.Types.FirstOrDefault(t => t.Name == "MainMenuUI");
                if (mainMenuType != null)
                {
                    var resetMethod = mainMenuType.Methods
                        .FirstOrDefault(m => m.Name == "ResetAllDataExceptEssential");
                    if (resetMethod != null && resetMethod.HasBody)
                    {
                        var processor = resetMethod.Body.GetILProcessor();
                        foreach (var inst in resetMethod.Body.Instructions.ToList())
                        {
                            if (inst.OpCode == OpCodes.Callvirt
                                && inst.Operand is MethodReference methodRef
                                && methodRef.Name == "Delete")
                            {
                                if (methodRef.DeclaringType.FullName == "System.IO.FileSystemInfo")
                                {
                                    processor.Replace(inst, processor.Create(OpCodes.Pop));
                                    Console.WriteLine("  [DELETE] Neutralized FileInfo.Delete");
                                    fixes++;
                                }
                                else if (methodRef.DeclaringType.FullName == "System.IO.DirectoryInfo")
                                {
                                    var pop1 = processor.Create(OpCodes.Pop);
                                    var pop2 = processor.Create(OpCodes.Pop);
                                    processor.InsertBefore(inst, pop1);
                                    processor.Replace(inst, pop2);
                                    Console.WriteLine("  [DELETE] Neutralized DirectoryInfo.Delete");
                                    fixes++;
                                }
                            }
                        }
                    }
                }

                if (fixes > 0)
                {
                    Console.WriteLine($"\nApplied {fixes} patches. Saving...");
                    assembly.Write();
                    Console.WriteLine("Done.");
                }
            }
        }
    }
}
```

## Step 2: Patch the DLL

Back up the original DLL, then run the patcher against it:

```bash
MANAGED="$HOME/.local/share/Steam/steamapps/common/Basement to the Sky Demo/Basement to the Sky Demo_Data/Managed"
cp "$MANAGED/Assembly-CSharp.dll" "$MANAGED/Assembly-CSharp.dll.bak"

export DOTNET_ROOT=~/.dotnet
export PATH=$DOTNET_ROOT:$PATH
cd /tmp/patcher
dotnet run "$MANAGED/Assembly-CSharp.dll"
```

You should see output confirming 7 patches applied (4 path fixes, 1 callback injection, 2 delete neutralizations).

## Step 3: Create the Dummy MP4 and Watcher Script

The game will still create 0-byte MP4 files every time you launch a rocket. These need to be replaced with a valid video file before the MyTube UI tries to load them, otherwise the video player crashes.

Generate a 5-second black video:

```bash
ffmpeg -y -f lavfi -i color=c=black:size=1280x720:rate=30 -t 5 -c:v libx264 -pix_fmt yuv420p ~/fake_rocket.mp4
```

Create the BlockSave.BE2 file if it does not exist (prevents a code path that can wipe saves):

```bash
GAMEDIR="$HOME/.local/share/Steam/steamapps/compatdata/4385770/pfx/drive_c/users/steamuser/AppData/LocalLow/Orange3000k/Basement to the Sky Demo"
touch "$GAMEDIR/BlockSave.BE2"
chmod 444 "$GAMEDIR/BlockSave.BE2"
```

Replace any existing 0-byte recordings:

```bash
for f in "$GAMEDIR"/recording_*.mp4; do
    [ -f "$f" ] && [ ! -s "$f" ] && cp ~/fake_rocket.mp4 "$f"
done
```

## Step 4: Run the Watcher Before Launching

Open a terminal and run this before starting the game. It polls the save directory every 200ms and replaces new 0-byte recordings as they appear:

```bash
GAMEDIR="$HOME/.local/share/Steam/steamapps/compatdata/4385770/pfx/drive_c/users/steamuser/AppData/LocalLow/Orange3000k/Basement to the Sky Demo"
while true; do
    for f in "$GAMEDIR"/recording_*.mp4; do
        [ -f "$f" ] && [ ! -s "$f" ] && cp ~/fake_rocket.mp4 "$f" && echo "Replaced: $(basename "$f")"
    done
    sleep 0.2
done
```

Leave this running, launch the game through Steam, build and launch a rocket, then open the laptop and go to MyTube. The upload button will appear. The video preview will be a black screen but the upload completes and the quest progresses normally.

Press Ctrl+C in the terminal to stop the watcher when you are done playing.

## Notes

- The .NET SDK is only needed to build and run the patcher. It is not needed at runtime and can be removed after patching.
- If a game update replaces Assembly-CSharp.dll, you will need to re-run the patcher.
- The upload button can be clicked multiple times per video. This is a minor side effect of bypassing the normal recording callback flow.
- Tested on Garuda Linux (Arch-based) with Proton and Wine Staging 11.11.
