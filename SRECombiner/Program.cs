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
        private static readonly Dictionary<string, TypeDefinition> diffTypes = new Dictionary<string, TypeDefinition>();

        private static readonly Dictionary<string, MethodDefinition> diffMethods =
            new Dictionary<string, MethodDefinition>();

        private static readonly Dictionary<string, FieldDefinition> diffFields =
            new Dictionary<string, FieldDefinition>();

        private static FieldReference GetFieldRef(this ModuleDefinition md, FieldReference fr)
        {
            if (fr.DeclaringType is GenericInstanceType git)
            {
                var res = GetFieldRef(md, fr.Resolve());
                var newGit = new GenericInstanceType(res.DeclaringType);

                foreach (var gitGenericArgument in git.GenericArguments)
                {
                    newGit.GenericArguments.Add(md.ResolveType(gitGenericArgument));
                }

                res.DeclaringType = newGit;
                return res;
            }

            return diffFields.TryGetValue(fr.FullName, out var result) ? result : md.ImportReference(fr);
        }

        private static MethodReference GetMethodRef(this ModuleDefinition md, MethodReference mr)
        {
            if (mr is GenericInstanceMethod gim)
            {
                var newGim = new GenericInstanceMethod(md.GetMethodRef(gim.ElementMethod));

                foreach (var gimGenericArgument in gim.GenericArguments)
                    newGim.GenericArguments.Add(md.ResolveType(gimGenericArgument));

                return newGim;
            }
            else if (mr.DeclaringType is GenericInstanceType git)
            {
                var res = GetMethodRef(md, mr.Resolve());
                var newGit = new GenericInstanceType(res.DeclaringType);

                foreach (var gitGenericArgument in git.GenericArguments)
                {
                    newGit.GenericArguments.Add(md.ResolveType(gitGenericArgument));
                }

                res.DeclaringType = newGit;
                return res;
            }

            return diffMethods.TryGetValue(mr.FullName, out var result) ? result : md.ImportReference(mr);
        }

        private static void Main(string[] args)
        {
            using (var diffDll = AssemblyDefinition.ReadAssembly("mscorlib_diff.dll"))
                using (var corlibDll = AssemblyDefinition.ReadAssembly("mscorlib.dll"))
                {
                    var diff = diffDll.MainModule;
                    var corlib = corlibDll.MainModule;
                    Console.WriteLine("PASS 1: Registering types");
                    RegisterTypes(corlib, null, diffDll.MainModule.Types);

                    Console.WriteLine("PASS 2: Copying class bodies");
                    foreach (var corTypePair in diffTypes)
                    {
                        var td = corTypePair.Value;
                        var diffType = diff.GetType(td.FullName);

                        Console.WriteLine(td.FullName);

                        if (diffType.BaseType != null)
                            td.BaseType = corlib.ResolveType(diffType.BaseType);

                        foreach (var field in diffType.Fields)
                        {
                            var fd = td.Fields.FirstOrDefault(f => f.Name == field.Name);

                            if (fd == null)
                            {
                                fd = new FieldDefinition(field.Name, field.Attributes,
                                                         corlib.ResolveType(field.FieldType));

                                if (field.HasConstant)
                                    fd.Constant = field.Constant;
                                td.Fields.Add(fd);
                            }
                            else
                            {
                                fd.FieldType = corlib.ResolveType(field.FieldType);
                                fd.Attributes = field.Attributes;
                            }

                            diffFields[fd.FullName] = fd;
                        }

                        foreach (var method in diffType.Methods)
                        {
                            var md = td.Methods.FirstOrDefault(m => m.FullName == method.FullName);

                            if (md == null)
                            {
                                md = new MethodDefinition(method.Name, method.Attributes,
                                                          corlib.ImportReference(typeof(void)));

                                td.Methods.Add(md);

                                foreach (var genPara in method.GenericParameters)
                                {
                                    var gp = new GenericParameter(genPara.Name, md) {Attributes = genPara.Attributes};
                                    md.GenericParameters.Add(gp);
                                }

                                md.ReturnType = corlib.ResolveType(method.ReturnType);
                                md.IsInternalCall = method.IsInternalCall;

                                foreach (var param in method.Parameters)
                                {
                                    var pd = new ParameterDefinition(param.Name, param.Attributes,
                                                                     corlib.ResolveType(param.ParameterType, md))
                                    {
                                        Constant = param.Constant
                                    };

                                    md.Parameters.Add(pd);
                                }

                            }
                            else
                            {
                                md.ReturnType = corlib.ResolveType(method.ReturnType, md);

                                foreach (var param in md.Parameters)
                                    param.ParameterType = corlib.ResolveType(param.ParameterType, md);
                            }

                            diffMethods[md.FullName] = md;
                        }
                    }

                    Console.WriteLine("PASS 3: Copying over IL");

                    foreach (var corTypePair in diffTypes)
                    {
                        var td = corTypePair.Value;
                        var diffType = diffDll.MainModule.GetType(td.FullName);

                        Console.WriteLine(td.FullName);

                        foreach (var md in td.Methods)
                        {
                            var diffMethod = diffType.Methods.FirstOrDefault(m => m.FullName == md.FullName);

                            if (diffMethod == null)
                                continue;

                            Console.WriteLine(md.FullName);

                            if (!diffMethod.HasBody)
                                continue;

                            // Remove old instructions cuz yolo
                            md.Body.Instructions.Clear();
                            md.Body.Variables.Clear();

                            var il = md.Body.GetILProcessor();

                            var varTable = new Dictionary<int, VariableDefinition>();
                            var fixupTable = new Dictionary<int, int>();
                            var fixupArrayTable = new Dictionary<int, int[]>();

                            md.Body.InitLocals = diffMethod.Body.InitLocals;

                            foreach (var variableDefinition in diffMethod.Body.Variables)
                            {
                                var vd = new VariableDefinition(corlib.ResolveType(variableDefinition.VariableType));
                                md.Body.Variables.Add(vd);
                                varTable[vd.Index] = vd;
                            }

                            for (var index = 0; index < diffMethod.Body.Instructions.Count; index++)
                            {
                                var ins = diffMethod.Body.Instructions[index];

                                switch (ins.Operand)
                                {
                                    case CallSite cs:
                                        throw new Exception($"Got call site: {cs}. Dunno how to handle that.");
                                    case Instruction label:
                                        fixupTable[index] = diffMethod.Body.Instructions.IndexOf(label);
                                        il.Emit(ins.OpCode, Instruction.Create(OpCodes.Nop));
                                        break;
                                    case Instruction[] labels:
                                        fixupArrayTable[index] =
                                            labels.Select(l => diffMethod.Body.Instructions.IndexOf(l)).ToArray();
                                        il.Emit(ins.OpCode, new Instruction[0]);
                                        break;
                                    case VariableDefinition vd:
                                        il.Emit(ins.OpCode, varTable[vd.Index]);
                                        break;
                                    case FieldReference fr:
                                        il.Emit(ins.OpCode, corlib.GetFieldRef(fr));
                                        break;
                                    case MethodReference mr:
                                        il.Emit(ins.OpCode, corlib.GetMethodRef(mr));
                                        break;
                                    case TypeReference tr:
                                        il.Emit(ins.OpCode, corlib.ResolveType(tr));
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
                                    case double d:
                                        il.Emit(ins.OpCode, d);
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

                    foreach (var corTypePair in diffTypes)
                    {
                        var td = corTypePair.Value;
                        var diffType = diffDll.MainModule.GetType(td.FullName);

                        Console.WriteLine(td.FullName);

                        foreach (var diffProperty in diffType.Properties)
                        {
                            var pd = td.Properties.FirstOrDefault(p => p.FullName == diffProperty.FullName);

                            if (pd == null)
                            {
                                pd = new PropertyDefinition(diffProperty.Name, diffProperty.Attributes,
                                                            corlib.ResolveType(diffProperty.PropertyType));
                                td.Properties.Add(pd);
                            }

                            if (diffProperty.GetMethod != null)
                                pd.GetMethod = diffMethods[diffProperty.GetMethod.FullName];
                            if (diffProperty.SetMethod != null)
                                pd.GetMethod = diffMethods[diffProperty.SetMethod.FullName];
                        }

                        // Remove old attributes cuz yolo
                        td.CustomAttributes.Clear();

                        foreach (var sreAttr in diffType.CustomAttributes)
                        {
                            var ca = new CustomAttribute(corlib.GetMethodRef(sreAttr.Constructor), sreAttr.GetBlob());
                            td.CustomAttributes.Add(ca);
                        }

                        foreach (var md in td.Methods)
                        {
                            var diffMethod = diffType.Methods.FirstOrDefault(m => m.FullName == md.FullName);

                            if (diffMethod == null)
                                continue;

                            Console.WriteLine(md.FullName);

                            md.CustomAttributes.Clear();
                            foreach (var diffAttr in diffMethod.CustomAttributes)
                            {
                                var ca = new CustomAttribute(corlib.GetMethodRef(diffAttr.Constructor), diffAttr.GetBlob());
                                md.CustomAttributes.Add(ca);
                            }
                        }
                    }

                    Console.WriteLine("Writing generated DLL");
                    corlibDll.Write("mscorlib_fixed.dll");
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
                diffTypes[td.FullName] = td;

                RegisterTypes(cor, td, mreType.NestedTypes);
            }
        }

        private static TypeReference ResolveType(this ModuleDefinition mre,
                                                 TypeReference tr, IGenericParameterProvider genericParamProvider = null)
        {
            if (diffTypes.TryGetValue(tr.FullName, out var result))
                return result;

            switch (tr)
            {
                case ArrayType at: return ResolveType(mre, at.ElementType, genericParamProvider).MakeArrayType();
                case ByReferenceType brt: return ResolveType(mre, brt.ElementType, genericParamProvider).MakeByReferenceType();
                case PointerType pt: return ResolveType(mre, pt.ElementType, genericParamProvider).MakePointerType();
                case PinnedType pt: return ResolveType(mre, pt.ElementType, genericParamProvider).MakePinnedType();
                case GenericInstanceType git:
                    return ResolveType(mre, git.ElementType, genericParamProvider)
                       .MakeGenericInstanceType(git.GenericArguments.Select(t => ResolveType(mre, t, genericParamProvider)).ToArray());
                case GenericParameter gp:
                    {
                        GenericParameter res;
                        if (gp.DeclaringMethod != null)
                        {
                            var md = genericParamProvider ?? mre.GetType(gp.DeclaringMethod.DeclaringType.FullName).Methods.First(m => m.FullName == gp.DeclaringMethod.FullName);
                            res = md.GenericParameters.FirstOrDefault(g => g.Name == gp.Name);
                            if (res != null)
                                return res;
                        }

                        res = mre.GetType(gp.DeclaringType.FullName).GenericParameters.FirstOrDefault(g => g.Name == gp.Name);
                        if (res != null)
                            return res;

                        throw new Exception($"Tried to resolve generic type {gp} that does not exist!");
                    }
            }

            return mre.ImportReference(tr);
        }
    }
}