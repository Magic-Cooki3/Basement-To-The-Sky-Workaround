using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Patcher {
    class Program {
        static void Main(string[] args) {
            string targetFile = "/home/magiccookie/.local/share/Steam/steamapps/common/Basement to the Sky Demo/Basement to the Sky Demo_Data/Managed/Assembly-CSharp.dll";
            string modHelperFile = "/home/magiccookie/.local/share/Steam/steamapps/common/Basement to the Sky Demo/Basement to the Sky Demo_Data/Managed/ModHelper.dll";
            
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(System.IO.Path.GetDirectoryName(targetFile));

            using (var assembly = AssemblyDefinition.ReadAssembly(targetFile, new ReaderParameters { ReadWrite = true, AssemblyResolver = resolver }))
            using (var modAssembly = AssemblyDefinition.ReadAssembly(modHelperFile)) {
                var module = assembly.MainModule;
                int fixes = 0;

                var modType = modAssembly.MainModule.Types.First(t => t.Name == "QueueModHelper");
                var addScoreRef = module.ImportReference(modType.Methods.First(m => m.Name == "AddScore"));
                var popScoreRef = module.ImportReference(modType.Methods.First(m => m.Name == "PopScore"));
                var hasMoreScoresRef = module.ImportReference(modType.Methods.First(m => m.Name == "HasMoreScores"));

                // RecordUI.S_OnRocketLanded
                var recordUIType = module.Types.FirstOrDefault(t => t.Name == "RecordUI");
                if (recordUIType != null) {
                    var method = recordUIType.Methods.FirstOrDefault(m => m.Name == "S_OnRocketLanded");
                    if (method != null && method.HasBody) {
                        var processor = method.Body.GetILProcessor();
                        var insts = method.Body.Instructions.ToList();
                        for (int i = 0; i < insts.Count; i++) {
                            if (insts[i].OpCode == OpCodes.Ldstr && (string)insts[i].Operand == "Score") {
                                var ldstrInst = insts[i];
                                var callInst = insts[i+3];
                                if (callInst.OpCode == OpCodes.Call && callInst.Operand is MethodReference mr && mr.Name == "Save") {
                                    processor.Remove(ldstrInst); 
                                    processor.Replace(callInst, processor.Create(OpCodes.Call, addScoreRef)); 
                                    fixes++;
                                    Console.WriteLine("  [PATCH] Redirected RecordUI Score Save");
                                    break;
                                }
                            }
                        }
                    }
                }

                // MyTubeUI.VideoUpload
                var myTubeUIType = module.Types.FirstOrDefault(t => t.Name == "MyTubeUI");
                if (myTubeUIType != null) {
                    var method = myTubeUIType.Methods.FirstOrDefault(m => m.Name == "VideoUpload");
                    if (method != null && method.HasBody) {
                        var processor = method.Body.GetILProcessor();
                        var insts = method.Body.Instructions.ToList();
                        bool patchedLoad = false;
                        bool patchedPanel = false;
                        for (int i = 0; i < insts.Count; i++) {
                            if (!patchedLoad && insts[i].OpCode == OpCodes.Ldstr && (string)insts[i].Operand == "Score") {
                                var ldstrInst = insts[i];
                                var ldcInst = insts[i+1];
                                var callInst = insts[i+2];
                                if (callInst.OpCode == OpCodes.Call && callInst.Operand is MethodReference mr && mr.Name == "Load") {
                                    processor.Remove(ldstrInst);
                                    processor.Remove(ldcInst);
                                    processor.Replace(callInst, processor.Create(OpCodes.Call, popScoreRef));
                                    fixes++;
                                    patchedLoad = true;
                                    Console.WriteLine("  [PATCH] Redirected MyTubeUI Score Load");
                                }
                            }
                            if (!patchedPanel && insts[i].OpCode == OpCodes.Ldfld && insts[i].Operand is FieldReference fr && fr.Name == "uploadPannel") {
                                var ldargInst = insts[i-1];
                                var ldcInst = insts[i+1];
                                var callvirtInst = insts[i+2];
                                if (ldargInst.OpCode == OpCodes.Ldarg_0 && ldcInst.OpCode == OpCodes.Ldc_I4_0 && callvirtInst.OpCode == OpCodes.Callvirt) {
                                    var nop = processor.Create(OpCodes.Nop);
                                    processor.InsertBefore(ldargInst, processor.Create(OpCodes.Call, hasMoreScoresRef));
                                    processor.InsertBefore(ldargInst, processor.Create(OpCodes.Brtrue, nop));
                                    processor.InsertAfter(callvirtInst, nop);
                                    fixes++;
                                    patchedPanel = true;
                                    Console.WriteLine("  [PATCH] Conditioned uploadPannel.SetActive(false)");
                                }
                            }
                        }
                    }
                }

                // Fix rapid-fire bug by unsubscribing MyTubeUI_OnVideoUploaded instead of modifying CompleteQuest
                var questMgrType = module.Types.FirstOrDefault(t => t.Name == "QuestManager");
                if (questMgrType != null && myTubeUIType != null) {
                    var method = questMgrType.Methods.FirstOrDefault(m => m.Name == "MyTubeUI_OnVideoUploaded");
                    var removeEventMethod = myTubeUIType.Methods.FirstOrDefault(m => m.Name == "remove_OnVideoUploaded");
                    var actionCtor = module.ImportReference(typeof(System.Action).GetConstructors()[0]);
                    
                    if (method != null && method.HasBody && removeEventMethod != null) {
                        var processor = method.Body.GetILProcessor();
                        var insts = method.Body.Instructions.ToList();
                        
                        var callCompleteQuest = insts.FirstOrDefault(i => i.OpCode == OpCodes.Call && ((MethodReference)i.Operand).Name == "CompleteQuest");
                        if (callCompleteQuest != null) {
                            var ldarg0 = callCompleteQuest.Previous.Previous; // Should be ldarg.0 before CompleteQuest args
                            
                            if (ldarg0 != null && ldarg0.OpCode == OpCodes.Ldarg_0) {
                                var newLdarg0 = processor.Create(OpCodes.Ldarg_0);
                                var ldftn = processor.Create(OpCodes.Ldftn, method);
                                var newobj = processor.Create(OpCodes.Newobj, actionCtor);
                                var callRemove = processor.Create(OpCodes.Call, module.ImportReference(removeEventMethod));

                                processor.InsertBefore(ldarg0, newLdarg0);
                                processor.InsertBefore(ldarg0, ldftn);
                                processor.InsertBefore(ldarg0, newobj);
                                processor.InsertBefore(ldarg0, callRemove);

                                fixes++;
                                Console.WriteLine("  [PATCH] Added unsubscribe safety to MyTubeUI_OnVideoUploaded");
                            }
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
