using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Utils;

namespace NetDiffPatch
{
    public class NetDiffer : DiffPatchBase
    {
        private static readonly DelegateComparer<Instruction> InstructionComparer =
            new DelegateComparer<Instruction>((i1, i2) => i1.OpCode == i2.OpCode);

        private readonly Dictionary<string, List<FieldDefinition>> fieldsToInclude =
            new Dictionary<string, List<FieldDefinition>>();

        private readonly ModuleDefinition from;

        private readonly Dictionary<string, List<MethodDefinition>> methodsToInclude =
            new Dictionary<string, List<MethodDefinition>>();

        private readonly Dictionary<string, List<TypeDefinition>> nestedTypesToInclude =
            new Dictionary<string, List<TypeDefinition>>();

        private readonly ModuleDefinition to;

        private readonly HashSet<TypeDefinition> typesToInclude = new HashSet<TypeDefinition>();

        private ModuleDefinition diffMD;

        public NetDiffer(ModuleDefinition from,
                         ModuleDefinition to,
                         HashSet<string> excludeNamespaces = null,
                         HashSet<string> excludeTypes = null)
        {
            this.from = from;
            this.to = to;

            if (excludeNamespaces != null)
                ExcludeNamespaces = excludeNamespaces;
            if (excludeTypes != null)
                ExcludeTypes = excludeTypes;

            InitDiff(to.Types);
        }

        public AssemblyDefinition CreateDiffAssembly()
        {
            var diffDll = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition($"{from.Assembly.Name.Name}_diff", new Version(1, 0)),
                $"{from.Assembly.Name.Name}_diff", ModuleKind.Dll);


            diffMD = diffDll.MainModule;
            
            // Reference original assembly to resolve stuff we don't import
            diffMD.AssemblyReferences.Add(to.Assembly.Name);

            RegisterTypes(diffMD, typesToInclude);
            GenerateTypeDefinitions(to, diffMD);
            CopyInstructions(to, diffMD);
            CopyAttributesAndProperties(to, diffMD);
            //var diff = diffDll.MainModule;

            //// Reference original assembly to resolve stuff we don't import
            //diff.AssemblyReferences.Add(to.Assembly.Name);

            //RegisterTypes(diff, typesToInclude);
            //GenerateTypeDefinitions(to, diff);
            //CopyInstructions(to, diff);
            //CopyAttributesAndProperties(to, diff);

            //RunDiff(diffDll);

            return diffDll;
        }

        private void RunDiff(AssemblyDefinition diffDll)
        {
            var diffMD = diffDll.MainModule;
            RegisterTypes(diffMD, typesToInclude);
            GenerateTypeDefinitions(to, diffMD);
            CopyInstructions(to, diffMD);
            CopyAttributesAndProperties(to, diffMD);

        }

        public override void Dispose()
        {
            from?.Dispose();
            to?.Dispose();
        }

        protected override IMetadataTokenProvider Relinker(IMetadataTokenProvider mtp, IGenericParameterProvider context)
        {
            //if(mtp is TypeReference ttr && ttr.FullName == "System.Guid")
            //    Debugger.Break();

            switch (mtp)
            {
                case TypeReference tr when diffTypes.TryGetValue(tr.FullName, out var localType):
                    return localType;
                case MethodReference mr when diffMethods.TryGetValue(mr.FullName, out var localMethod):
                    return localMethod;
                case FieldReference fr when diffFields.TryGetValue(fr.FullName, out var localField):
                    return localField;
            }

            return diffMD.ImportReference(mtp);
        }


        protected override IEnumerable<TypeDefinition> GetChildrenToInclude(TypeDefinition type)
        {
            return nestedTypesToInclude.TryGetValue(type.FullName, out var result) ? result : null;
        }

        protected override IEnumerable<FieldDefinition> GetFieldsToInclude(TypeDefinition td)
        {
            return fieldsToInclude.TryGetValue(td.FullName, out var result) ? result : new List<FieldDefinition>();
        }

        protected override IEnumerable<MethodDefinition> GetMethodsToInclude(TypeDefinition td)
        {
            return methodsToInclude.TryGetValue(td.FullName, out var result) ? result : new List<MethodDefinition>();
        }

        protected override FieldReference GetOriginalField(FieldReference field,
                                                           ModuleDefinition fromModule,
                                                           ModuleDefinition toModule)
        {
            return toModule.ImportReference(fromModule.GetType(field.DeclaringType.FullName).Fields
                                                      .First(f => f.Name == field.Name));
        }

        protected override MethodReference GetOriginalMethod(MethodReference method,
                                                             ModuleDefinition fromModule,
                                                             ModuleDefinition toModule)
        {
            return toModule.ImportReference(fromModule.GetType(method.DeclaringType.FullName).Methods
                                                      .First(m => m.FullName == method.FullName));
        }

        protected override TypeReference GetOriginalType(TypeReference type,
                                                         ModuleDefinition fromModule,
                                                         ModuleDefinition toModule)
        {
            return toModule.ImportReference(fromModule.GetType(type.FullName));
        }

        protected override GenericParameter
            ResolveGenericParameter(GenericParameter gp, ModuleDefinition fromModule, ModuleDefinition toModule)
        {
            return fromModule.GetType(gp.DeclaringType.FullName).GenericParameters
                             .FirstOrDefault(g => g.Name == gp.Name);
        }

        private void InitDiff(IEnumerable<TypeDefinition> toTypes, TypeDefinition parent = null)
        {
            foreach (var toType in toTypes)
            {
                if (ExcludeTypes.Contains(toType.FullName) ||
                    parent == null && ExcludeNamespaces.Contains(toType.Namespace))
                    continue;

                var fromType = from.GetType(toType.FullName);

                if (fromType == null)
                {
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

                    InitDiff(toType.NestedTypes, toType);
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

                    if (toMethod.HasBody &&
                        !toMethod.Body.Instructions.SequenceEqual(fromMethod.Body.Instructions, InstructionComparer))
                    {
                        if (!methodsToInclude.TryGetValue(toType.FullName, out var list))
                            methodsToInclude[toType.FullName] = list = new List<MethodDefinition>();
                        list.Add(toMethod);
                    }
                }

                if (fieldsToInclude.ContainsKey(toType.FullName) || methodsToInclude.ContainsKey(toType.FullName) ||
                    nestedTypesToInclude.ContainsKey(toType.FullName))
                {
                    if (parent == null)
                        typesToInclude.Add(toType);
                    else
                    {
                        if (!nestedTypesToInclude.TryGetValue(toType.FullName, out var list))
                            nestedTypesToInclude[toType.FullName] = list = new List<TypeDefinition>();
                        list.Add(toType);
                    }
                }

                InitDiff(toType.NestedTypes, toType);
            }
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