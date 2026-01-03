using dnlib.DotNet;
using dnlib.DotNet.Emit;
using System.Text.RegularExpressions;

namespace CouriersFix;

internal class Program
{
    private const string inPath = "C:\\Program Files (x86)\\Steam\\steamapps\\content\\app_1030300\\1.0.28891\\Hollow Knight Silksong_Data\\Managed\\Assembly-CSharp.dll";
    private const string outPath = "C:\\Program Files (x86)\\Steam\\steamapps\\content\\app_1030300\\1.0.28891-CouriersFix\\Hollow Knight Silksong_Data\\Managed\\Assembly-CSharp.dll";

    public static void Main()
    {
        using ModuleDefMD module = ModuleDefMD.Load(inPath);

        /*
         * Edits SimpleQuestsShopOwner::GetItems()
         * "Generic" (repeatable) quests that have not been completed
         * will be prioritized over previously completed quests
         *
         * ...
         * this.runningGenericList.Shuffle<SimpleQuestsShopOwner.ShopItemInfo>();
         * <-- BEGIN INJECTED -->
         * for (int i = 0; i < runningGenericList.Count; i++)
         * {
         *     if (runningGenericList[i].Quest.IsCompleted)
         *     {
         *         runningGenericList.Add(runningGenericList[i]);
         *         runningGenericList.RemoveAt(i);
         *     }
         * }
         * <-- END INJECTED -->
         * while (this.runningGenericList.Count > this.genericQuestCap)
         * {
         *     this.runningGenericList.RemoveAt(this.runningGenericList.Count - 1);
         * }
         * ...
         */

        TypeDef simpleQuestsShopOwnerType = module.Types.First(x => x.Name == "SimpleQuestsShopOwner");
        MethodDef getItemsMethod = simpleQuestsShopOwnerType.Methods.First(x => x.Name == "GetItems");
        CilBody body = getItemsMethod.Body;

        int insertIndex = 0;
        while (true)
        {
            Instruction instruction = body.Instructions[insertIndex];
            if (instruction.OpCode == OpCodes.Call && (instruction.Operand as IMethod)?.Name == "Shuffle")
            {
                insertIndex++;
                break;
            }
            insertIndex++;
        }

        FieldDef runningGenericListField = simpleQuestsShopOwnerType
            .Fields.First(x => x.Name == "runningGenericList");
        FieldDef questField = simpleQuestsShopOwnerType
            .NestedTypes.First(x => x.Name.Contains("ShopItemInfo"))
            .Fields.First(x => x.Name == "Quest");
        MethodDef isCompletedGetter = module
            .Types.First(x => x.Name == "FullQuestBase")
            .Properties.First(x => x.Name == "IsCompleted").GetMethod;

        MemberRef countGetter = module
            .GetMemberRefs().First(x => x.Name == "get_Count" && x.DeclaringType.Name == "List`1");
        MemberRef indexer = module
            .GetMemberRefs().First(x => x.Name == "get_Item" && x.DeclaringType.Name == "List`1");
        MemberRef addMethod = module
            .GetMemberRefs().First(x => x.Name == "Add" && x.DeclaringType.Name == "List`1");
        MemberRef removeAtMethod = module
            .GetMemberRefs().First(x => x.Name == "RemoveAt" && x.DeclaringType.Name == "List`1");

        Local iLocal = body.Variables.Add(new Local(module.CorLibTypes.Int32));

        List<Instruction> instructions = [];

        Instruction startWhile = OpCodes.Nop.ToInstruction();
        Instruction endIf = OpCodes.Nop.ToInstruction();
        Instruction endWhile = OpCodes.Nop.ToInstruction();

        // int i = 0;
        instructions.Add(OpCodes.Ldc_I4_0.ToInstruction());
        instructions.Add(OpCodes.Stloc.ToInstruction(iLocal));

        // while (i < runningGenericList.Count) {
        instructions.Add(startWhile);
        instructions.Add(OpCodes.Ldloc.ToInstruction(iLocal));
        instructions.Add(OpCodes.Ldarg_0.ToInstruction());
        instructions.Add(OpCodes.Ldfld.ToInstruction(runningGenericListField));
        instructions.Add(OpCodes.Callvirt.ToInstruction(countGetter));
        instructions.Add(OpCodes.Bge.ToInstruction(endWhile));

        // if (runningGenericList[i].Quest.IsCompleted) {
        instructions.Add(OpCodes.Ldarg_0.ToInstruction());
        instructions.Add(OpCodes.Ldfld.ToInstruction(runningGenericListField));
        instructions.Add(OpCodes.Ldloc.ToInstruction(iLocal));
        instructions.Add(OpCodes.Callvirt.ToInstruction(indexer));
        instructions.Add(OpCodes.Ldfld.ToInstruction(questField));
        instructions.Add(OpCodes.Callvirt.ToInstruction(isCompletedGetter));
        instructions.Add(OpCodes.Brfalse.ToInstruction(endIf));

        // runningGenericList.Add(runningGenericList[i]);
        instructions.Add(OpCodes.Ldarg_0.ToInstruction());
        instructions.Add(OpCodes.Ldfld.ToInstruction(runningGenericListField));
        instructions.Add(OpCodes.Ldarg_0.ToInstruction());
        instructions.Add(OpCodes.Ldfld.ToInstruction(runningGenericListField));
        instructions.Add(OpCodes.Ldloc.ToInstruction(iLocal));
        instructions.Add(OpCodes.Callvirt.ToInstruction(indexer));
        instructions.Add(OpCodes.Call.ToInstruction(addMethod));

        // runningGenericList.RemoveAt(i);
        instructions.Add(OpCodes.Ldarg_0.ToInstruction());
        instructions.Add(OpCodes.Ldfld.ToInstruction(runningGenericListField));
        instructions.Add(OpCodes.Ldloc.ToInstruction(iLocal));
        instructions.Add(OpCodes.Call.ToInstruction(removeAtMethod));

        // }
        instructions.Add(endIf);

        // i++;
        instructions.Add(OpCodes.Ldloc.ToInstruction(iLocal));
        instructions.Add(OpCodes.Ldc_I4_1.ToInstruction());
        instructions.Add(OpCodes.Add.ToInstruction());
        instructions.Add(OpCodes.Stloc.ToInstruction(iLocal));

        // }
        instructions.Add(OpCodes.Br.ToInstruction(startWhile));
        instructions.Add(endWhile);

        foreach (Instruction instruction in instructions)
        {
            body.Instructions.Insert(insertIndex, instruction);
            insertIndex++;
        }

        body.SimplifyBranches();
        body.OptimizeBranches();

        /*
         * Edits SetVersionNumber::Start()
         * Appends "-CouriersFix" to version
         *
         * Before:
         * ...
         * StringBuilder stringBuilder = new StringBuilder("1.X.XXXXX");
         * ...
         *
         * After:
         * ...
         * StringBuilder stringBuilder = new StringBuilder("1.X.XXXXX-CouriersFix");
         * ...
         */

        MethodDef startMethod = module
            .Types.First(x => x.Name == "SetVersionNumber")
            .Methods.First(x => x.Name == "Start");

        foreach (Instruction instruction in startMethod.Body.Instructions)
        {
            if (instruction.OpCode == OpCodes.Ldstr)
            {
                string operand = (string)instruction.Operand;
                if (Regex.IsMatch(operand, "[0-9.]+"))
                {
                    instruction.Operand = $"{operand}-CouriersFix";
                    break;
                }
            }
        }

        module.Write(outPath);
    }
}
