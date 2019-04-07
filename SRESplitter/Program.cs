using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        private static readonly HashSet<TypeDefinition> typesToInclude = new HashSet<TypeDefinition>();

        private static readonly Dictionary<string, List<TypeDefinition>> nestedTypesToInclude =
            new Dictionary<string, List<TypeDefinition>>();

        private static readonly Dictionary<string, List<FieldDefinition>> fieldsToInclude =
            new Dictionary<string, List<FieldDefinition>>();

        private static readonly Dictionary<string, List<MethodDefinition>> methodsToInclude =
            new Dictionary<string, List<MethodDefinition>>();

        private static void Diff(ModuleDefinition from,
                                 IEnumerable<TypeDefinition> toTypes,
                                 TypeDefinition parent = null)
        {
            foreach (var toType in toTypes)
            {
                // Skip generic parameters for now because of resolving issues
                //if(toType.HasGenericParameters)
                //    continue;

                var fromType = from.GetType(toType.FullName);

                if (fromType == null)
                {
                    Console.WriteLine($"Adding {toType.FullName}");

                    fieldsToInclude[toType.FullName] = toType.Fields.ToList();
                    methodsToInclude[toType.FullName] = toType.Methods.ToList();

                    if (parent == null)
                        typesToInclude.Add(toType);
                    else
                    {
                        if (!nestedTypesToInclude.TryGetValue(parent.FullName, out var list))
                            nestedTypesToInclude[parent.FullName] = list = new List<TypeDefinition>();
                        list.Add(toType);
                    }

                    Diff(from, toType.NestedTypes, toType);
                    continue;
                }

                foreach (var toField in toType.Fields)
                {
                    var fromField = fromType.Fields.FirstOrDefault(f => f.Name == toField.Name);

                    if (fromField == null || fromField.FieldType.FullName != toField.FieldType.FullName ||
                        fromField.Attributes != toField.Attributes)
                    {
                        if (!fieldsToInclude.TryGetValue(toType.FullName, out var list))
                            fieldsToInclude[toType.FullName] = list = new List<FieldDefinition>();
                        list.Add(toField);
                    }
                }

                foreach (var toMethod in toType.Methods)
                {
                    var fromMethod = fromType.Methods.FirstOrDefault(m => m.FullName == toMethod.FullName);

                    if (fromMethod == null || fromMethod.HasBody != toMethod.HasBody ||
                        fromMethod.Body?.Instructions.Count != toMethod.Body?.Instructions.Count)
                    {
                        if (!methodsToInclude.TryGetValue(toType.FullName, out var list))
                            methodsToInclude[toType.FullName] = list = new List<MethodDefinition>();
                        list.Add(toMethod);
                        continue;
                    }

                    if (toMethod.HasBody && !toMethod.Body.Instructions.SequenceEqual(fromMethod.Body.Instructions,
                                                                                     new DelegateComparer<Instruction>(
                                                                                         (i1, i2) =>
                                                                                             i1.OpCode == i2.OpCode)))
                    {
                        if (!methodsToInclude.TryGetValue(toType.FullName, out var list))
                            methodsToInclude[toType.FullName] = list = new List<MethodDefinition>();
                        list.Add(toMethod);
                    }
                }

                if (fieldsToInclude.ContainsKey(toType.FullName) || methodsToInclude.ContainsKey(toType.FullName) || nestedTypesToInclude.ContainsKey(toType.FullName))
                {
                    Console.WriteLine($"Adding {toType.FullName}");

                    if (parent == null)
                        typesToInclude.Add(toType);
                    else
                    {
                        if (!nestedTypesToInclude.TryGetValue(toType.FullName, out var list))
                            nestedTypesToInclude[toType.FullName] = list = new List<TypeDefinition>();
                        list.Add(toType);
                    }
                }

                Diff(from, toType.NestedTypes, toType);
            }
        }

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
            } else if (mr.DeclaringType is GenericInstanceType git)
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
            using (var fromCorlib = AssemblyDefinition.ReadAssembly("from_mscorlib.dll"))
                using (var toCorlib = AssemblyDefinition.ReadAssembly("to_mscorlib.dll"))
                {
                    var diffDll = AssemblyDefinition.CreateAssembly(
                        new AssemblyNameDefinition("mscorlib_diff", new Version(1, 0)), "mscorlib_diff",
                        ModuleKind.Dll);

                    var diff = diffDll.MainModule;

                    // Reference original assembly to resolve stuff we don't import
                    diff.AssemblyReferences.Add(AssemblyNameReference.Parse(toCorlib.FullName));

                    Diff(fromCorlib.MainModule, toCorlib.MainModule.Types);

                    Console.WriteLine("PASS 1: Registering types");

                    RegisterTypes(diff, typesToInclude);

                    Console.WriteLine("PASS 2: Copying class bodies");

                    foreach (var diffTypePair in diffTypes)
                    {
                        var td = diffTypePair.Value;
                        var originalType = toCorlib.MainModule.GetType(td.FullName);

                    Console.WriteLine(td.FullName);

                        if (originalType.BaseType != null)
                            td.BaseType = diff.ResolveType(originalType.BaseType);

                        

                    if (fieldsToInclude.TryGetValue(td.FullName, out var fields))
                        foreach (var field in fields)
                        {
                            var fd = new FieldDefinition(field.Name, field.Attributes,
                                                         diff.ResolveType(field.FieldType));
                            if (field.HasConstant)
                                fd.Constant = field.Constant;

                            td.Fields.Add(fd);
                            diffFields[fd.FullName] = fd;
                        }

                        if(methodsToInclude.TryGetValue(td.FullName, out var methods))
                        foreach (var method in methods)
                        {
                            var md = new MethodDefinition(method.Name, method.Attributes,
                                                          diff.ImportReference(typeof(void)));

                            foreach (var genPara in method.GenericParameters)
                            {
                                var gp = new GenericParameter(genPara.Name, md) {Attributes = genPara.Attributes};
                                md.GenericParameters.Add(gp);
                            }

                            md.ReturnType = diff.ResolveType(method.ReturnType, md);
                            md.IsInternalCall = method.IsInternalCall;
                            md.IsRuntime = method.IsRuntime;
                            md.IsManaged = method.IsManaged;

                            foreach (var param in method.Parameters)
                            {
                                var pd = new ParameterDefinition(param.Name, param.Attributes,
                                                                 diff.ResolveType(param.ParameterType, md))
                                {
                                    Constant = param.Constant
                                };

                                md.Parameters.Add(pd);
                            }


                            td.Methods.Add(md);

                            diffMethods[md.FullName] = md;
                        }
                }

                    Console.WriteLine("PASS 3: Copying over IL");

                    foreach (var diffTypePair in diffTypes)
                    {
                        var td = diffTypePair.Value;
                        var originalType = toCorlib.MainModule.GetType(td.FullName);

                        Console.WriteLine(td.FullName);
                        
                        foreach (var md in td.Methods)
                        {
                            if(md.IsRuntime)
                                continue;

                            var originalMethod = originalType.Methods.First(m => m.FullName == md.FullName);

                            Console.WriteLine(md.FullName);

                            if (!originalMethod.HasBody)
                                continue;

                            var il = md.Body.GetILProcessor();

                            var varTable = new Dictionary<int, VariableDefinition>();
                            var fixupTable = new Dictionary<int, int>();
                            var fixupArrayTable = new Dictionary<int, int[]>();

                            md.Body.InitLocals = originalMethod.Body.InitLocals;

                            foreach (var variableDefinition in originalMethod.Body.Variables)
                            {
                                var vd = new VariableDefinition(diff.ResolveType(variableDefinition.VariableType));
                                md.Body.Variables.Add(vd);
                                varTable[vd.Index] = vd;
                            }

                            for (var index = 0; index < originalMethod.Body.Instructions.Count; index++)
                            {
                                var ins = originalMethod.Body.Instructions[index];

                                switch (ins.Operand)
                                {
                                    case CallSite cs:
                                        throw new Exception($"Got call site: {cs}. Dunno how to handle that.");
                                    case Instruction label:
                                        fixupTable[index] = originalMethod.Body.Instructions.IndexOf(label);
                                        il.Emit(ins.OpCode, Instruction.Create(OpCodes.Nop));
                                        break;
                                    case Instruction[] labels:
                                        fixupArrayTable[index] =
                                            labels.Select(l => originalMethod.Body.Instructions.IndexOf(l)).ToArray();
                                        il.Emit(ins.OpCode, new Instruction[0]);
                                        break;
                                    case VariableDefinition vd:
                                        il.Emit(ins.OpCode, varTable[vd.Index]);
                                        break;
                                    case FieldReference fr:
                                        il.Emit(ins.OpCode, diff.GetFieldRef(fr));
                                        break;
                                    case MethodReference mr:
                                        il.Emit(ins.OpCode, diff.GetMethodRef(mr));
                                        break;
                                    case TypeReference tr:
                                        il.Emit(ins.OpCode, diff.ResolveType(tr));
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
                                    case double d:
                                        il.Emit(ins.OpCode, d);
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

                    foreach (var diffTypePair in diffTypes)
                    {
                        var td = diffTypePair.Value;
                        var originalType = toCorlib.MainModule.GetType(td.FullName);

                        Console.WriteLine(td.FullName);

                        //foreach (var originalProperty in originalType.Properties)
                        //{
                        //    var pd = new PropertyDefinition(originalProperty.Name, originalProperty.Attributes,
                        //                                    diff.ResolveType(td, originalProperty.PropertyType));

                        //    if (originalProperty.GetMethod != null)
                        //        pd.GetMethod = diffMethods[originalProperty.GetMethod.FullName];
                        //    if (originalProperty.SetMethod != null)
                        //        pd.GetMethod = diffMethods[originalProperty.SetMethod.FullName];

                        //    td.Properties.Add(pd);
                        //}

                        foreach (var originalAttr in originalType.CustomAttributes)
                        {
                            var ca = new CustomAttribute(diff.GetMethodRef(originalAttr.Constructor),
                                                         originalAttr.GetBlob());
                            td.CustomAttributes.Add(ca);
                        }

                        foreach (var md in td.Methods)
                        {
                            if(md.IsRuntime)
                                continue;

                            var originalMethod = originalType.Methods.First(m => m.FullName == md.FullName);

                            Console.WriteLine(md.FullName);

                            foreach (var originalAttr in originalMethod.CustomAttributes)
                            {
                                var ca = new CustomAttribute(diff.GetMethodRef(originalAttr.Constructor),
                                                             originalAttr.GetBlob());
                                md.CustomAttributes.Add(ca);
                            }
                        }
                    }

                    Console.WriteLine("Writing generated DLL");
                    diffDll.Write("mscorlib_diff.dll");
                }

            Console.WriteLine("Done");
            Console.ReadKey();
        }

        private static void RegisterTypes(ModuleDefinition diff,
                                          IEnumerable<TypeDefinition> types,
                                          TypeDefinition parent = null)
        {
            foreach (var originalType in types)
            {
                var td = new TypeDefinition(originalType.Namespace, originalType.Name, originalType.Attributes);

                foreach (var genPara in originalType.GenericParameters)
                {
                    var gp = new GenericParameter(genPara.Name, td) { Attributes = genPara.Attributes };
                    td.GenericParameters.Add(gp);
                }

                if (parent == null)
                    diff.Types.Add(td);
                else
                    parent.NestedTypes.Add(td);

                Console.WriteLine(originalType.FullName);
                diffTypes[td.FullName] = td;

                if (nestedTypesToInclude.TryGetValue(originalType.FullName, out var nestedTypes))
                    RegisterTypes(diff, nestedTypes, td);
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

        private class DelegateComparer<T> : IEqualityComparer<T>
        {
            private readonly Func<T, T, bool> comparer;

            public DelegateComparer(Func<T, T, bool> comparer) { this.comparer = comparer; }

            public bool Equals(T x, T y) { return comparer(x, y); }

            public int GetHashCode(T obj) { return obj.GetHashCode(); }
        }
    }
}