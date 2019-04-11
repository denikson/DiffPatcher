using System.Collections.Generic;
using Mono.Cecil;

namespace NetPatch
{
    public class NetPatcher : DiffPatchBase
    {
        private readonly ModuleDefinition diffModule;

        public NetPatcher(ModuleDefinition diffModule) { this.diffModule = diffModule; }

        public override void Dispose() { diffModule?.Dispose(); }

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
    }
}