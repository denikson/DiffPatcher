using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;
using CustomAttributeNamedArgument = Mono.Cecil.CustomAttributeNamedArgument;
using ICustomAttributeProvider = Mono.Cecil.ICustomAttributeProvider;
using MethodBody = Mono.Cecil.Cil.MethodBody;
using TypeOrTypeInfo = System.Type;

namespace MonoMod.Utils
{
    public class RelinkFailedException : Exception
    {
        public const string DefaultMessage = "MonoMod failed relinking";
        public IMetadataTokenProvider Context;

        public IMetadataTokenProvider MTP;

        public RelinkFailedException(IMetadataTokenProvider mtp, IMetadataTokenProvider context = null)
            : this(_Format(DefaultMessage, mtp, context), mtp, context)
        {
        }

        public RelinkFailedException(string message,
            IMetadataTokenProvider mtp, IMetadataTokenProvider context = null)
            : base(message)
        {
            MTP = mtp;
            Context = context;
        }

        public RelinkFailedException(string message, Exception innerException,
            IMetadataTokenProvider mtp, IMetadataTokenProvider context = null)
            : base(message ?? _Format(DefaultMessage, mtp, context), innerException)
        {
            MTP = mtp;
            Context = context;
        }

        protected static string _Format(string message,
            IMetadataTokenProvider mtp, IMetadataTokenProvider context)
        {
            if (mtp == null && context == null)
                return message;

            var builder = new StringBuilder(message);
            builder.Append(" ");

            if (mtp != null)
                builder.Append(mtp);

            if (context != null)
                builder.Append(" ");

            if (context != null)
                builder.Append("(context: ").Append(context).Append(")");

            return builder.ToString();
        }
    }

    public class RelinkTargetNotFoundException : RelinkFailedException
    {
        public new const string DefaultMessage = "MonoMod relinker failed finding";

        public RelinkTargetNotFoundException(IMetadataTokenProvider mtp, IMetadataTokenProvider context = null)
            : base(_Format(DefaultMessage, mtp, context), mtp, context)
        {
        }

        public RelinkTargetNotFoundException(string message,
            IMetadataTokenProvider mtp, IMetadataTokenProvider context = null)
            : base(message ?? DefaultMessage, mtp, context)
        {
        }

        public RelinkTargetNotFoundException(string message, Exception innerException,
            IMetadataTokenProvider mtp, IMetadataTokenProvider context = null)
            : base(message ?? DefaultMessage, innerException, mtp, context)
        {
        }
    }
}

namespace MonoMod.Utils
{
    public delegate IMetadataTokenProvider Relinker(IMetadataTokenProvider mtp, IGenericParameterProvider context);

    /// <summary>
    ///     Huge collection of MonoMod-related Mono.Cecil extensions.
    /// </summary>
    public static class MonoModExt
    {
        public static Dictionary<string, object> SharedData = new Dictionary<string, object>();

        private static readonly Regex TypeGenericParamRegex = new Regex(@"\!\d");
        private static readonly Regex MethodGenericParamRegex = new Regex(@"\!\!\d");

        private static readonly Type t_ParamArrayAttribute = typeof(ParamArrayAttribute);

        public static ModuleDefinition ReadModule(string path, ReaderParameters rp)
        {
            Retry:
            try
            {
                return ModuleDefinition.ReadModule(path, rp);
            }
            catch
            {
                if (rp.ReadSymbols)
                {
                    rp.ReadSymbols = false;
                    goto Retry;
                }

                throw;
            }
        }

        public static ModuleDefinition ReadModule(Stream input, ReaderParameters rp)
        {
            Retry:
            try
            {
                return ModuleDefinition.ReadModule(input, rp);
            }
            catch
            {
                if (rp.ReadSymbols)
                {
                    rp.ReadSymbols = false;
                    goto Retry;
                }

                throw;
            }
        }

        public static MethodDefinition Clone(this MethodDefinition o, MethodDefinition c = null, TypeDefinition owner = null, Relinker typeRelinker = null)
        {
            if (o == null)
                return null;
            if (c == null)
                c = new MethodDefinition(o.Name, o.Attributes, typeRelinker != null ? o.ReturnType.Relink(typeRelinker, owner) : o.ReturnType);
            c.Name = o.Name;
            c.Attributes = o.Attributes;
            c.ReturnType = o.ReturnType;
            c.DeclaringType = owner ?? o.DeclaringType;
            c.MetadataToken = o.MetadataToken;
            c.Body = o.Body.Clone(c, typeRelinker);
            c.Attributes = o.Attributes;
            c.ImplAttributes = o.ImplAttributes;
            c.PInvokeInfo = o.PInvokeInfo;
            c.IsPreserveSig = o.IsPreserveSig;
            c.IsPInvokeImpl = o.IsPInvokeImpl;

            foreach (var genParam in o.GenericParameters)
                c.GenericParameters.Add(genParam.Clone(c));

            foreach (var param in o.Parameters)
                c.Parameters.Add(typeRelinker != null ? param.Relink(typeRelinker, owner) : param.Clone());

            foreach (var attrib in o.CustomAttributes)
                c.CustomAttributes.Add(attrib.Clone(owner, typeRelinker));

            foreach (var @override in o.Overrides)
                c.Overrides.Add(typeRelinker != null ? @override.Relink(typeRelinker, owner) : @override);

            return c;
        }

        public static MethodBody Clone(this MethodBody bo, MethodDefinition m, Relinker relinker = null)
        {
            if (bo == null)
                return null;

            var bc = new MethodBody(m);
            bc.MaxStackSize = bo.MaxStackSize;
            bc.InitLocals = bo.InitLocals;
            bc.LocalVarToken = bo.LocalVarToken;

            bc.Instructions.AddRange(bo.Instructions.Select(o =>
            {
                var c = Instruction.Create(OpCodes.Nop);
                c.OpCode = o.OpCode;
                switch (o.Operand)
                {
                    case ParameterDefinition pdRef:
                        c.Operand = m.Parameters[pdRef.Index];
                        break;
                    case IMetadataTokenProvider reference when relinker != null:
                        c.Operand = reference.Relink(relinker, m);
                        break;
                    default:
                        c.Operand = o.Operand;
                        break;
                }
                c.Offset = o.Offset;
                return c;
            }));

            foreach (var c in bc.Instructions)
                if (c.Operand is Instruction target)
                    c.Operand = bc.Instructions[bo.Instructions.IndexOf(target)];
                else if (c.Operand is Instruction[] targets)
                    c.Operand = targets.Select(i => bc.Instructions[bo.Instructions.IndexOf(i)]).ToArray();

            bc.ExceptionHandlers.AddRange(bo.ExceptionHandlers.Select(o =>
            {
                var c = new ExceptionHandler(o.HandlerType);
                c.TryStart = o.TryStart == null ? null : bc.Instructions[bo.Instructions.IndexOf(o.TryStart)];
                c.TryEnd = o.TryEnd == null ? null : bc.Instructions[bo.Instructions.IndexOf(o.TryEnd)];
                c.FilterStart = o.FilterStart == null ? null : bc.Instructions[bo.Instructions.IndexOf(o.FilterStart)];
                c.HandlerStart = o.HandlerStart == null
                    ? null
                    : bc.Instructions[bo.Instructions.IndexOf(o.HandlerStart)];
                c.HandlerEnd = o.HandlerEnd == null ? null : bc.Instructions[bo.Instructions.IndexOf(o.HandlerEnd)];
                c.CatchType = relinker != null ? o.CatchType.Relink(relinker, m) : o.CatchType;
                return c;
            }));

            bc.Variables.AddRange(bo.Variables.Select(o =>
            {
                var c = new VariableDefinition(relinker != null ? o.VariableType.Relink(relinker, m) : o.VariableType);
                return c;
            }));

#if !CECIL0_9
            m.CustomDebugInformations.AddRange(bo.Method
                .CustomDebugInformations); // Abstract. TODO: Implement deep CustomDebugInformations copy.
            m.DebugInformation.SequencePoints.AddRange(bo.Method.DebugInformation.SequencePoints.Select(o =>
            {
                var c = new SequencePoint(bc.Instructions.FirstOrDefault(i => i.Offset == o.Offset),
                    o.Document);
                c.StartLine = o.StartLine;
                c.StartColumn = o.StartColumn;
                c.EndLine = o.EndLine;
                c.EndColumn = o.EndColumn;
                return c;
            }));
#endif

            return bc;
        }

        public static readonly FieldInfo f_GenericParameter_position =
            typeof(GenericParameter).GetField("position",
                BindingFlags.Instance | BindingFlags.NonPublic);

        public static readonly FieldInfo f_GenericParameter_type =
            typeof(GenericParameter).GetField("type",
                BindingFlags.Instance | BindingFlags.NonPublic);

        public static GenericParameter Update(this GenericParameter param, GenericParameter other)
        {
            return param.Update(other.Position, other.Type);
        }

        public static GenericParameter Update(this GenericParameter param, int position, GenericParameterType type)
        {
            f_GenericParameter_position.SetValue(param, position);
            f_GenericParameter_type.SetValue(param, type);
            return param;
        }

        public static void AddAttribute(this ICustomAttributeProvider cap, MethodReference constructor)
        {
            cap.AddAttribute(new CustomAttribute(constructor));
        }

        public static void AddAttribute(this ICustomAttributeProvider cap, CustomAttribute attr)
        {
            cap.CustomAttributes.Add(attr);
        }

        /// <summary>
        ///     Determines if the attribute provider has got a specific MonoMod attribute.
        /// </summary>
        /// <returns><c>true</c> if the attribute provider contains the given MonoMod attribute, <c>false</c> otherwise.</returns>
        /// <param name="cap">Attribute provider to check.</param>
        /// <param name="attribute">Attribute.</param>
        public static bool HasMMAttribute(this ICustomAttributeProvider cap, string attribute)
        {
            return cap.HasCustomAttribute("MonoMod.MonoMod" + attribute);
        }

        public static CustomAttribute GetMMAttribute(this ICustomAttributeProvider cap, string attribute)
        {
            return cap.GetCustomAttribute("MonoMod.MonoMod" + attribute);
        }

        public static CustomAttribute GetNextMMAttribute(this ICustomAttributeProvider cap, string attribute)
        {
            return cap.GetNextCustomAttribute("MonoMod.MonoMod" + attribute);
        }

        public static CustomAttribute GetCustomAttribute(this ICustomAttributeProvider cap, string attribute)
        {
            if (cap == null || !cap.HasCustomAttributes) return null;
            foreach (var attrib in cap.CustomAttributes)
                if (attrib.AttributeType.FullName == attribute)
                    return attrib;
            return null;
        }

        public static CustomAttribute GetNextCustomAttribute(this ICustomAttributeProvider cap, string attribute)
        {
            if (cap == null || !cap.HasCustomAttributes) return null;
            var next = false;
            for (var i = 0; i < cap.CustomAttributes.Count; i++)
            {
                var attrib = cap.CustomAttributes[i];
                if (attrib.AttributeType.FullName != attribute)
                    continue;
                if (!next)
                {
                    cap.CustomAttributes.RemoveAt(i);
                    i--;
                    next = true;
                    continue;
                }

                return attrib;
            }

            return null;
        }

        /// <summary>
        ///     Determines if the attribute provider has got a specific custom attribute.
        /// </summary>
        /// <returns><c>true</c> if the attribute provider contains the given custom attribute, <c>false</c> otherwise.</returns>
        /// <param name="cap">Attribute provider to check.</param>
        /// <param name="attribute">Attribute.</param>
        public static bool HasCustomAttribute(this ICustomAttributeProvider cap, string attribute)
        {
            return cap.GetCustomAttribute(attribute) != null;
        }

        public static string GetFindableID(this MethodBase method, string name = null,
            string type = null, bool withType = true, bool proxyMethod = false, bool simple = false)
        {
            while (method is MethodInfo && method.IsGenericMethod &&
                   !method.IsGenericMethodDefinition)
                method = ((MethodInfo) method).GetGenericMethodDefinition();

            var builder = new StringBuilder();

            if (simple)
            {
                if (withType && method.DeclaringType != null)
                    builder.Append(type ?? method.DeclaringType.FullName).Append("::");
                builder.Append(name ?? method.Name);
                return builder.ToString();
            }

            builder
                .Append((method as MethodInfo)?.ReturnType?.FullName ?? "System.Void")
                .Append(" ");

            if (withType)
                builder.Append(type ?? method.DeclaringType.FullName.Replace("+", "/")).Append("::");

            builder
                .Append(name ?? method.Name);

            if (method.ContainsGenericParameters)
            {
                builder.Append("<");
                var arguments = method.GetGenericArguments();
                for (var i = 0; i < arguments.Length; i++)
                {
                    if (i > 0)
                        builder.Append(",");
                    builder.Append(arguments[i].Name);
                }

                builder.Append(">");
            }

            builder.Append("(");

            var parameters = method.GetParameters();
            for (var i = proxyMethod ? 1 : 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                if (i > (proxyMethod ? 1 : 0))
                    builder.Append(",");

#if NETSTANDARD
                if (System.Reflection.CustomAttributeExtensions.IsDefined(parameter, t_ParamArrayAttribute, false))
#else
                if (t_ParamArrayAttribute.GetCustomAttributes(t_ParamArrayAttribute, false).Length != 0)
#endif
                    builder.Append("...,");

                builder.Append(parameter.ParameterType.FullName);
            }

            builder.Append(")");

            return builder.ToString();
        }

        public static void UpdateOffsets(this MethodBody body, int instri, int delta)
        {
            for (var offsi = body.Instructions.Count - 1; instri <= offsi; offsi--)
                body.Instructions[offsi].Offset += delta;
        }

        public static int GetInt(this Instruction instr)
        {
            var op = instr.OpCode;
            if (op == OpCodes.Ldc_I4_M1) return -1;
            if (op == OpCodes.Ldc_I4_0) return 0;
            if (op == OpCodes.Ldc_I4_1) return 1;
            if (op == OpCodes.Ldc_I4_2) return 2;
            if (op == OpCodes.Ldc_I4_3) return 3;
            if (op == OpCodes.Ldc_I4_4) return 4;
            if (op == OpCodes.Ldc_I4_5) return 5;
            if (op == OpCodes.Ldc_I4_6) return 6;
            if (op == OpCodes.Ldc_I4_7) return 7;
            if (op == OpCodes.Ldc_I4_8) return 8;
            if (op == OpCodes.Ldc_I4_S) return (sbyte) instr.Operand;
            return (int) instr.Operand;
        }

        public static int? GetIntOrNull(this Instruction instr)
        {
            var op = instr.OpCode;
            if (op == OpCodes.Ldc_I4_M1) return -1;
            if (op == OpCodes.Ldc_I4_0) return 0;
            if (op == OpCodes.Ldc_I4_1) return 1;
            if (op == OpCodes.Ldc_I4_2) return 2;
            if (op == OpCodes.Ldc_I4_3) return 3;
            if (op == OpCodes.Ldc_I4_4) return 4;
            if (op == OpCodes.Ldc_I4_5) return 5;
            if (op == OpCodes.Ldc_I4_6) return 6;
            if (op == OpCodes.Ldc_I4_7) return 7;
            if (op == OpCodes.Ldc_I4_8) return 8;
            if (op == OpCodes.Ldc_I4_S) return (sbyte) instr.Operand;
            if (op == OpCodes.Ldc_I4) return (int) instr.Operand;
            return null;
        }

        public static ParameterDefinition GetParam(this Instruction instr, MethodDefinition method)
        {
            var op = instr.OpCode;
            var offs = method.HasThis ? -1 : 0;
            if (op == OpCodes.Ldarg_0) return method.HasThis ? null : method.Parameters[offs + 0];
            if (op == OpCodes.Ldarg_1) return method.Parameters[offs + 1];
            if (op == OpCodes.Ldarg_2) return method.Parameters[offs + 2];
            if (op == OpCodes.Ldarg_3) return method.Parameters[offs + 3];
            if (op == OpCodes.Ldarg_S) return (ParameterDefinition) instr.Operand;
            if (op == OpCodes.Ldarg) return (ParameterDefinition) instr.Operand;
            return null;
        }

        public static VariableDefinition GetLocal(this Instruction instr, MethodDefinition method)
        {
            var op = instr.OpCode;
            if (op == OpCodes.Ldloc_0) return method.Body.Variables[0];
            if (op == OpCodes.Ldloc_1) return method.Body.Variables[1];
            if (op == OpCodes.Ldloc_2) return method.Body.Variables[2];
            if (op == OpCodes.Ldloc_3) return method.Body.Variables[3];
            if (op == OpCodes.Ldloca_S) return (VariableDefinition) instr.Operand;
            if (op == OpCodes.Ldloc) return (VariableDefinition) instr.Operand;
            return null;
        }

        public static void AddRange<T>(this Collection<T> list, IEnumerable<T> other)
        {
            foreach (var entry in other)
                list.Add(entry);
        }

        public static void AddRange(this IDictionary dict, IDictionary other)
        {
            foreach (DictionaryEntry entry in other)
                dict.Add(entry.Key, entry.Value);
        }

        public static void AddRange<K, V>(this IDictionary<K, V> dict, IDictionary<K, V> other)
        {
            foreach (var entry in other)
                dict.Add(entry.Key, entry.Value);
        }

        public static void AddRange<K, V>(this Dictionary<K, V> dict, Dictionary<K, V> other)
        {
            foreach (var entry in other)
                dict.Add(entry.Key, entry.Value);
        }

        public static void InsertRange<T>(this Collection<T> list, int index, IEnumerable<T> other)
        {
            foreach (var entry in other)
                list.Insert(index++, entry);
        }

        public static void PushRange<T>(this Stack<T> stack, IEnumerable<T> other)
        {
            foreach (var entry in other)
                stack.Push(entry);
        }

        public static void PopRange<T>(this Stack<T> stack, int n)
        {
            for (var i = 0; i < n; i++)
                stack.Pop();
        }

        public static void EnqueueRange<T>(this Queue<T> queue, IEnumerable<T> other)
        {
            foreach (var entry in other)
                queue.Enqueue(entry);
        }

        public static void DequeueRange<T>(this Queue<T> queue, int n)
        {
            for (var i = 0; i < n; i++)
                queue.Dequeue();
        }

        public static T[] Clone<T>(this T[] array, int length)
        {
            var clone = new T[length];
            Array.Copy(array, clone, length);
            return clone;
        }

        public static GenericParameter GetGenericParameter(this IGenericParameterProvider provider,
            GenericParameter orig)
        {
            // Don't ask me, that's possible for T[,].Get in "Enter the Gungeon"...?!
            if (provider is GenericParameter && ((GenericParameter) provider).Name == orig.Name)
                return (GenericParameter) provider;

            foreach (var param in provider.GenericParameters)
                if (param.Name == orig.Name)
                    return param;

            var index = orig.Position;
            if (provider is MethodReference && orig.DeclaringMethod != null)
            {
                if (index < provider.GenericParameters.Count)
                    return provider.GenericParameters[index];
                return new GenericParameter(orig.Name, provider).Update(index, GenericParameterType.Method);
            }

            if (provider is TypeReference && orig.DeclaringType != null)
                if (index < provider.GenericParameters.Count)
                    return provider.GenericParameters[index];
                else
                    return new GenericParameter(orig.Name, provider).Update(index, GenericParameterType.Type);

            return
                (provider as TypeSpecification)?.ElementType.GetGenericParameter(orig) ??
                (provider as MemberReference)?.DeclaringType?.GetGenericParameter(orig);
        }

        public static IMetadataTokenProvider Relink(this IMetadataTokenProvider mtp, Relinker relinker,
            IGenericParameterProvider context)
        {
            if (mtp is TypeReference) return ((TypeReference) mtp).Relink(relinker, context);
            if (mtp is MethodReference) return ((MethodReference) mtp).Relink(relinker, context);
            if (mtp is FieldReference) return ((FieldReference) mtp).Relink(relinker, context);
            if (mtp is ParameterDefinition) return ((ParameterDefinition) mtp).Relink(relinker, context);
            if (mtp is CallSite) return ((CallSite) mtp).Relink(relinker, context);
            throw new InvalidOperationException(
                $"MonoMod can't handle metadata token providers of the type {mtp.GetType()}");
        }

        public static TypeReference Relink(this TypeReference type, Relinker relinker,
            IGenericParameterProvider context)
        {
            if (type == null)
                return null;

            if (type is TypeSpecification ts)
            {
                var relinkedElem = ts.ElementType.Relink(relinker, context);

                if (type.IsSentinel)
                    return new SentinelType(relinkedElem);

                if (type.IsByReference)
                    return new ByReferenceType(relinkedElem);

                if (type.IsPointer)
                    return new PointerType(relinkedElem);

                if (type.IsPinned)
                    return new PinnedType(relinkedElem);

                if (type.IsArray)
                {
                    var at = new ArrayType(relinkedElem, ((ArrayType) type).Rank);
                    for (var i = 0; i < at.Rank; i++)
                        // It's a struct.
                        at.Dimensions[i] = ((ArrayType) type).Dimensions[i];
                    return at;
                }

                if (type.IsRequiredModifier)
                    return new RequiredModifierType(
                        ((RequiredModifierType) type).ModifierType.Relink(relinker, context), relinkedElem);

                if (type.IsOptionalModifier)
                    return new OptionalModifierType(
                        ((OptionalModifierType) type).ModifierType.Relink(relinker, context), relinkedElem);

                if (type.IsGenericInstance)
                {
                    var git = new GenericInstanceType(relinkedElem);
                    foreach (var genArg in ((GenericInstanceType) type).GenericArguments)
                        git.GenericArguments.Add(genArg?.Relink(relinker, context));
                    return git;
                }

                if (type.IsFunctionPointer)
                {
                    var fp = (FunctionPointerType) type;
                    fp.ReturnType = fp.ReturnType.Relink(relinker, context);
                    for (var i = 0; i < fp.Parameters.Count; i++)
                        fp.Parameters[i].ParameterType = fp.Parameters[i].ParameterType.Relink(relinker, context);
                    return fp;
                }

                throw new NotSupportedException(
                    $"MonoMod can't handle TypeSpecification: {type.FullName} ({type.GetType()})");
            }

            if (type.IsGenericParameter)
            {
                var genParam = context.GetGenericParameter((GenericParameter) type);
                if (genParam == null)
                    throw new RelinkTargetNotFoundException(
                        $"{RelinkTargetNotFoundException.DefaultMessage} {type.FullName} (context: {context})", type,
                        context);
                for (var i = 0; i < genParam.Constraints.Count; i++)
                    if (!genParam.Constraints[i].IsGenericInstance
                    ) // That is somehow possible and causes a stack overflow.
                        genParam.Constraints[i] = genParam.Constraints[i].Relink(relinker, context);
                return genParam;
            }

            return (TypeReference) relinker(type, context);
        }

        public static MethodReference Relink(this MethodReference method, Relinker relinker,
            IGenericParameterProvider context)
        {
            if (method.IsGenericInstance)
            {
                var methodg = (GenericInstanceMethod) method;
                var gim = new GenericInstanceMethod(methodg.ElementMethod.Relink(relinker, context));
                foreach (var arg in methodg.GenericArguments)
                    // Generic arguments for the generic instance are often given by the next higher provider.
                    gim.GenericArguments.Add(arg.Relink(relinker, context));

                return (MethodReference) relinker(gim, context);
            }

            var relink = new MethodReference(method.Name, method.ReturnType,
                method.DeclaringType.Relink(relinker, context));

            relink.CallingConvention = method.CallingConvention;
            relink.ExplicitThis = method.ExplicitThis;
            relink.HasThis = method.HasThis;

            foreach (var param in method.GenericParameters)
            {
                var paramN = new GenericParameter(param.Name, param.Owner)
                {
                    Attributes = param.Attributes
                    // MetadataToken = param.MetadataToken
                }.Update(param);

                relink.GenericParameters.Add(paramN);

                foreach (var constraint in param.Constraints)
                    paramN.Constraints.Add(constraint.Relink(relinker, relink));
            }

            relink.ReturnType = relink.ReturnType?.Relink(relinker, relink);

            foreach (var param in method.Parameters)
            {
                param.ParameterType = param.ParameterType.Relink(relinker, method);
                relink.Parameters.Add(param);
            }

            return (MethodReference) relinker(relink, context);
        }

        public static CallSite Relink(this CallSite method, Relinker relinker, IGenericParameterProvider context)
        {
            var relink = new CallSite(method.ReturnType);

            relink.CallingConvention = method.CallingConvention;
            relink.ExplicitThis = method.ExplicitThis;
            relink.HasThis = method.HasThis;

            relink.ReturnType = relink.ReturnType?.Relink(relinker, context);

            foreach (var param in method.Parameters)
            {
                param.ParameterType = param.ParameterType.Relink(relinker, context);
                relink.Parameters.Add(param);
            }

            return (CallSite) relinker(relink, context);
        }

        public static IMetadataTokenProvider Relink(this FieldReference field, Relinker relinker,
            IGenericParameterProvider context)
        {
            var declaringType = field.DeclaringType.Relink(relinker, context);
            return relinker(
                new FieldReference(field.Name, field.FieldType.Relink(relinker, declaringType), declaringType),
                context);
        }

        public static ParameterDefinition Relink(this ParameterDefinition param, Relinker relinker,
            IGenericParameterProvider context)
        {
            param = (param.Method as MethodReference)?.Parameters[param.Index] ?? param;
            var newParam =
                new ParameterDefinition(param.Name, param.Attributes, param.ParameterType.Relink(relinker, context))
                {
                    IsIn = param.IsIn,
                    IsLcid = param.IsLcid,
                    IsOptional = param.IsOptional,
                    IsOut = param.IsOut,
                    IsReturnValue = param.IsReturnValue,
                    MarshalInfo = param.MarshalInfo
                };
            if (param.HasConstant)
                newParam.Constant = param.Constant;
            return newParam;
        }

        public static ParameterDefinition Clone(this ParameterDefinition param)
        {
            var newParam = new ParameterDefinition(param.Name, param.Attributes, param.ParameterType)
            {
                IsIn = param.IsIn,
                IsLcid = param.IsLcid,
                IsOptional = param.IsOptional,
                IsOut = param.IsOut,
                IsReturnValue = param.IsReturnValue,
                MarshalInfo = param.MarshalInfo
            };
            if (param.HasConstant)
                newParam.Constant = param.Constant;
            foreach (var attrib in param.CustomAttributes)
                newParam.CustomAttributes.Add(attrib.Clone());
            return newParam;
        }

        public static CustomAttribute Relink(this CustomAttribute attrib, Relinker relinker,
            IGenericParameterProvider context)
        {
            var newAttrib = new CustomAttribute(attrib.Constructor.Relink(relinker, context));
            foreach (var attribArg in attrib.ConstructorArguments)
                newAttrib.ConstructorArguments.Add(new CustomAttributeArgument(attribArg.Type.Relink(relinker, context),
                    attribArg.Value));
            foreach (var attribArg in attrib.Fields)
                newAttrib.Fields.Add(new CustomAttributeNamedArgument(attribArg.Name,
                    new CustomAttributeArgument(attribArg.Argument.Type.Relink(relinker, context),
                        attribArg.Argument.Value))
                );
            foreach (var attribArg in attrib.Properties)
                newAttrib.Properties.Add(new CustomAttributeNamedArgument(attribArg.Name,
                    new CustomAttributeArgument(attribArg.Argument.Type.Relink(relinker, context),
                        attribArg.Argument.Value))
                );
            return newAttrib;
        }

        public static CustomAttribute Clone(this CustomAttribute attrib, IGenericParameterProvider owner = null, Relinker relinker = null)
        {
            var newAttrib = new CustomAttribute(relinker != null ? attrib.Constructor.Relink(relinker, owner) : attrib.Constructor);
            foreach (var attribArg in attrib.ConstructorArguments)
                newAttrib.ConstructorArguments.Add(new CustomAttributeArgument(relinker != null ? attribArg.Type.Relink(relinker, owner) : attribArg.Type, attribArg.Value));
            foreach (var attribArg in attrib.Fields)
                newAttrib.Fields.Add(new CustomAttributeNamedArgument(attribArg.Name,
                    new CustomAttributeArgument(relinker != null ? attribArg.Argument.Type.Relink(relinker, owner) : attribArg.Argument.Type, attribArg.Argument.Value))
                );
            foreach (var attribArg in attrib.Properties)
                newAttrib.Properties.Add(new CustomAttributeNamedArgument(attribArg.Name,
                    new CustomAttributeArgument(relinker != null ? attribArg.Argument.Type.Relink(relinker, owner) : attribArg.Argument.Type, attribArg.Argument.Value))
                );
            return newAttrib;
        }

        public static GenericParameter Relink(this GenericParameter param, Relinker relinker,
            IGenericParameterProvider context)
        {
            var newParam = new GenericParameter(param.Name, param.Owner)
            {
                Attributes = param.Attributes
            }.Update(param);
            foreach (var constraint in param.Constraints)
                newParam.Constraints.Add(constraint.Relink(relinker, context));
            return newParam;
        }

        public static GenericParameter Clone(this GenericParameter param, IGenericParameterProvider owner = null)
        {
            var newParam = new GenericParameter(param.Name, owner ?? param.Owner)
            {
                Attributes = param.Attributes
            }.Update(param);
            foreach (var constraint in param.Constraints)
                newParam.Constraints.Add(constraint);
            return newParam;
        }

        public static MethodInfo FindMethod(this Type type, string findableID, bool simple = true)
        {
            var methods = type.GetMethods(
                BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic
            );
            // First pass: With type name (f.e. global searches)
            foreach (var method in methods)
                if (method.GetFindableID() == findableID)
                    return method;
            // Second pass: Without type name (f.e. LinkTo)
            foreach (var method in methods)
                if (method.GetFindableID(withType: false) == findableID)
                    return method;

            if (!simple)
                return null;

            // Those shouldn't be reached, unless you're defining a relink map dynamically, which may conflict with itself.
            // First simple pass: With type name (just "Namespace.Type::MethodName")
            foreach (var method in methods)
                if (method.GetFindableID(simple: true) == findableID)
                    return method;
            // Second simple pass: Without type name (basically name only)
            foreach (var method in methods)
                if (method.GetFindableID(withType: false, simple: true) == findableID)
                    return method;

            return null;
        }

        public static PropertyDefinition FindProperty(this TypeDefinition type, string name)
        {
            foreach (var prop in type.Properties)
                if (prop.Name == name)
                    return prop;
            return null;
        }

        public static PropertyDefinition FindPropertyDeep(this TypeDefinition type, string name)
        {
            return type.FindProperty(name) ?? type.BaseType?.Resolve()?.FindPropertyDeep(name);
        }

        public static FieldDefinition FindField(this TypeDefinition type, string name)
        {
            foreach (var field in type.Fields)
                if (field.Name == name)
                    return field;
            return null;
        }

        public static FieldDefinition FindFieldDeep(this TypeDefinition type, string name)
        {
            return type.FindField(name) ?? type.BaseType?.Resolve()?.FindFieldDeep(name);
        }

        public static EventDefinition FindEvent(this TypeDefinition type, string name)
        {
            foreach (var eventDef in type.Events)
                if (eventDef.Name == name)
                    return eventDef;
            return null;
        }

        public static EventDefinition FindEventDeep(this TypeDefinition type, string name)
        {
            return type.FindEvent(name) ?? type.BaseType?.Resolve()?.FindEventDeep(name);
        }

        public static bool HasProperty(this TypeDefinition type, PropertyDefinition prop)
        {
            return type.FindProperty(prop.Name) != null;
        }

        public static bool HasField(this TypeDefinition type, FieldDefinition field)
        {
            return type.FindField(field.Name) != null;
        }

        public static bool HasEvent(this TypeDefinition type, EventDefinition eventDef)
        {
            return type.FindEvent(eventDef.Name) != null;
        }

        public static void SetPublic(this IMetadataTokenProvider mtp, bool p)
        {
            if (mtp is TypeDefinition) ((TypeDefinition) mtp).SetPublic(p);
            else if (mtp is FieldDefinition) ((FieldDefinition) mtp).SetPublic(p);
            else if (mtp is MethodDefinition) ((MethodDefinition) mtp).SetPublic(p);
            else if (mtp is PropertyDefinition) ((PropertyDefinition) mtp).SetPublic(p);
            else if (mtp is EventDefinition) ((EventDefinition) mtp).SetPublic(p);
            else
                throw new InvalidOperationException(
                    $"MonoMod can't set metadata token providers of the type {mtp.GetType()} public.");
        }

        public static void SetPublic(this FieldDefinition o, bool p)
        {
            if (!o.IsDefinition || o.DeclaringType.Name == "<PrivateImplementationDetails>")
                return;
            o.IsPrivate = !p;
            o.IsPublic = p;
            if (p) o.DeclaringType.SetPublic(true);
        }

        public static void SetPublic(this MethodDefinition o, bool p)
        {
            if (!o.IsDefinition || o.DeclaringType.Name == "<PrivateImplementationDetails>")
                return;
            o.IsPrivate = !p;
            o.IsPublic = p;
            if (p) o.DeclaringType.SetPublic(true);
        }

        public static void SetPublic(this PropertyDefinition o, bool p)
        {
            if (!o.IsDefinition || o.DeclaringType.Name == "<PrivateImplementationDetails>")
                return;
            o.GetMethod?.SetPublic(p);
            o.SetMethod?.SetPublic(p);
            foreach (var method in o.OtherMethods)
                method.SetPublic(p);
            if (p) o.DeclaringType.SetPublic(true);
        }

        public static void SetPublic(this EventDefinition o, bool p)
        {
            if (!o.IsDefinition || o.DeclaringType.Name == "<PrivateImplementationDetails>")
                return;
            o.AddMethod?.SetPublic(p);
            o.RemoveMethod?.SetPublic(p);
            o.InvokeMethod?.SetPublic(p);
            foreach (var method in o.OtherMethods)
                method.SetPublic(p);
            if (p) o.DeclaringType.SetPublic(true);
        }

        public static void SetPublic(this TypeDefinition o, bool p)
        {
            if (
                !o.IsDefinition ||
                o.Name == "<PrivateImplementationDetails>" ||
                o.DeclaringType != null && o.DeclaringType.Name == "<PrivateImplementationDetails>"
            )
                return;
            if (o.DeclaringType == null)
            {
                o.IsNotPublic = !p;
                o.IsPublic = p;
            }
            else
            {
                o.IsNestedPrivate = !p;
                o.IsNestedPublic = p;
                if (p) SetPublic(o.DeclaringType, true);
            }
        }

        public static IMetadataTokenProvider ImportReference(this ModuleDefinition mod, IMetadataTokenProvider mtp)
        {
            if (mtp is TypeReference) return mod.ImportReference((TypeReference) mtp);
            if (mtp is FieldReference) return mod.ImportReference((FieldReference) mtp);
            if (mtp is MethodReference) return mod.ImportReference((MethodReference) mtp);
            return mtp;
        }

#if CECIL0_9
        public static TypeReference ImportReference(this ModuleDefinition mod, TypeReference type)
            => mod.Import(type);
        public static TypeReference ImportReference(this ModuleDefinition mod, Type type, IGenericParameterProvider context)
            => mod.Import(type, context);
        public static FieldReference ImportReference(this ModuleDefinition mod, System.Reflection.FieldInfo field)
            => mod.Import(field);
        public static FieldReference ImportReference(this ModuleDefinition mod, System.Reflection.FieldInfo field, IGenericParameterProvider context)
            => mod.Import(field, context);
        public static MethodReference ImportReference(this ModuleDefinition mod, System.Reflection.MethodBase method)
            => mod.Import(method);
        public static MethodReference ImportReference(this ModuleDefinition mod, System.Reflection.MethodBase method, IGenericParameterProvider context)
            => mod.Import(method, context);
        public static TypeReference ImportReference(this ModuleDefinition mod, TypeReference type, IGenericParameterProvider context)
            => mod.Import(type, context);
        public static TypeReference ImportReference(this ModuleDefinition mod, Type type)
            => mod.Import(type);
        public static FieldReference ImportReference(this ModuleDefinition mod, FieldReference field)
            => mod.Import(field);
        public static MethodReference ImportReference(this ModuleDefinition mod, MethodReference method)
            => mod.Import(method);
        public static MethodReference ImportReference(this ModuleDefinition mod, MethodReference method, IGenericParameterProvider context)
            => mod.Import(method, context);
        public static FieldReference ImportReference(this ModuleDefinition mod, FieldReference field, IGenericParameterProvider context)
            => mod.Import(field, context);
#endif

        public static TypeDefinition SafeResolve(this TypeReference r)
        {
            try
            {
                return r.Resolve();
            }
            catch
            {
                return null;
            }
        }

        public static FieldDefinition SafeResolve(this FieldReference r)
        {
            try
            {
                return r.Resolve();
            }
            catch
            {
                return null;
            }
        }

        public static MethodDefinition SafeResolve(this MethodReference r)
        {
            try
            {
                return r.Resolve();
            }
            catch
            {
                return null;
            }
        }

        public static PropertyDefinition SafeResolve(this PropertyReference r)
        {
            try
            {
                return r.Resolve();
            }
            catch
            {
                return null;
            }
        }

        public static bool IsCallvirt(this MethodReference method)
        {
            if (!method.HasThis)
                return false;
            if (method.DeclaringType.IsValueType)
                return false;
            return true;
        }

        public static bool IsStruct(this TypeReference type)
        {
            if (!type.IsValueType)
                return false;
            if (type.IsPrimitive)
                return false;
            return true;
        }

        public static void RecalculateILOffsets(this MethodDefinition method)
        {
            if (!method.HasBody)
                return;
            // Calculate updated offsets required for any further manual fixes.
            var offs = 0;
            for (var i = 0; i < method.Body.Instructions.Count; i++)
            {
                var instr = method.Body.Instructions[i];
                instr.Offset = offs;
                offs += instr.GetSize();
            }
        }

        // Required for field -> call conversions where the original access was an address access.
        public static void AppendGetAddr(this MethodBody body, Instruction instr, TypeReference type,
            IDictionary<TypeReference, VariableDefinition> localMap = null)
        {
            if (localMap == null || !localMap.TryGetValue(type, out var local))
            {
                local = new VariableDefinition(type);
                body.Variables.Add(local);
                if (localMap != null)
                    localMap[type] = local;
            }

            var il = body.GetILProcessor();
            var tmp = instr;
            // TODO: Fast stloc / ldloca!
            il.InsertAfter(tmp, tmp = il.Create(OpCodes.Stloc, local));
            il.InsertAfter(tmp, tmp = il.Create(OpCodes.Ldloca, local));
        }

        public static void PrependGetValue(this MethodBody body, Instruction instr, TypeReference type)
        {
            var il = body.GetILProcessor();
            il.InsertBefore(instr, il.Create(OpCodes.Ldobj, type));
        }

        public static void ConvertShortLongOps(this MethodDefinition method)
        {
            if (!method.HasBody)
                return;

            // Convert short to long ops.
            for (var i = 0; i < method.Body.Instructions.Count; i++)
            {
                var instr = method.Body.Instructions[i];
                if (instr.Operand is Instruction) instr.OpCode = instr.OpCode.ShortToLongOp();
            }

            method.RecalculateILOffsets();

            // Optimize long to short ops.
            bool optimized;
            do
            {
                optimized = false;
                for (var i = 0; i < method.Body.Instructions.Count; i++)
                {
                    var instr = method.Body.Instructions[i];
                    // Change short <-> long operations as the method grows / shrinks.
                    if (instr.Operand is Instruction target)
                    {
                        // Thanks to Chicken Bones for helping out with this!
                        var distance = target.Offset - (instr.Offset + instr.GetSize());
                        if (distance == (sbyte) distance)
                        {
                            var prev = instr.OpCode;
                            instr.OpCode = instr.OpCode.LongToShortOp();
                            optimized = prev != instr.OpCode;
                        }
                    }
                }
            } while (optimized);
        }

        private static readonly Type t_Code = typeof(Code);
        private static readonly Type t_OpCodes = typeof(OpCodes);

        private static readonly Dictionary<int, OpCode> _ShortToLongOp = new Dictionary<int, OpCode>();

        public static OpCode ShortToLongOp(this OpCode op)
        {
            var name = Enum.GetName(t_Code, op.Code);
            if (!name.EndsWith("_S"))
                return op;
            lock (_ShortToLongOp)
            {
                if (_ShortToLongOp.TryGetValue((int) op.Code, out var found))
                    return found;
                return _ShortToLongOp[(int) op.Code] =
                    (OpCode?) t_OpCodes.GetField(name.Substring(0, name.Length - 2))?.GetValue(null) ?? op;
            }
        }

        private static readonly Dictionary<int, OpCode> _LongToShortOp = new Dictionary<int, OpCode>();

        public static OpCode LongToShortOp(this OpCode op)
        {
            var name = Enum.GetName(t_Code, op.Code);
            if (name.EndsWith("_S"))
                return op;
            lock (_LongToShortOp)
            {
                if (_LongToShortOp.TryGetValue((int) op.Code, out var found))
                    return found;
                return _LongToShortOp[(int) op.Code] = (OpCode?) t_OpCodes.GetField(name + "_S")?.GetValue(null) ?? op;
            }
        }

        // IsMatchingSignature and related methods taken and adapted from the Mono.Linker:
        // https://github.com/mono/linker/blob/e4dfcf006b0705aba6b204aab2d603b781c5fc44/linker/Mono.Linker.Steps/TypeMapStep.cs

        public static bool IsMatchingSignature(this MethodDefinition method, MethodReference candidate)
        {
            if (method.Parameters.Count != candidate.Parameters.Count)
                return false;

            if (method.Name != candidate.Name)
                return false;

            if (!method.ReturnType._InflateGenericType(method).IsMatchingSignature(
                candidate.ReturnType._InflateGenericType(candidate)
            ))
                return false;

            if (method.GenericParameters.Count != candidate.GenericParameters.Count)
                return false;

            if (method.HasParameters)
                for (var i = 0; i < method.Parameters.Count; i++)
                    if (!method.Parameters[i].ParameterType._InflateGenericType(method).IsMatchingSignature(
                        candidate.Parameters[i].ParameterType._InflateGenericType(candidate)
                    ))
                        return false;

            if (!candidate.SafeResolve()?.IsVirtual ?? false)
                return false;

            return true;
        }

        public static bool IsMatchingSignature(this IModifierType a, IModifierType b)
        {
            if (!a.ModifierType.IsMatchingSignature(b.ModifierType))
                return false;

            return a.ElementType.IsMatchingSignature(b.ElementType);
        }

        public static bool IsMatchingSignature(this TypeSpecification a, TypeSpecification b)
        {
            if (a.GetType() != b.GetType())
                return false;

            if (a is GenericInstanceType gita)
                return gita.IsMatchingSignature((GenericInstanceType) b);

            if (a is IModifierType mta)
                return mta.IsMatchingSignature((IModifierType) b);

            return IsMatchingSignature(a.ElementType, b.ElementType);
        }

        public static bool IsMatchingSignature(this GenericInstanceType a, GenericInstanceType b)
        {
            if (!a.ElementType.IsMatchingSignature(b.ElementType))
                return false;

            if (a.HasGenericArguments != b.HasGenericArguments)
                return false;

            if (!a.HasGenericArguments)
                return true;

            if (a.GenericArguments.Count != b.GenericArguments.Count)
                return false;

            for (var i = 0; i < a.GenericArguments.Count; i++)
                if (!a.GenericArguments[i].IsMatchingSignature(b.GenericArguments[i]))
                    return false;

            return true;
        }

        public static bool IsMatchingSignature(this GenericParameter a, GenericParameter b)
        {
            if (a.Position != b.Position)
                return false;

            if (a.Type != b.Type)
                return false;

            return true;
        }

        public static bool IsMatchingSignature(this TypeReference a, TypeReference b)
        {
            if (a is TypeSpecification || b is TypeSpecification)
                return
                    a is TypeSpecification && b is TypeSpecification &&
                    ((TypeSpecification) a).IsMatchingSignature((TypeSpecification) b);

            if (a is GenericParameter && b is GenericParameter)
                return ((GenericParameter) a).IsMatchingSignature((GenericParameter) b);

            return a.FullName == b.FullName;
        }

        private static TypeReference _InflateGenericType(this TypeReference type, MethodReference method)
        {
            if (!(method.DeclaringType is GenericInstanceType))
                return type;
            return _InflateGenericType(method.DeclaringType as GenericInstanceType, type);
        }

        private static TypeReference _InflateGenericType(GenericInstanceType genericInstanceProvider,
            TypeReference typeToInflate)
        {
            if (typeToInflate is ArrayType arrayType)
            {
                var inflatedElementType = _InflateGenericType(genericInstanceProvider, arrayType.ElementType);

                if (inflatedElementType != arrayType.ElementType)
                {
                    var at = new ArrayType(inflatedElementType, arrayType.Rank);
                    for (var i = 0; i < arrayType.Rank; i++)
                        // It's a struct.
                        at.Dimensions[i] = arrayType.Dimensions[i];
                    return at;
                }

                return arrayType;
            }

            if (typeToInflate is GenericInstanceType genericInst)
            {
                var result = new GenericInstanceType(genericInst.ElementType);

                for (var i = 0; i < genericInst.GenericArguments.Count; ++i)
                    result.GenericArguments.Add(_InflateGenericType(genericInstanceProvider,
                        genericInst.GenericArguments[i]));

                return result;
            }

            if (typeToInflate is GenericParameter genericParameter)
            {
                if (genericParameter.Owner is MethodReference)
                    return genericParameter;

                var elementType = genericInstanceProvider.ElementType.Resolve();
                var parameter = elementType.GetGenericParameter(genericParameter);
                return genericInstanceProvider.GenericArguments[parameter.Position];
            }

            if (typeToInflate is FunctionPointerType functionPointerType)
            {
                var result = new FunctionPointerType();
                result.ReturnType = _InflateGenericType(genericInstanceProvider, functionPointerType.ReturnType);

                for (var i = 0; i < functionPointerType.Parameters.Count; i++)
                    result.Parameters.Add(new ParameterDefinition(_InflateGenericType(genericInstanceProvider,
                        functionPointerType.Parameters[i].ParameterType)));

                return result;
            }

            if (typeToInflate is IModifierType modifierType)
            {
                var modifier = _InflateGenericType(genericInstanceProvider, modifierType.ModifierType);
                var elementType = _InflateGenericType(genericInstanceProvider, modifierType.ElementType);

                if (modifierType is OptionalModifierType)
                    return new OptionalModifierType(modifier, elementType);

                return new RequiredModifierType(modifier, elementType);
            }

            if (typeToInflate is PinnedType pinnedType)
            {
                var elementType = _InflateGenericType(genericInstanceProvider, pinnedType.ElementType);

                if (elementType != pinnedType.ElementType)
                    return new PinnedType(elementType);

                return pinnedType;
            }

            if (typeToInflate is PointerType pointerType)
            {
                var elementType = _InflateGenericType(genericInstanceProvider, pointerType.ElementType);

                if (elementType != pointerType.ElementType)
                    return new PointerType(elementType);

                return pointerType;
            }

            if (typeToInflate is ByReferenceType byReferenceType)
            {
                var elementType = _InflateGenericType(genericInstanceProvider, byReferenceType.ElementType);

                if (elementType != byReferenceType.ElementType)
                    return new ByReferenceType(elementType);

                return byReferenceType;
            }

            if (typeToInflate is SentinelType sentinelType)
            {
                var elementType = _InflateGenericType(genericInstanceProvider, sentinelType.ElementType);

                if (elementType != sentinelType.ElementType)
                    return new SentinelType(elementType);

                return sentinelType;
            }

            return typeToInflate;
        }
    }
}