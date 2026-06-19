using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Patcher {
    class Program {
        static void Main(string[] args) {
            string targetFile = args.Length > 0 ? args[0] :
                "/home/magiccookie/.local/share/Steam/steamapps/common/Basement to the Sky Demo/Basement to the Sky Demo_Data/Managed/Assembly-CSharp.dll";
            string modHelperFile = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(targetFile), "ModHelper.dll");

            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(System.IO.Path.GetDirectoryName(targetFile));

            using (var assembly = AssemblyDefinition.ReadAssembly(targetFile,
                new ReaderParameters { ReadWrite = true, AssemblyResolver = resolver }))
            using (var modAssembly = AssemblyDefinition.ReadAssembly(modHelperFile)) {
                var module = assembly.MainModule;
                int fixes = 0;

                // Import ModHelper methods
                var modType = modAssembly.MainModule.Types.First(t => t.Name == "QueueModHelper");
                var addScoreRef = module.ImportReference(modType.Methods.First(m => m.Name == "AddScore"));
                var popScoreRef = module.ImportReference(modType.Methods.First(m => m.Name == "PopScore"));
                var hasMoreScoresRef = module.ImportReference(modType.Methods.First(m => m.Name == "HasMoreScores"));

                // Get GameManager references for camera check
                var gameManagerType = module.Types.First(t => t.Name == "GameManager");
                var gmSProp = gameManagerType.Properties.First(p => p.Name == "S");
                var gmSGetter = module.ImportReference(gmSProp.GetMethod);
                var isDicaField = module.ImportReference(gameManagerType.Fields.First(f => f.Name == "isDicaInstalled"));
                var isRocketCamField = module.ImportReference(gameManagerType.Fields.First(f => f.Name == "isRocketCamInstalled"));

                // ============================================================
                // PATCH 1: RecordUI.S_OnRocketLanded
                // Only queue the score if a camera is installed (so non-filmed
                // flights don't count as uploadable).
                // Original: ES3.Save("Score", this.score)
                // New:      if (GameManager.S.isDicaInstalled || GameManager.S.isRocketCamInstalled)
                //               QueueModHelper.AddScore(this.score);
                // ============================================================
                var recordUIType = module.Types.FirstOrDefault(t => t.Name == "RecordUI");
                if (recordUIType != null) {
                    var method = recordUIType.Methods.FirstOrDefault(m => m.Name == "S_OnRocketLanded");
                    if (method != null && method.HasBody) {
                        var processor = method.Body.GetILProcessor();
                        var insts = method.Body.Instructions.ToList();
                        for (int i = 0; i < insts.Count; i++) {
                            if (insts[i].OpCode == OpCodes.Ldstr && (string)insts[i].Operand == "Score") {
                                var ldstrInst = insts[i];     // ldstr "Score"
                                var ldarg0 = insts[i+1];      // ldarg.0
                                var ldfldScore = insts[i+2];  // ldfld score
                                var callSave = insts[i+3];    // call ES3::Save

                                if (callSave.OpCode == OpCodes.Call &&
                                    callSave.Operand is MethodReference mr && mr.Name == "Save") {

                                    var retInst = insts.Last(x => x.OpCode == OpCodes.Ret);

                                    // Remove ldstr "Score" -- we don't need ES3 key
                                    processor.Remove(ldstrInst);
                                    // Replace call ES3.Save with call AddScore
                                    processor.Replace(callSave, processor.Create(OpCodes.Call, addScoreRef));

                                    // Re-fetch after modification
                                    insts = method.Body.Instructions.ToList();
                                    // Find the ldarg.0 that is now at the old position
                                    var loadThis = insts.First(x => x.OpCode == OpCodes.Ldarg_0 &&
                                        x.Next != null && x.Next.OpCode == OpCodes.Ldfld &&
                                        x.Next.Operand is FieldReference fr2 && fr2.Name == "score");

                                    // Insert camera check before the score load
                                    var ldGmS1 = processor.Create(OpCodes.Call, gmSGetter);
                                    var ldDica = processor.Create(OpCodes.Ldfld, isDicaField);
                                    var brDica = processor.Create(OpCodes.Brtrue_S, loadThis);
                                    var ldGmS2 = processor.Create(OpCodes.Call, gmSGetter);
                                    var ldRCam = processor.Create(OpCodes.Ldfld, isRocketCamField);
                                    // Find the ret that follows the AddScore call
                                    insts = method.Body.Instructions.ToList();
                                    var finalRet = insts.Last(x => x.OpCode == OpCodes.Ret);
                                    var brNoCam = processor.Create(OpCodes.Brfalse_S, finalRet);

                                    processor.InsertBefore(loadThis, ldGmS1);
                                    processor.InsertBefore(loadThis, ldDica);
                                    processor.InsertBefore(loadThis, brDica);
                                    processor.InsertBefore(loadThis, ldGmS2);
                                    processor.InsertBefore(loadThis, ldRCam);
                                    processor.InsertBefore(loadThis, brNoCam);

                                    // Also inject GameManager.S.NewVidRercorded("RocketLaunch")
                                    // after AddScore so the upload button appears on Linux
                                    // (VideoKit's recording callback never fires on Proton)
                                    var newVidMethod = gameManagerType.Methods.First(
                                        m => m.Name == "NewVidRercorded");
                                    insts = method.Body.Instructions.ToList();
                                    var addScoreCall = insts.First(x =>
                                        x.OpCode == OpCodes.Call &&
                                        x.Operand is MethodReference amr &&
                                        amr.Name == "AddScore");
                                    var afterAddScore = addScoreCall.Next; // the ret

                                    processor.InsertBefore(afterAddScore,
                                        processor.Create(OpCodes.Call, gmSGetter));
                                    processor.InsertBefore(afterAddScore,
                                        processor.Create(OpCodes.Ldstr, "RocketLaunch"));
                                    processor.InsertBefore(afterAddScore,
                                        processor.Create(OpCodes.Callvirt,
                                            module.ImportReference(newVidMethod)));

                                    fixes++;
                                    Console.WriteLine("  [PATCH 1] RecordUI: Score save -> AddScore with camera guard + NewVidRercorded");
                                    break;
                                }
                            }
                        }
                    }
                }

                // ============================================================
                // PATCH 2: MyTubeUI.VideoUpload
                // a) Replace ES3.Load("Score", 0) with QueueModHelper.PopScore()
                // b) Only hide uploadPannel if !HasMoreScores()
                // c) Fix path separators for Linux: change Replace("/","\\") to
                //    Replace("\\","/") so Wine paths work on Linux
                // ============================================================
                var myTubeUIType = module.Types.FirstOrDefault(t => t.Name == "MyTubeUI");
                if (myTubeUIType != null) {
                    var method = myTubeUIType.Methods.FirstOrDefault(m => m.Name == "VideoUpload");
                    if (method != null && method.HasBody) {
                        var processor = method.Body.GetILProcessor();
                        var insts = method.Body.Instructions.ToList();
                        bool patchedLoad = false;
                        bool patchedPanel = false;
                        int pathFixCount = 0;
                        for (int i = 0; i < insts.Count; i++) {
                            // (a) Replace score load
                            if (!patchedLoad && insts[i].OpCode == OpCodes.Ldstr &&
                                (string)insts[i].Operand == "Score") {
                                var ldstrInst = insts[i];
                                var ldcInst = insts[i+1];
                                var callInst = insts[i+2];
                                if (callInst.OpCode == OpCodes.Call &&
                                    callInst.Operand is MethodReference mr && mr.Name == "Load") {
                                    processor.Remove(ldstrInst);
                                    processor.Remove(ldcInst);
                                    processor.Replace(callInst, processor.Create(OpCodes.Call, popScoreRef));
                                    fixes++;
                                    patchedLoad = true;
                                    Console.WriteLine("  [PATCH 2a] MyTubeUI: Score load -> PopScore");
                                    insts = method.Body.Instructions.ToList();
                                    i = -1;
                                    continue;
                                }
                            }
                            // (b) Condition uploadPannel hide
                            if (!patchedPanel && insts[i].OpCode == OpCodes.Ldfld &&
                                insts[i].Operand is FieldReference fr && fr.Name == "uploadPannel") {
                                if (i > 0) {
                                    var ldargInst = insts[i-1];
                                    var ldcInst = insts[i+1];
                                    var callvirtInst = insts[i+2];
                                    if (ldargInst.OpCode == OpCodes.Ldarg_0 &&
                                        ldcInst.OpCode == OpCodes.Ldc_I4_0 &&
                                        callvirtInst.OpCode == OpCodes.Callvirt) {
                                        var nop = processor.Create(OpCodes.Nop);
                                        processor.InsertBefore(ldargInst,
                                            processor.Create(OpCodes.Call, hasMoreScoresRef));
                                        processor.InsertBefore(ldargInst,
                                            processor.Create(OpCodes.Brtrue, nop));
                                        processor.InsertAfter(callvirtInst, nop);
                                        fixes++;
                                        patchedPanel = true;
                                        Console.WriteLine("  [PATCH 2b] MyTubeUI: Conditioned uploadPannel hide");
                                        insts = method.Body.Instructions.ToList();
                                        i = -1;
                                        continue;
                                    }
                                }
                            }
                            // (c) Fix path separators -- swap the Replace args
                            // Original: Replace("/", "\\") -- forward slash to backslash
                            // We change it to: Replace("\\", "/") -- backslash to forward slash
                            if (insts[i].OpCode == OpCodes.Ldstr && (string)insts[i].Operand == "/" &&
                                i+1 < insts.Count && insts[i+1].OpCode == OpCodes.Ldstr &&
                                (string)insts[i+1].Operand == "\\") {
                                insts[i].Operand = "\\";
                                insts[i+1].Operand = "/";
                                pathFixCount++;
                            }
                        }
                        if (pathFixCount > 0) {
                            fixes++;
                            Console.WriteLine($"  [PATCH 2c] MyTubeUI: Fixed {pathFixCount} path separator(s) for Linux");
                        }
                    }
                }

                // ============================================================
                // PATCH 3: QuestManager.MyTubeUI_OnVideoUploaded
                // Prevent double-completion of quest 7. We add a static bool
                // field directly on QuestManager (avoids cross-assembly
                // references that break under Unity's Mono runtime).
                // ============================================================
                var questMgrType = module.Types.FirstOrDefault(t => t.Name == "QuestManager");
                if (questMgrType != null) {
                    var flagField = new FieldDefinition("_videoQuestDone",
                        Mono.Cecil.FieldAttributes.Private | Mono.Cecil.FieldAttributes.Static,
                        module.TypeSystem.Boolean);
                    questMgrType.Fields.Add(flagField);

                    var method = questMgrType.Methods.FirstOrDefault(m => m.Name == "MyTubeUI_OnVideoUploaded");
                    if (method != null && method.HasBody) {
                        var processor = method.Body.GetILProcessor();
                        var insts = method.Body.Instructions.ToList();

                        var bneInst = insts.FirstOrDefault(x =>
                            x.OpCode == OpCodes.Bne_Un_S || x.OpCode == OpCodes.Bne_Un);
                        var completeCall = insts.FirstOrDefault(x =>
                            x.OpCode == OpCodes.Call &&
                            x.Operand is MethodReference cmr && cmr.Name == "CompleteQuest");

                        if (bneInst != null && completeCall != null) {
                            var retTarget = (Instruction)bneInst.Operand;
                            var afterBne = bneInst.Next;

                            processor.InsertBefore(afterBne,
                                processor.Create(OpCodes.Ldsfld, flagField));
                            processor.InsertBefore(afterBne,
                                processor.Create(OpCodes.Brtrue_S, retTarget));
                            processor.InsertBefore(afterBne,
                                processor.Create(OpCodes.Ldc_I4_1));
                            processor.InsertBefore(afterBne,
                                processor.Create(OpCodes.Stsfld, flagField));

                            fixes++;
                            Console.WriteLine("  [PATCH 3] QuestManager: Added _videoQuestDone guard");
                        }
                    }
                }

                // ============================================================
                // PATCH 4: Fix VidSelected path separators too
                // ============================================================
                if (myTubeUIType != null) {
                    var method = myTubeUIType.Methods.FirstOrDefault(m => m.Name == "VidSelected");
                    if (method != null && method.HasBody) {
                        var insts = method.Body.Instructions.ToList();
                        int pathFixCount = 0;
                        for (int i = 0; i < insts.Count; i++) {
                            if (insts[i].OpCode == OpCodes.Ldstr && (string)insts[i].Operand == "/" &&
                                i+1 < insts.Count && insts[i+1].OpCode == OpCodes.Ldstr &&
                                (string)insts[i+1].Operand == "\\") {
                                insts[i].Operand = "\\";
                                insts[i+1].Operand = "/";
                                pathFixCount++;
                            }
                        }
                        if (pathFixCount > 0) {
                            fixes++;
                            Console.WriteLine($"  [PATCH 4] MyTubeUI.VidSelected: Fixed {pathFixCount} path separator(s)");
                        }
                    }
                }

                if (fixes > 0) {
                    Console.WriteLine($"\nApplied {fixes} patches. Saving...");
                    assembly.Write();
                    Console.WriteLine("Done.");
                } else {
                    Console.WriteLine("No patches applied.");
                }
            }
        }
    }
}
