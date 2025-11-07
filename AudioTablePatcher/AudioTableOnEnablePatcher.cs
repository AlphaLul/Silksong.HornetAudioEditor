using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace HornetAudioEditor.Patcher
{
    public class AudioTableOnEnablePatcher
    {
        public static IEnumerable<string> TargetDLLs => new[] { "Assembly-CSharp.dll" };

        public static void Patch(AssemblyDefinition assembly)
        {
            ModuleDefinition module = assembly.MainModule;
            TypeDefinition type = module.GetType("RandomAudioClipTable");
            if (type == null) return;
            if (type.Methods.Any(m => m.Name == "OnEnable")) return;

            MethodDefinition onEnable = new MethodDefinition(
                "OnEnable",
                MethodAttributes.Private | MethodAttributes.HideBySig,
                module.ImportReference(typeof(void))
            );

            onEnable.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
            type.Methods.Add(onEnable);
            
            if (type.Methods.Any(m => m.Name == "OnEnable"))
                Console.WriteLine("AudioTablePatcher: Injected OnEnable into RandomAudioClipTable.");
        }
    }
}