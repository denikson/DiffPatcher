using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;

namespace NetPatch
{
    public class NetPatcher : DiffPatchBase
    {
        private readonly ModuleDefinition diffModule;

        public NetPatcher(ModuleDefinition diffModule) { this.diffModule = diffModule; }

        public override void Dispose() { diffModule?.Dispose(); }
        protected override GenericParameter ResolveGenericParameter(GenericParameter gp, ModuleDefinition fromModule, ModuleDefinition toModule) => toModule.GetType(gp.DeclaringType.FullName).GenericParameters
                                                                                                                                                              .FirstOrDefault(g => g.Name == gp.Name);

        public void Patch(ModuleDefinition module)
        {
            RegisterTypes(module, diffModule.Types);
            GenerateTypeDefinitions(diffModule, module);
            CopyInstructions(diffModule, module);
            CopyAttributesAndProperties(diffModule, module);
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

        protected override MethodReference GetOriginalMethod(MethodReference method,
                                                             ModuleDefinition fromModule,
                                                             ModuleDefinition toModule) =>
            toModule.GetType(method.DeclaringType.FullName).Methods.First(m => m.FullName == method.FullName);

        protected override FieldReference GetOriginalField(FieldReference field,
                                                           ModuleDefinition fromModule,
                                                           ModuleDefinition toModule) =>
            toModule.GetType(field.DeclaringType.FullName).Fields.First(f => f.Name == field.Name);

        protected override TypeReference GetOriginalType(TypeReference type,
                                                         ModuleDefinition fromModule,
                                                         ModuleDefinition toModule) =>
            toModule.GetType(type.FullName);
    }
}