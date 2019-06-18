using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using MonoMod.Utils;

namespace NetDiffPatch
{
    public abstract class DiffPatchBase : IDisposable
    {
        // Store full names of types/methods/types that need to be diffed
        private static readonly TypeReference EmptyType = new TypeReference("", "", null, null);
        protected readonly Dictionary<string, FieldDefinition> diffFields = new Dictionary<string, FieldDefinition>();

        protected readonly Dictionary<string, MethodDefinition>
            diffMethods = new Dictionary<string, MethodDefinition>();

        protected readonly Dictionary<string, TypeDefinition> diffTypes = new Dictionary<string, TypeDefinition>();

        // Namespaces to exclude from original assembly
        public HashSet<string> ExcludeNamespaces { get; set; } = new HashSet<string>();

        // Types to exclude from the original assembly
        public HashSet<string> ExcludeTypes { get; set; } = new HashSet<string>();

        public virtual void Dispose()
        {
        }

        /// <summary>
        ///     Copies attributes and generates properties from getters and setters in the target assembly.
        /// </summary>
        /// <param name="fromModule">Assemblies to copy from.</param>
        /// <param name="toModule">Assemblies to copy to.</param>
        protected void CopyAttributesAndProperties(ModuleDefinition fromModule, ModuleDefinition toModule)
        {
            foreach (var toTypePair in diffTypes)
            {
                var toType = toTypePair.Value;
                var fromType = fromModule.GetType(toType.FullName);

                foreach (var fromTypeProperty in fromType.Properties)
                {
                    var pd = toType.Properties.FirstOrDefault(p => p.Name == fromTypeProperty.Name);

                    MethodDefinition getter = null, setter = null;

                    var hasCustomGetter = fromTypeProperty.GetMethod != null &&
                                          diffMethods.TryGetValue(fromTypeProperty.GetMethod.FullName, out getter);
                    var hasCustomSetter = fromTypeProperty.SetMethod != null &&
                                          diffMethods.TryGetValue(fromTypeProperty.SetMethod.FullName, out setter);

                    if (!hasCustomGetter && !hasCustomSetter)
                        continue;

                    if (pd == null)
                    {
                        pd = new PropertyDefinition(fromTypeProperty.Name, fromTypeProperty.Attributes,
                            fromTypeProperty.PropertyType.Relink(Relinker, toType));
                        toType.Properties.Add(pd);
                    }
                    else
                    {
                        pd.PropertyType = fromTypeProperty.PropertyType.Relink(Relinker, toType);
                    }

                    if (hasCustomGetter)
                        pd.GetMethod = getter;
                    if (hasCustomSetter)
                        pd.SetMethod = setter;
                }

                // Remove old attributes cuz yolo
                toType.CustomAttributes.Clear();

                foreach (var fromTypeAttribute in fromType.CustomAttributes)
                    toType.CustomAttributes.Add(fromTypeAttribute.Relink(Relinker, toType));

                foreach (var toMethod in toType.Methods)
                {
                    var fromMethod = fromType.Methods.FirstOrDefault(m => m.FullName == toMethod.FullName);

                    if (fromMethod == null)
                        continue;

                    toMethod.CustomAttributes.Clear();
                    foreach (var fromMethodAttribute in fromMethod.CustomAttributes)
                        toMethod.CustomAttributes.Add(fromMethodAttribute.Relink(Relinker, toMethod));

                    for (var i = 0; i < toMethod.Parameters.Count; i++)
                    {
                        var pd = toMethod.Parameters[i];
                        var pOriginal = fromMethod.Parameters[i];

                        foreach (var pAttr in pOriginal.CustomAttributes)
                            pd.CustomAttributes.Add(pAttr.Relink(Relinker, toMethod));
                    }
                }
            }
        }

        /// <summary>
        ///     Copies instructions into all target method bodies.
        /// </summary>
        /// <param name="fromModule">Assembly to copy from.</param>
        /// <param name="toModule">Assembly to copy to.</param>
        protected void CopyInstructions(ModuleDefinition fromModule, ModuleDefinition toModule)
        {
            foreach (var corTypePair in diffTypes)
            {
                var toType = corTypePair.Value;
                var fromType = fromModule.GetType(toType.FullName);

                foreach (var toMethod in toType.Methods)
                {
                    var fromMethod = fromType.Methods.FirstOrDefault(m => m.FullName == toMethod.FullName);

                    if (fromMethod == null)
                        continue;

                    if (!fromMethod.HasBody)
                        continue;

                    toMethod.Body = fromMethod.Body.Clone(toMethod, Relinker);
                }
            }
        }

        /// <summary>
        ///     Copy over type definitions.
        /// </summary>
        /// <param name="fromModule">Assemblies to copy from.</param>
        /// <param name="toModule">Assemblies to copy to.</param>
        protected void GenerateTypeDefinitions(ModuleDefinition fromModule, ModuleDefinition toModule)
        {
            foreach (var typePair in diffTypes)
            {
                var td = typePair.Value;
                var fromType = fromModule.GetType(td.FullName);

                if (fromType.BaseType != null)
                    td.BaseType = fromType.BaseType.Relink(Relinker, td);

                foreach (var diffTypeInterface in fromType.Interfaces)
                {
                    if (td.Interfaces.Any(i => i.InterfaceType.FullName == diffTypeInterface.InterfaceType.FullName))
                        continue;

                    var imp = new InterfaceImplementation(diffTypeInterface.InterfaceType.Relink(Relinker, td));
                    td.Interfaces.Add(imp);
                }

                td.IsAbstract = fromType.IsAbstract;
                td.IsSequentialLayout = fromType.IsSequentialLayout;
                td.IsSealed = fromType.IsSealed;

                foreach (var field in GetFieldsToInclude(fromType))
                {
                    var fd = td.Fields.FirstOrDefault(f => f.Name == field.Name);

                    if (fd == null)
                    {
                        fd = new FieldDefinition(field.Name, field.Attributes, field.FieldType.Relink(Relinker, td));

                        if (field.HasConstant)
                            fd.Constant = field.Constant;
                        td.Fields.Add(fd);
                    }
                    else
                    {
                        fd.FieldType = field.FieldType.Relink(Relinker, td);
                        fd.Attributes = field.Attributes;
                    }

                    diffFields[fd.FullName] = fd;
                }

                foreach (var method in GetMethodsToInclude(fromType))
                {
                    var md = td.Methods.FirstOrDefault(m => m.FullName == method.FullName);

                    if (md == null)
                    {
                        md = new MethodDefinition(method.Name, method.Attributes, EmptyType);

                        td.Methods.Add(md);
                        md.ImplAttributes = method.ImplAttributes;
                        md.IsPreserveSig = method.IsPreserveSig;
                        md.IsPInvokeImpl = method.IsPInvokeImpl;

                        foreach (var @override in method.Overrides)
                            md.Overrides.Add(@override.Relink(Relinker, td));

                        foreach (var genPara in method.GenericParameters)
                            md.GenericParameters.Add(genPara.Clone(md));

                        md.ReturnType = method.ReturnType.Relink(Relinker, md);

                        if (method.PInvokeInfo != null)
                        {
                            var modRef =
                                toModule.ModuleReferences.FirstOrDefault(m => m.Name == method.PInvokeInfo.Module.Name);
                            if (modRef == null)
                            {
                                modRef = new ModuleReference(method.PInvokeInfo.Module.Name);
                                toModule.ModuleReferences.Add(modRef);
                            }

                            md.PInvokeInfo = new PInvokeInfo(method.PInvokeInfo.Attributes,
                                method.PInvokeInfo.EntryPoint, modRef);
                        }

                        foreach (var param in method.Parameters)
                        {
                            var pd = new ParameterDefinition(param.Name, param.Attributes,
                                param.ParameterType.Relink(Relinker, md))
                            {
                                IsIn = param.IsIn,
                                IsLcid = param.IsLcid,
                                IsOptional = param.IsOptional,
                                IsOut = param.IsOut,
                                IsReturnValue = param.IsReturnValue,
                                MarshalInfo = param.MarshalInfo
                            };
                            if (param.HasConstant)
                                pd.Constant = param.Constant;

                            md.Parameters.Add(pd);
                        }
                    }
                    else
                    {
                        md.ReturnType = method.ReturnType.Relink(Relinker, td);

                        md.Attributes = method.Attributes;
                        md.ImplAttributes = method.ImplAttributes;
                        md.IsPreserveSig = method.IsPreserveSig;
                        md.IsPInvokeImpl = method.IsPInvokeImpl;

                        foreach (var param in md.Parameters)
                            param.ParameterType = param.ParameterType.Relink(Relinker, td);
                    }

                    diffMethods[md.FullName] = md;
                }
            }
        }

        protected abstract IMetadataTokenProvider Relinker(IMetadataTokenProvider mtp,
            IGenericParameterProvider context);

        /// <summary>
        ///     Gets nested types of the given type to include in patching.
        /// </summary>
        /// <param name="type">Type to get nested types from.</param>
        /// <returns>Nested types include in the patching process.</returns>
        protected abstract IEnumerable<TypeDefinition> GetChildrenToInclude(TypeDefinition type);

        /// <summary>
        ///     Get fields from the type to include in patching.
        /// </summary>
        /// <param name="td">Type to search fields from.</param>
        /// <returns>Fields to include.</returns>
        protected abstract IEnumerable<FieldDefinition> GetFieldsToInclude(TypeDefinition td);

        /// <summary>
        ///     Get methods from the type to include in patching.
        /// </summary>
        /// <param name="td">Type to search method from.</param>
        /// <returns>Types to include.</returns>
        protected abstract IEnumerable<MethodDefinition> GetMethodsToInclude(TypeDefinition td);

        /// <summary>
        ///     Create type definitions in the target assembly.
        /// </summary>
        /// <param name="target">Assembly to include types into.</param>
        /// <param name="types">Types to include.</param>
        /// <param name="parent">Parent type.</param>
        protected void RegisterTypes(ModuleDefinition target,
            IEnumerable<TypeDefinition> types,
            TypeDefinition parent = null)
        {
            if (types == null)
                return;

            foreach (var type in types)
            {
                var td = target.GetType(type.FullName);

                if (td == null)
                {
                    td = new TypeDefinition(type.Namespace, type.Name, type.Attributes);

                    foreach (var genPara in type.GenericParameters)
                        td.GenericParameters.Add(genPara.Clone(td));

                    if (parent == null)
                        target.Types.Add(td);
                    else
                        parent.NestedTypes.Add(td);
                }

                diffTypes[td.FullName] = td;

                RegisterTypes(target, GetChildrenToInclude(type), td);
            }
        }
    }
}