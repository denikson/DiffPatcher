using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace NetPatch
{
    public abstract class DiffPatchBase : IDisposable
    {
        private readonly Dictionary<string, FieldDefinition> diffFields = new Dictionary<string, FieldDefinition>();

        private readonly Dictionary<string, MethodDefinition> diffMethods = new Dictionary<string, MethodDefinition>();

        private readonly Dictionary<string, TypeDefinition> diffTypes = new Dictionary<string, TypeDefinition>();

        public HashSet<string> ExcludeNamespaces { get; set; }

        public HashSet<string> ExcludeTypes { get; set; }

        public virtual void Dispose() { }

        public FieldReference ImportField(ModuleDefinition md, FieldReference fr)
        {
            if (fr.DeclaringType is GenericInstanceType git)
            {
                var res = ImportField(md, fr.Resolve());
                var newGit = new GenericInstanceType(res.DeclaringType);

                foreach (var gitGenericArgument in git.GenericArguments)
                    newGit.GenericArguments.Add(ImportType(md, gitGenericArgument));

                return new FieldReference(res.Name, res.FieldType, newGit);
            }

            return diffFields.TryGetValue(fr.FullName, out var result)
                ? result
                : md.GetType(fr.DeclaringType.FullName).Fields.First(f => f.Name == fr.Name);
        }

        public MethodReference ImportMethod(ModuleDefinition md, MethodReference mr)
        {
            if (mr is GenericInstanceMethod gim)
            {
                var newGim = new GenericInstanceMethod(ImportMethod(md, gim.ElementMethod.Resolve()));

                foreach (var gimGenericArgument in gim.GenericArguments)
                    newGim.GenericArguments.Add(ImportType(md, gimGenericArgument.DeclaringType));

                return newGim;
            }

            if (mr.DeclaringType is GenericInstanceType git)
            {
                var res = ImportMethod(md, mr.Resolve());
                var newGit = new GenericInstanceType(res.DeclaringType);

                foreach (var gitGenericArgument in git.GenericArguments)
                    newGit.GenericArguments.Add(ImportType(md, gitGenericArgument));

                var methodReference = new MethodReference(res.Name, res.ReturnType, newGit) {HasThis = res.HasThis};

                foreach (var parameterDefinition in res.Parameters)
                    methodReference.Parameters.Add(new ParameterDefinition(parameterDefinition.Name,
                                                                           parameterDefinition.Attributes,
                                                                           ImportType(
                                                                               md, parameterDefinition.ParameterType)));

                return methodReference;
            }

            return diffMethods.TryGetValue(mr.FullName, out var result)
                ? result
                : md.GetType(mr.DeclaringType.FullName).Methods.First(m => m.FullName == mr.FullName);
        }

        public TypeReference ImportType(ModuleDefinition module,
                                        TypeReference tr,
                                        IGenericParameterProvider genericParamProvider = null)
        {
            if (diffTypes.TryGetValue(tr.FullName, out var result))
                return result;

            switch (tr)
            {
                case ArrayType at: return ImportType(module, at.ElementType, genericParamProvider).MakeArrayType();
                case ByReferenceType brt:
                    return ImportType(module, brt.ElementType, genericParamProvider).MakeByReferenceType();
                case PointerType pt: return ImportType(module, pt.ElementType, genericParamProvider).MakePointerType();
                case PinnedType pt: return ImportType(module, pt.ElementType, genericParamProvider).MakePinnedType();
                case GenericInstanceType git:
                    return ImportType(module, git.ElementType, genericParamProvider).MakeGenericInstanceType(
                        git.GenericArguments.Select(t => ImportType(module, t, genericParamProvider)).ToArray());
                case GenericParameter gp:
                {
                    GenericParameter res;
                    if (gp.DeclaringMethod != null)
                    {
                        var md = genericParamProvider ??
                                 module.GetType(gp.DeclaringMethod.DeclaringType.FullName).Methods
                                       .First(m => m.FullName == gp.DeclaringMethod.FullName);
                        res = md.GenericParameters.FirstOrDefault(g => g.Name == gp.Name);
                        if (res != null)
                            return res;
                    }

                    res = module.GetType(gp.DeclaringType.FullName).GenericParameters
                                .FirstOrDefault(g => g.Name == gp.Name);
                    if (res != null)
                        return res;

                    throw new Exception($"Tried to resolve generic type {gp} that does not exist!");
                }
            }

            return module.GetType(tr.FullName);
        }

        protected void CopyAttributesAndProperties(ModuleDefinition fromModule, ModuleDefinition toModule)
        {
            foreach (var toTypePair in diffTypes)
            {
                var toType = toTypePair.Value;
                var fromType = fromModule.GetType(toType.FullName);

                foreach (var fromTypeProperty in fromType.Properties)
                {
                    var pd = toType.Properties.FirstOrDefault(p => p.Name == fromTypeProperty.Name);

                    if (pd == null)
                    {
                        pd = new PropertyDefinition(fromTypeProperty.Name, fromTypeProperty.Attributes,
                                                    ImportType(toModule, fromTypeProperty.PropertyType));
                        toType.Properties.Add(pd);
                    }
                    else
                        pd.PropertyType = ImportType(toModule, fromTypeProperty.PropertyType);

                    if (fromTypeProperty.GetMethod != null)
                        pd.GetMethod = diffMethods[fromTypeProperty.GetMethod.FullName];
                    if (fromTypeProperty.SetMethod != null)
                        pd.SetMethod = diffMethods[fromTypeProperty.SetMethod.FullName];
                }

                // Remove old attributes cuz yolo
                toType.CustomAttributes.Clear();

                foreach (var fromTypeAttribute in fromType.CustomAttributes)
                    toType.CustomAttributes.Add(new CustomAttribute(
                                                    ImportMethod(toModule, fromTypeAttribute.Constructor),
                                                    fromTypeAttribute.GetBlob()));

                foreach (var toMethod in toType.Methods)
                {
                    var fromMethod = fromType.Methods.FirstOrDefault(m => m.FullName == toMethod.FullName);

                    if (fromMethod == null)
                        continue;

                    toMethod.CustomAttributes.Clear();
                    foreach (var fromMethodAttribute in fromMethod.CustomAttributes)
                        toMethod.CustomAttributes.Add(new CustomAttribute(
                                                          ImportMethod(toModule, fromMethodAttribute.Constructor),
                                                          fromMethodAttribute.GetBlob()));

                    for (var i = 0; i < toMethod.Parameters.Count; i++)
                    {
                        var pd = toMethod.Parameters[i];
                        var pOriginal = fromMethod.Parameters[i];

                        foreach (var pAttr in pOriginal.CustomAttributes)
                            pd.CustomAttributes.Add(
                                new CustomAttribute(ImportMethod(toModule, pAttr.Constructor), pAttr.GetBlob()));
                    }
                }
            }
        }

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

                    toMethod.Body.Instructions.Clear();
                    toMethod.Body.Variables.Clear();

                    var il = toMethod.Body.GetILProcessor();

                    var varTable = new Dictionary<int, VariableDefinition>();
                    var fixupTable = new Dictionary<int, int>();
                    var fixupArrayTable = new Dictionary<int, int[]>();

                    toMethod.Body.InitLocals = fromMethod.Body.InitLocals;

                    foreach (var variableDefinition in fromMethod.Body.Variables)
                    {
                        var vd = new VariableDefinition(ImportType(toModule, variableDefinition.VariableType));
                        toMethod.Body.Variables.Add(vd);
                        varTable[vd.Index] = vd;
                    }

                    for (var index = 0; index < fromMethod.Body.Instructions.Count; index++)
                    {
                        var ins = fromMethod.Body.Instructions[index];

                        switch (ins.Operand)
                        {
                            case CallSite cs:
                                throw new NotImplementedException($"Got call site: {cs}. Dunno how to handle that.");
                            case Instruction label:
                                fixupTable[index] = fromMethod.Body.Instructions.IndexOf(label);
                                il.Emit(ins.OpCode, Instruction.Create(OpCodes.Nop));
                                break;
                            case Instruction[] labels:
                                fixupArrayTable[index] =
                                    labels.Select(l => fromMethod.Body.Instructions.IndexOf(l)).ToArray();
                                il.Emit(ins.OpCode, new Instruction[0]);
                                break;
                            case VariableDefinition vd:
                                il.Emit(ins.OpCode, varTable[vd.Index]);
                                break;
                            case FieldReference fr:
                                il.Emit(ins.OpCode, ImportField(toModule, fr));
                                break;
                            case MethodReference mr:
                                il.Emit(ins.OpCode, ImportMethod(toModule, mr));
                                break;
                            case TypeReference tr:
                                il.Emit(ins.OpCode, ImportType(toModule, tr));
                                break;
                            case ParameterDefinition pd:
                                il.Emit(ins.OpCode, toMethod.Parameters[pd.Index]);
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
                        toMethod.Body.Instructions[entry.Key].Operand = toMethod.Body.Instructions[entry.Value];

                    foreach (var entry in fixupArrayTable)
                        toMethod.Body.Instructions[entry.Key].Operand =
                            entry.Value.Select(i => toMethod.Body.Instructions[i]).ToArray();

                    toMethod.Body.ExceptionHandlers.Clear();

                    foreach (var bodyExceptionHandler in fromMethod.Body.ExceptionHandlers)
                    {
                        var exHandler = new ExceptionHandler(bodyExceptionHandler.HandlerType);

                        exHandler.CatchType = bodyExceptionHandler.CatchType != null
                            ? ImportType(toModule, bodyExceptionHandler.CatchType)
                            : null;

                        if (bodyExceptionHandler.TryStart != null)
                            exHandler.TryStart =
                                toMethod.Body.Instructions[
                                    fromMethod.Body.Instructions.IndexOf(bodyExceptionHandler.TryStart)];

                        if (bodyExceptionHandler.TryEnd != null)
                            exHandler.TryEnd =
                                toMethod.Body.Instructions[
                                    fromMethod.Body.Instructions.IndexOf(bodyExceptionHandler.TryEnd)];

                        if (bodyExceptionHandler.FilterStart != null)
                            exHandler.FilterStart =
                                toMethod.Body.Instructions[
                                    fromMethod.Body.Instructions.IndexOf(bodyExceptionHandler.FilterStart)];

                        if (bodyExceptionHandler.HandlerStart != null)
                            exHandler.HandlerStart =
                                toMethod.Body.Instructions[
                                    fromMethod.Body.Instructions.IndexOf(bodyExceptionHandler.HandlerStart)];

                        if (bodyExceptionHandler.HandlerEnd != null)
                            exHandler.HandlerEnd =
                                toMethod.Body.Instructions[
                                    fromMethod.Body.Instructions.IndexOf(bodyExceptionHandler.HandlerEnd)];

                        toMethod.Body.ExceptionHandlers.Add(exHandler);
                    }
                }
            }
        }

        protected void GenerateTypeDefinitions(ModuleDefinition fromModule, ModuleDefinition toModule)
        {
            foreach (var typePair in diffTypes)
            {
                var td = typePair.Value;
                var fromType = fromModule.GetType(td.FullName);

                if (fromType.BaseType != null)
                    td.BaseType = ImportType(toModule, fromType.BaseType);

                foreach (var diffTypeInterface in fromType.Interfaces)
                {
                    if (td.Interfaces.Any(i => i.InterfaceType.FullName == diffTypeInterface.InterfaceType.FullName))
                        continue;

                    var imp = new InterfaceImplementation(ImportType(toModule, diffTypeInterface.InterfaceType));
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
                        fd = new FieldDefinition(field.Name, field.Attributes, ImportType(toModule, field.FieldType));

                        if (field.HasConstant)
                            fd.Constant = field.Constant;
                        td.Fields.Add(fd);
                    }
                    else
                    {
                        fd.FieldType = ImportType(toModule, field.FieldType);
                        fd.Attributes = field.Attributes;
                    }

                    diffFields[fd.FullName] = fd;
                }

                foreach (var method in GetMethodsToInclude(fromType))
                {
                    var md = td.Methods.FirstOrDefault(m => m.FullName == method.FullName);

                    if (md == null)
                    {
                        md = new MethodDefinition(method.Name, method.Attributes, toModule.GetType("System.Void"));

                        td.Methods.Add(md);

                        foreach (var genPara in method.GenericParameters)
                        {
                            var gp = new GenericParameter(genPara.Name, md) {Attributes = genPara.Attributes};
                            md.GenericParameters.Add(gp);
                        }

                        md.ReturnType = ImportType(toModule, method.ReturnType);
                        md.IsInternalCall = method.IsInternalCall;
                        md.IsRuntime = method.IsRuntime;
                        md.IsManaged = method.IsManaged;

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
                                                             ImportType(toModule, param.ParameterType));
                            if (param.HasConstant)
                                pd.Constant = param.Constant;

                            md.Parameters.Add(pd);
                        }
                    }
                    else
                    {
                        md.ReturnType = ImportType(toModule, method.ReturnType);

                        md.IsInternalCall = method.IsInternalCall;
                        md.IsRuntime = method.IsRuntime;
                        md.IsManaged = method.IsManaged;
                        md.IsPrivate = false;
                        md.IsAssembly = false;
                        md.IsPublic = true;

                        foreach (var param in md.Parameters)
                            param.ParameterType = ImportType(toModule, param.ParameterType);
                    }

                    diffMethods[md.FullName] = md;
                }
            }
        }

        protected abstract IEnumerable<TypeDefinition> GetChildrenToInclude(TypeDefinition type);

        protected abstract IEnumerable<FieldDefinition> GetFieldsToInclude(TypeDefinition td);

        protected abstract IEnumerable<MethodDefinition> GetMethodsToInclude(TypeDefinition td);

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
                        td.GenericParameters.Add(
                            new GenericParameter(genPara.Name, td) {Attributes = genPara.Attributes});

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