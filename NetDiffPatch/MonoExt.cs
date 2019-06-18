#region LICENSE
/*
   The MIT License (MIT)
   
   Copyright (c) 2015 Maik Macho
   
   Permission is hereby granted, free of charge, to any person obtaining a copy
   of this software and associated documentation files (the "Software"), to deal
   in the Software without restriction, including without limitation the rights
   to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
   copies of the Software, and to permit persons to whom the Software is
   furnished to do so, subject to the following conditions:
   
   The above copyright notice and this permission notice shall be included in all
   copies or substantial portions of the Software.
   
   THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
   IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
   FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
   AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
   LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
   OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
   SOFTWARE.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;
using CustomAttributeNamedArgument = Mono.Cecil.CustomAttributeNamedArgument;
using MethodBody = Mono.Cecil.Cil.MethodBody;

// ReSharper disable once CheckNamespace
namespace MonoMod.Utils
{
    public class RelinkFailedException : Exception
    {
        public IMetadataTokenProvider Context;

        public IMetadataTokenProvider Mtp;

        public RelinkFailedException(string message,
            IMetadataTokenProvider mtp, IMetadataTokenProvider context = null)
            : base(message)
        {
            Mtp = mtp;
            Context = context;
        }
    }

    public class RelinkTargetNotFoundException : RelinkFailedException
    {
        public const string DefaultMessage = "MonoMod relinker failed finding";

        public RelinkTargetNotFoundException(string message,
            IMetadataTokenProvider mtp, IMetadataTokenProvider context = null)
            : base(message ?? DefaultMessage, mtp, context)
        {
        }
    }

    public delegate IMetadataTokenProvider Relinker(IMetadataTokenProvider mtp, IGenericParameterProvider context);

    /// <summary>
    ///     Huge collection of MonoMod-related Mono.Cecil extensions.
    /// </summary>
    public static class MonoModExt
    {
        public static readonly FieldInfo GenericParameterPosition =
            typeof(GenericParameter).GetField("position",
                BindingFlags.Instance | BindingFlags.NonPublic);

        public static readonly FieldInfo GenericParameterType =
            typeof(GenericParameter).GetField("type",
                BindingFlags.Instance | BindingFlags.NonPublic);

        public static MethodBody Clone(this MethodBody bo, MethodDefinition m, Relinker relinker = null)
        {
            if (bo == null)
                return null;

            var bc = new MethodBody(m)
            {
                MaxStackSize = bo.MaxStackSize, InitLocals = bo.InitLocals, LocalVarToken = bo.LocalVarToken
            };

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
                switch (c.Operand)
                {
                    case Instruction target:
                        c.Operand = bc.Instructions[bo.Instructions.IndexOf(target)];
                        break;
                    case Instruction[] targets:
                        c.Operand = targets.Select(i => bc.Instructions[bo.Instructions.IndexOf(i)]).ToArray();
                        break;
                }

            bc.ExceptionHandlers.AddRange(bo.ExceptionHandlers.Select(o =>
            {
                var c = new ExceptionHandler(o.HandlerType)
                {
                    TryStart = o.TryStart == null ? null : bc.Instructions[bo.Instructions.IndexOf(o.TryStart)],
                    TryEnd = o.TryEnd == null ? null : bc.Instructions[bo.Instructions.IndexOf(o.TryEnd)],
                    FilterStart =
                        o.FilterStart == null ? null : bc.Instructions[bo.Instructions.IndexOf(o.FilterStart)],
                    HandlerStart = o.HandlerStart == null
                        ? null
                        : bc.Instructions[bo.Instructions.IndexOf(o.HandlerStart)],
                    HandlerEnd = o.HandlerEnd == null ? null : bc.Instructions[bo.Instructions.IndexOf(o.HandlerEnd)],
                    CatchType = relinker != null ? o.CatchType.Relink(relinker, m) : o.CatchType
                };
                return c;
            }));

            bc.Variables.AddRange(bo.Variables.Select(o =>
            {
                var c = new VariableDefinition(relinker != null ? o.VariableType.Relink(relinker, m) : o.VariableType);
                return c;
            }));

            m.CustomDebugInformations.AddRange(bo.Method
                .CustomDebugInformations); // Abstract. TODO: Implement deep CustomDebugInformations copy.
            m.DebugInformation.SequencePoints.AddRange(bo.Method.DebugInformation.SequencePoints.Select(o =>
            {
                var c = new SequencePoint(bc.Instructions.FirstOrDefault(i => i.Offset == o.Offset),
                    o.Document)
                {
                    StartLine = o.StartLine, StartColumn = o.StartColumn, EndLine = o.EndLine, EndColumn = o.EndColumn
                };
                return c;
            }));

            return bc;
        }

        public static GenericParameter Update(this GenericParameter param, GenericParameter other)
        {
            return param.Update(other.Position, other.Type);
        }

        public static GenericParameter Update(this GenericParameter param, int position, GenericParameterType type)
        {
            GenericParameterPosition.SetValue(param, position);
            GenericParameterType.SetValue(param, type);
            return param;
        }

        public static void AddRange<T>(this Collection<T> list, IEnumerable<T> other)
        {
            foreach (var entry in other)
                list.Add(entry);
        }

        public static GenericParameter GetGenericParameter(this IGenericParameterProvider provider,
            GenericParameter orig)
        {
            // Don't ask me, that's possible for T[,].Get in "Enter the Gungeon"...?!
            if (provider is GenericParameter parameter && parameter.Name == orig.Name)
                return parameter;

            foreach (var param in provider.GenericParameters)
                if (param.Name == orig.Name)
                    return param;

            var index = orig.Position;
            switch (provider)
            {
                case MethodReference _ when orig.DeclaringMethod != null:
                {
                    return index < provider.GenericParameters.Count
                        ? provider.GenericParameters[index]
                        : new GenericParameter(orig.Name, provider).Update(index,
                            Mono.Cecil.GenericParameterType.Method);
                }

                case TypeReference _ when orig.DeclaringType != null:
                {
                    return index < provider.GenericParameters.Count
                        ? provider.GenericParameters[index]
                        : new GenericParameter(orig.Name, provider).Update(index, Mono.Cecil.GenericParameterType.Type);
                }

                default:
                    return
                        (provider as TypeSpecification)?.ElementType.GetGenericParameter(orig) ??
                        (provider as MemberReference)?.DeclaringType?.GetGenericParameter(orig);
            }
        }

        public static IMetadataTokenProvider Relink(this IMetadataTokenProvider mtp, Relinker relinker,
            IGenericParameterProvider context)
        {
            switch (mtp)
            {
                case TypeReference reference:
                    return reference.Relink(relinker, context);
                case MethodReference reference:
                    return reference.Relink(relinker, context);
                case FieldReference reference:
                    return reference.Relink(relinker, context);
                case ParameterDefinition definition:
                    return definition.Relink(relinker, context);
                case CallSite site:
                    return site.Relink(relinker, context);
                default:
                    throw new InvalidOperationException(
                        $"MonoMod can't handle metadata token providers of the type {mtp.GetType()}");
            }
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

                if (!type.IsFunctionPointer)
                    throw new NotSupportedException(
                        $"MonoMod can't handle TypeSpecification: {type.FullName} ({type.GetType()})");
                var fp = (FunctionPointerType) type;
                fp.ReturnType = fp.ReturnType.Relink(relinker, context);
                foreach (var t in fp.Parameters)
                    t.ParameterType = t.ParameterType.Relink(relinker, context);

                return fp;
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
                method.DeclaringType.Relink(relinker, context))
            {
                CallingConvention = method.CallingConvention,
                ExplicitThis = method.ExplicitThis,
                HasThis = method.HasThis
            };


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
            var relink = new CallSite(method.ReturnType)
            {
                CallingConvention = method.CallingConvention,
                ExplicitThis = method.ExplicitThis,
                HasThis = method.HasThis
            };


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

        public static IMetadataTokenProvider ImportReference(this ModuleDefinition mod, IMetadataTokenProvider mtp)
        {
            switch (mtp)
            {
                case TypeReference reference:
                    return mod.ImportReference(reference);
                case FieldReference reference:
                    return mod.ImportReference(reference);
                case MethodReference reference:
                    return mod.ImportReference(reference);
                default:
                    return mtp;
            }
        }
    }
}