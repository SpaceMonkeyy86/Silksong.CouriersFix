using dnlib.DotNet;
using dnlib.DotNet.Emit;
using System.Text.RegularExpressions;

namespace CouriersFix;

internal class Program
{
    public static void Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: CouriersFix.exe <in-path> <out-path>");
        }

        string inPath = args[0];
        string outPath = args[1];

        using ModuleDefMD module = ModuleDefMD.Load(inPath);

        /*
         * Edits SimpleQuestsShopOwner::GetItems()
         * "Generic" (repeatable) quests that have not been completed
         * will be prioritized over previously completed quests
         *
         * ...
         * this.runningGenericList.Shuffle<SimpleQuestsShopOwner.ShopItemInfo>();
         * <-- BEGIN INJECTED -->
         * List<SimpleQuestsShopOwner.ShopItemInfo> completed = [];
         * List<SimpleQuestsShopOwner.ShopItemInfo> notCompleted = [];
         * for (int i = 0; i < runningGenericList.Count; i++)
         * {
         *     if (runningGenericList[i].Quest.IsCompleted)
         *     {
         *         completed.Add(runningGenericList[i]);
         *     }
         *     else
         *     {
         *         notCompleted.Add(runningGenericList[i]);
         *     }
         * }
         * notCompleted.AddRange(completed);
         * runningGenericList = notCompleted;
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

        TypeDef shopItemInfoType = simpleQuestsShopOwnerType
            .NestedTypes.First(x => x.Name == "ShopItemInfo");
        FieldDef runningGenericListField = simpleQuestsShopOwnerType
            .Fields.First(x => x.Name == "runningGenericList");
        FieldDef questField = shopItemInfoType
            .Fields.First(x => x.Name == "Quest");
        MethodDef isCompletedGetter = module
            .Types.First(x => x.Name == "FullQuestBase")
            .Properties.First(x => x.Name == "IsCompleted").GetMethod;

        AssemblyRef netstandardRef = module.GetAssemblyRef("netstandard");
        TypeSpec listType = new TypeSpecUser(new GenericInstSig(new ClassSig(
            new TypeRefUser(module, "System.Collections.Generic", "List`1", netstandardRef)), shopItemInfoType.ToTypeSig()));
        TypeSpec enumerableType = new TypeSpecUser(new GenericInstSig(new ClassSig(
            new TypeRefUser(module, "System.Collections.Generic", "IEnumerable`1", netstandardRef)), new GenericVar(0)));

        MemberRef constructor = new MemberRefUser(module, ".ctor",
            MethodSig.CreateInstance(module.CorLibTypes.Void), listType);
        MemberRef countGetter = new MemberRefUser(module, "get_Count",
            MethodSig.CreateInstance(module.CorLibTypes.Int32), listType);
        MemberRef indexer = new MemberRefUser(module, "get_Item",
            MethodSig.CreateInstance(new GenericVar(0), module.CorLibTypes.Int32), listType);
        MemberRef addMethod = new MemberRefUser(module, "Add",
            MethodSig.CreateInstance(module.CorLibTypes.Void, new GenericVar(0)), listType);
        MemberRef addRangeMethod = new MemberRefUser(module, "AddRange",
            MethodSig.CreateInstance(module.CorLibTypes.Void, enumerableType.TypeSig), listType);

        Local completedLocal = body.Variables.Add(new Local(listType.TypeSig));
        Local notCompletedLocal = body.Variables.Add(new Local(listType.TypeSig));
        Local iLocal = body.Variables.Add(new Local(module.CorLibTypes.Int32));

        List<Instruction> instructions = [];

        Instruction startWhile = OpCodes.Nop.ToInstruction();
        Instruction startElse = OpCodes.Nop.ToInstruction();
        Instruction endIf = OpCodes.Nop.ToInstruction();
        Instruction endWhile = OpCodes.Nop.ToInstruction();

        // List<SimpleQuestsShopOwner.ShopItemInfo> completed = [];
        instructions.Add(OpCodes.Newobj.ToInstruction(constructor));
        instructions.Add(OpCodes.Stloc.ToInstruction(completedLocal));

        // List<SimpleQuestsShopOwner.ShopItemInfo> notCompleted = [];
        instructions.Add(OpCodes.Newobj.ToInstruction(constructor));
        instructions.Add(OpCodes.Stloc.ToInstruction(notCompletedLocal));

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
        instructions.Add(OpCodes.Brfalse.ToInstruction(startElse));

        // completed.Add(runningGenericList[i]);
        instructions.Add(OpCodes.Ldloc.ToInstruction(completedLocal));
        instructions.Add(OpCodes.Ldarg_0.ToInstruction());
        instructions.Add(OpCodes.Ldfld.ToInstruction(runningGenericListField));
        instructions.Add(OpCodes.Ldloc.ToInstruction(iLocal));
        instructions.Add(OpCodes.Callvirt.ToInstruction(indexer));
        instructions.Add(OpCodes.Callvirt.ToInstruction(addMethod));

        // } else {
        instructions.Add(OpCodes.Br.ToInstruction(endIf));
        instructions.Add(startElse);

        // notCompleted.Add(runningGenericList[i]);
        instructions.Add(OpCodes.Ldloc.ToInstruction(notCompletedLocal));
        instructions.Add(OpCodes.Ldarg_0.ToInstruction());
        instructions.Add(OpCodes.Ldfld.ToInstruction(runningGenericListField));
        instructions.Add(OpCodes.Ldloc.ToInstruction(iLocal));
        instructions.Add(OpCodes.Callvirt.ToInstruction(indexer));
        instructions.Add(OpCodes.Callvirt.ToInstruction(addMethod));

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

        // notCompleted.AddRange(completed);
        instructions.Add(OpCodes.Ldloc.ToInstruction(notCompletedLocal));
        instructions.Add(OpCodes.Ldloc.ToInstruction(completedLocal));
        instructions.Add(OpCodes.Call.ToInstruction(addRangeMethod));

        // runningGenericList = notCompleted;
        instructions.Add(OpCodes.Ldarg_0.ToInstruction());
        instructions.Add(OpCodes.Ldloc.ToInstruction(notCompletedLocal));
        instructions.Add(OpCodes.Stfld.ToInstruction(runningGenericListField));

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
