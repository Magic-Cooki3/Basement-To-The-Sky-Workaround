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

            using (var assembly = AssemblyDefinition.ReadAssembly(targetFile, new ReaderParameters { ReadWrite = true, AssemblyResolver = resolver }))
            {
                var module = assembly.MainModule;
                int fixes = 0;

                // ============================================================
                // PATCH 1: Fix path separators in MyTubeUI
                // Replace("/", "\\") -> Replace("/", "/") which is a no-op
                // ============================================================
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
                                if (instructions[i - 1].OpCode == OpCodes.Ldstr && (string)instructions[i - 1].Operand == "/")
                                {
                                    Console.WriteLine($"  [PATH] Fixed separator in {type.Name}::{method.Name}");
                                    inst.Operand = "/";
                                    fixes++;
                                }
                            }
                        }
                    }
                }

                // ============================================================
                // PATCH 2: In VideoKitTest.Rocket_OnRetriveRocketActive,
                // after the existing code, inject:
                //   GameManager.S.NewVidRercorded("RocketLaunch");
                // This ensures the MyTubeUI gets notified even if the 
                // VideoKit recorder fails silently.
                // ============================================================
                var videoKitTestType = module.Types.FirstOrDefault(t => t.Name == "VideoKitTest");
                var gameManagerType = module.Types.FirstOrDefault(t => t.Name == "GameManager");

                if (videoKitTestType != null && gameManagerType != null)
                {
                    var rocketMethod = videoKitTestType.Methods.FirstOrDefault(m => m.Name == "Rocket_OnRetriveRocketActive");
                    
                    // Find GameManager.get_S (the static singleton accessor)
                    var getSMethod = gameManagerType.Methods.FirstOrDefault(m => m.Name == "get_S");
                    // Find GameManager.NewVidRercorded(string)
                    var newVidMethod = gameManagerType.Methods.FirstOrDefault(m => m.Name == "NewVidRercorded");

                    if (rocketMethod != null && getSMethod != null && newVidMethod != null && rocketMethod.HasBody)
                    {
                        var processor = rocketMethod.Body.GetILProcessor();
                        // Find the final ret instruction
                        var retInst = rocketMethod.Body.Instructions.Last(i => i.OpCode == OpCodes.Ret);
                        
                        // Inject: GameManager.S.NewVidRercorded("RocketLaunch");
                        // IL: call GameManager::get_S()
                        //     ldstr "RocketLaunch"
                        //     callvirt GameManager::NewVidRercorded(string)
                        var callGetS = processor.Create(OpCodes.Call, module.ImportReference(getSMethod));
                        var ldstr = processor.Create(OpCodes.Ldstr, "RocketLaunch");
                        var callNewVid = processor.Create(OpCodes.Callvirt, module.ImportReference(newVidMethod));

                        processor.InsertBefore(retInst, callGetS);
                        processor.InsertBefore(retInst, ldstr);
                        processor.InsertBefore(retInst, callNewVid);

                        Console.WriteLine("  [INJECT] Added NewVidRercorded('RocketLaunch') call to Rocket_OnRetriveRocketActive");
                        fixes++;
                    }
                    else
                    {
                        Console.WriteLine("  [WARN] Could not find one of the required methods:");
                        Console.WriteLine($"    rocketMethod: {rocketMethod != null}");
                        Console.WriteLine($"    getSMethod: {getSMethod != null}");
                        Console.WriteLine($"    newVidMethod: {newVidMethod != null}");
                    }
                }

                // ============================================================
                // PATCH 3: Prevent ResetAllDataExceptEssential from deleting
                // files and directories (the aggressive save-wiper)
                // ============================================================
                var mainMenuType = module.Types.FirstOrDefault(t => t.Name == "MainMenuUI");
                if (mainMenuType != null)
                {
                    var resetMethod = mainMenuType.Methods.FirstOrDefault(m => m.Name == "ResetAllDataExceptEssential");
                    if (resetMethod != null && resetMethod.HasBody)
                    {
                        var processor = resetMethod.Body.GetILProcessor();
                        var instructions = resetMethod.Body.Instructions.ToList();
                        foreach (var inst in instructions)
                        {
                            if (inst.OpCode == OpCodes.Callvirt && inst.Operand is MethodReference methodRef)
                            {
                                // FileSystemInfo.Delete() - used by FileInfo (inherits)
                                if (methodRef.Name == "Delete" && 
                                    (methodRef.DeclaringType.FullName == "System.IO.FileSystemInfo" ||
                                     methodRef.DeclaringType.FullName == "System.IO.FileInfo"))
                                {
                                    // FileInfo.Delete() takes no args, pop 'this'
                                    processor.Replace(inst, processor.Create(OpCodes.Pop));
                                    Console.WriteLine("  [DELETE] Neutralized FileInfo.Delete in ResetAllDataExceptEssential");
                                    fixes++;
                                }
                                else if (methodRef.Name == "Delete" &&
                                         methodRef.DeclaringType.FullName == "System.IO.DirectoryInfo")
                                {
                                    // DirectoryInfo.Delete(bool recursive) takes 1 arg + this
                                    var pop1 = processor.Create(OpCodes.Pop);
                                    var pop2 = processor.Create(OpCodes.Pop);
                                    processor.InsertBefore(inst, pop1);
                                    processor.Replace(inst, pop2);
                                    Console.WriteLine("  [DELETE] Neutralized DirectoryInfo.Delete in ResetAllDataExceptEssential");
                                    fixes++;
                                }
                            }
                        }
                    }
                }

                if (fixes > 0)
                {
                    Console.WriteLine($"\nTotal: {fixes} patches applied. Saving assembly...");
                    assembly.Write();
                    Console.WriteLine("Patching complete!");
                }
                else
                {
                    Console.WriteLine("No patches applied.");
                }
            }
        }
    }
}
