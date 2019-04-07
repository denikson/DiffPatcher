using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace SRESplitter
{
    internal static class Program
    {
        private static readonly Dictionary<string, TypeDefinition> corTypes = new Dictionary<string, TypeDefinition>();

        private static readonly Dictionary<string, MethodDefinition> corMethods =
            new Dictionary<string, MethodDefinition>();

        private static readonly Dictionary<string, FieldDefinition> corFields =
            new Dictionary<string, FieldDefinition>();

        private static FieldReference GetFieldRef(this ModuleDefinition md, FieldReference fr)
        {
            return corFields.TryGetValue(fr.FullName, out var result) ? result : md.ImportReference(fr);
        }

        private static MethodReference GetMethodRef(this ModuleDefinition md, MethodReference mr)
        {
            return corMethods.TryGetValue(mr.FullName, out var result) ? result : md.ImportReference(mr);
        }

        private static void Main(string[] args)
        {
            using (var mre = AssemblyDefinition.ReadAssembly("Mono.Reflection.Emit.dll"))
                using (var corlib = AssemblyDefinition.ReadAssembly("mscorlib.dll"))
                {
                    var cor = corlib.MainModule;
                    Console.WriteLine("PASS 1: Registering types");
                    RegisterTypes(cor, null, mre.MainModule.Types);

                    Console.WriteLine("PASS 2: Copying class bodies");
                    foreach (var corTypePair in corTypes)
                    {
                        var td = corTypePair.Value;
                        var mreType = mre.MainModule.GetType(td.FullName);

                        Console.WriteLine(td.FullName);

                        if (mreType.BaseType != null)
                            td.BaseType = cor.ResolveType(td, mreType.BaseType);

                        foreach (var field in mreType.Fields)
                        {
                            var fd = td.Fields.FirstOrDefault(f => f.Name == field.Name);

                            if (fd == null)
                            {
                                fd = new FieldDefinition(field.Name, field.Attributes,
                                                         cor.ResolveType(td, field.FieldType));

                                if (field.HasConstant)
                                    fd.Constant = field.Constant;
                                td.Fields.Add(fd);
                            }
                            else
                                fd.FieldType = cor.ResolveType(td, field.FieldType);

                            if (td.Name == "Assembly")
                            {
                                fd.IsPrivate = false;
                                fd.IsAssembly = true;
                            }

                            corFields[fd.FullName] = fd;
                        }

                        foreach (var method in mreType.Methods)
                        {
                            var md = td.Methods.FirstOrDefault(m => m.FullName == method.FullName);

                            if (md == null)
                            {
                                md = new MethodDefinition(method.Name, method.Attributes,
                                                          cor.ImportReference(typeof(void)));

                                md.ReturnType = cor.ResolveType(md, method.ReturnType);
                                md.IsInternalCall = method.IsInternalCall;

                                foreach (var genPara in method.GenericParameters)
                                {
                                    var gp = new GenericParameter(genPara.Name, md) {Attributes = genPara.Attributes};
                                    md.GenericParameters.Add(gp);
                                }

                                foreach (var param in method.Parameters)
                                {
                                    var pd = new ParameterDefinition(param.Name, param.Attributes,
                                                                     cor.ResolveType(md, param.ParameterType))
                                    {
                                        Constant = param.Constant
                                    };

                                    md.Parameters.Add(pd);
                                }

                                td.Methods.Add(md);
                            }
                            else
                            {
                                md.ReturnType = cor.ResolveType(md, method.ReturnType);

                                foreach (var param in md.Parameters)
                                    param.ParameterType = cor.ResolveType(md, param.ParameterType);
                            }

                            corMethods[md.FullName] = md;
                        }
                    }

                    Console.WriteLine("PASS 3: Copying over IL");

                    foreach (var corTypePair in corTypes)
                    {
                        var td = corTypePair.Value;
                        var mreType = mre.MainModule.GetType(td.FullName);

                        Console.WriteLine(td.FullName);

                        foreach (var md in td.Methods)
                        {
                            var mreMethod = mreType.Methods.FirstOrDefault(m => m.FullName == md.FullName);

                            if (mreMethod == null)
                                continue;

                            Console.WriteLine(md.FullName);

                            if (!mreMethod.HasBody)
                                continue;

                            // Remove old instructions cuz yolo
                            md.Body.Instructions.Clear();
                            md.Body.Variables.Clear();

                            var il = md.Body.GetILProcessor();

                            var varTable = new Dictionary<int, VariableDefinition>();
                            var fixupTable = new Dictionary<int, int>();
                            var fixupArrayTable = new Dictionary<int, int[]>();

                            md.Body.InitLocals = mreMethod.Body.InitLocals;

                            foreach (var variableDefinition in mreMethod.Body.Variables)
                            {
                                var vd = new VariableDefinition(cor.ResolveType(md, variableDefinition.VariableType));
                                md.Body.Variables.Add(vd);
                                varTable[vd.Index] = vd;
                            }

                            for (var index = 0; index < mreMethod.Body.Instructions.Count; index++)
                            {
                                var ins = mreMethod.Body.Instructions[index];

                                switch (ins.Operand)
                                {
                                    case CallSite cs:
                                        throw new Exception($"Got call site: {cs}. Dunno how to handle that.");
                                    case Instruction label:
                                        fixupTable[index] = mreMethod.Body.Instructions.IndexOf(label);
                                        il.Emit(ins.OpCode, Instruction.Create(OpCodes.Nop));
                                        break;
                                    case Instruction[] labels:
                                        fixupArrayTable[index] =
                                            labels.Select(l => mreMethod.Body.Instructions.IndexOf(l)).ToArray();
                                        il.Emit(ins.OpCode, new Instruction[0]);
                                        break;
                                    case VariableDefinition vd:
                                        il.Emit(ins.OpCode, varTable[vd.Index]);
                                        break;
                                    case FieldReference fr:
                                        il.Emit(ins.OpCode, cor.GetFieldRef(fr));
                                        break;
                                    case MethodReference mr:
                                        il.Emit(ins.OpCode, cor.GetMethodRef(mr));
                                        break;
                                    case TypeReference tr:
                                        il.Emit(ins.OpCode, cor.ResolveType(md, tr));
                                        break;
                                    case ParameterDefinition pd:
                                        il.Emit(ins.OpCode, md.Parameters[pd.Index]);
                                        break;
                                    case byte b:
                                        il.Emit(ins.OpCode, b);
                                        break;
                                    case sbyte sb:
                                        il.Emit(ins.OpCode, sb);
                                        break;
                                    case float f:
                                        il.Emit(ins.OpCode, f);
                                        break;
                                    case int i:
                                        il.Emit(ins.OpCode, i);
                                        break;
                                    case long l:
                                        il.Emit(ins.OpCode, l);
                                        break;
                                    case string s:
                                        il.Emit(ins.OpCode, s);
                                        break;
                                    default:
                                        il.Emit(ins.OpCode);
                                        break;
                                }
                            }

                            foreach (var entry in fixupTable)
                                md.Body.Instructions[entry.Key].Operand = md.Body.Instructions[entry.Value];

                            foreach (var entry in fixupArrayTable)
                                md.Body.Instructions[entry.Key].Operand =
                                    md.Body.Instructions.Where((ins, i) => entry.Value.Contains(i)).ToArray();
                        }
                    }

                    Console.WriteLine("PASS 4: Gluing properties, adding attributes");

                    foreach (var corTypePair in corTypes)
                    {
                        var td = corTypePair.Value;
                        var mreType = mre.MainModule.GetType(td.FullName);

                        Console.WriteLine(td.FullName);

                        foreach (var mreProperty in mreType.Properties)
                        {
                            var pd = td.Properties.FirstOrDefault(p => p.FullName == mreProperty.FullName);

                            if (pd == null)
                            {
                                pd = new PropertyDefinition(mreProperty.Name, mreProperty.Attributes,
                                                            cor.ResolveType(td, mreProperty.PropertyType));
                                td.Properties.Add(pd);
                            }

                            if (mreProperty.GetMethod != null)
                                pd.GetMethod = corMethods[mreProperty.GetMethod.FullName];
                            if (mreProperty.SetMethod != null)
                                pd.GetMethod = corMethods[mreProperty.SetMethod.FullName];
                        }

                        // Remove old attributes cuz yolo
                        td.CustomAttributes.Clear();

                        foreach (var sreAttr in mreType.CustomAttributes)
                        {
                            var ca = new CustomAttribute(cor.GetMethodRef(sreAttr.Constructor), sreAttr.GetBlob());
                            td.CustomAttributes.Add(ca);
                        }

                        foreach (var md in td.Methods)
                        {
                            var mreMethod = mreType.Methods.FirstOrDefault(m => m.FullName == md.FullName);

                            if (mreMethod == null)
                                continue;

                            Console.WriteLine(md.FullName);

                            md.CustomAttributes.Clear();
                            foreach (var sreAttr in mreMethod.CustomAttributes)
                            {
                                var ca = new CustomAttribute(cor.GetMethodRef(sreAttr.Constructor), sreAttr.GetBlob());
                                md.CustomAttributes.Add(ca);
                            }
                        }
                    }

                    Console.WriteLine("Writing generated DLL");
                    corlib.Write("mscorlib_fixed.dll");
                }

            Console.WriteLine("Done");
            Console.ReadKey();
        }

        private static void RegisterTypes(ModuleDefinition cor,
                                          TypeDefinition parent,
                                          IEnumerable<TypeDefinition> types)
        {
            foreach (var mreType in types)
            {
                var td = cor.GetType(mreType.FullName);

                if (td == null)
                {
                    td = new TypeDefinition(mreType.Namespace, mreType.Name, mreType.Attributes);

                    if (parent == null)
                        cor.Types.Add(td);
                    else
                        parent.NestedTypes.Add(td);
                }

                Console.WriteLine(mreType.FullName);
                corTypes[td.FullName] = td;

                RegisterTypes(cor, td, mreType.NestedTypes);
            }
        }

        private static TypeReference ResolveType(this ModuleDefinition mre,
                                                 IGenericParameterProvider md,
                                                 TypeReference tr)
        {
            if (corTypes.TryGetValue(tr.FullName, out var result))
                return result;

            switch (tr)
            {
                case ArrayType at: return ResolveType(mre, md, at.ElementType).MakeArrayType();
                case ByReferenceType brt: return ResolveType(mre, md, brt.ElementType).MakeByReferenceType();
                case PointerType pt: return ResolveType(mre, md, pt.ElementType).MakePointerType();
                case PinnedType pt: return ResolveType(mre, md, pt.ElementType).MakePinnedType();
                case GenericInstanceType git:
                    return ResolveType(mre, md, git.ElementType)
                       .MakeGenericInstanceType(git.GenericArguments.Select(t => ResolveType(mre, md, t)).ToArray());
                case GenericParameter gp:
                {
                    if (gp.DeclaringMethod == null)
                        throw new Exception("Type generics not supported because I'm lazy");

                    var res = md.GenericParameters.FirstOrDefault(g => g.Name == gp.Name);
                    if (res != null)
                        return res;

                    res = new GenericParameter(gp.Name, md) {Attributes = gp.Attributes};

                    md.GenericParameters.Add(res);
                    return res;
                }
            }

            return mre.ImportReference(tr);
        }
    }
}