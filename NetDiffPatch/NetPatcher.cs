using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using MonoMod.Utils;

namespace NetDiffPatch
{
    public class NetPatcher : DiffPatchBase
    {
        private readonly ModuleDefinition diffModule;

        public NetPatcher(ModuleDefinition diffModule) { this.diffModule = diffModule; }

        public override void Dispose() { diffModule?.Dispose(); }

        private ModuleDefinition targetModule;

        public void Patch(ModuleDefinition module)
        {
            targetModule = module;
            RegisterTypes(module, diffModule.Types);
            GenerateTypeDefinitions(diffModule, module);
            CopyInstructions(diffModule, module);
            CopyAttributesAndProperties(diffModule, module);
        }

        protected override IMetadataTokenProvider Relinker(IMetadataTokenProvider mtp, IGenericParameterProvider context)
        {

            switch (mtp)
            {
                case TypeReference tr when tr.Module != targetModule:
                    return targetModule.GetType(tr.FullName);
                case MethodReference mr when mr.Module != targetModule:
                    return targetModule.GetType(mr.DeclaringType.FullName).Methods.First(m => m.FullName == mr.FullName);
                case FieldReference fr when fr.Module != targetModule:
                    return targetModule.GetType(fr.DeclaringType.FullName).Fields.First(f => f.FullName == fr.FullName);
            }

            return mtp;
        }

        protected override IEnumerable<TypeDefinition> GetChildrenToInclude(TypeDefinition type)
        {
            return type.NestedTypes;
        }

        protected override IEnumerable<FieldDefinition> GetFieldsToInclude(TypeDefinition type) { return type.Fields; }

        protected override IEnumerable<MethodDefinition> GetMethodsToInclude(TypeDefinition type)
        {
            return type.Methods;
        }

        protected override FieldReference GetOriginalField(FieldReference field,
                                                           ModuleDefinition fromModule,
                                                           ModuleDefinition toModule)
        {
            return toModule.GetType(field.DeclaringType.FullName).Fields.First(f => f.Name == field.Name);
        }

        protected override MethodReference GetOriginalMethod(MethodReference method,
                                                             ModuleDefinition fromModule,
                                                             ModuleDefinition toModule)
        {
            return toModule.GetType(method.DeclaringType.FullName).Methods.First(m => m.FullName == method.FullName);
        }

        protected override TypeReference GetOriginalType(TypeReference type,
                                                         ModuleDefinition fromModule,
                                                         ModuleDefinition toModule)
        {
            return toModule.GetType(type.FullName);
        }

        protected override GenericParameter
            ResolveGenericParameter(GenericParameter gp, ModuleDefinition fromModule, ModuleDefinition toModule)
        {
            return toModule.GetType(gp.DeclaringType.FullName).GenericParameters.FirstOrDefault(g => g.Name == gp.Name);
        }
    }
}