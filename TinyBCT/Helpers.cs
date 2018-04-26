﻿using Backend;
using Backend.Analyses;
using Backend.Model;
using Backend.ThreeAddressCode.Instructions;
using Backend.ThreeAddressCode.Values;
using Microsoft.Cci;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TinyBCT
{
    static class Helpers
    {
        public static bool IsInstructionImplemented(Instruction inst)
        {
            if (inst is MethodCallInstruction ||
                inst is LoadInstruction ||
                inst is UnconditionalBranchInstruction ||
                inst is BinaryInstruction ||
                inst is NopInstruction ||
                inst is ReturnInstruction ||
                inst is ConditionalBranchInstruction ||
                inst is CreateObjectInstruction ||
                inst is StoreInstruction ||
                inst is FinallyInstruction ||
                inst is TryInstruction || 
                inst is ConvertInstruction)
                return true;

            return false;
        }
        public static String GetBoogieType(ITypeReference type)
        {
            if (type.TypeCode.Equals(PrimitiveTypeCode.Int32))
                return "int";

            if (type.TypeCode.Equals(PrimitiveTypeCode.Boolean))
                return "bool";

            if (type.TypeCode.Equals(PrimitiveTypeCode.String))
                return "Ref";

            // hack 
            if (type.TypeCode.Equals(PrimitiveTypeCode.NotPrimitive) || type.TypeCode.Equals(PrimitiveTypeCode.Reference))
                return "Ref";

            return null;
        }

        public static String GetMethodName(IMethodReference methodDefinition)
        {
            var signature = MemberHelper.GetMethodSignature(methodDefinition);
            signature = signature.Replace("..", ".#"); // for ctor its name is ..ctor it changes to .#ctor
            var arity = Helpers.GetArityWithNonBoogieTypes(methodDefinition);
            arity = arity.Replace("[]", "array");
            var result = signature + arity;
            result = result.Replace('<', '$').Replace('>', '$').Replace(", ", "$"); // for example containing type for delegates
            return result;
        }

        public static String GetMethodBoogieReturnType(IMethodReference methodDefinition)
        {
            return GetBoogieType(methodDefinition.Type);
        }

        public static String GetExternalMethodDefinition(IMethodReference methodRef)
        {
            var methodName = Helpers.GetMethodName(methodRef);
            var parameters = Helpers.GetParametersWithBoogieType(methodRef);
            var returnType = Helpers.GetMethodBoogieReturnType(methodRef) == null ? String.Empty : ("returns (r : " + Helpers.GetMethodBoogieReturnType(methodRef) + ")");

            var t = new BoogieProcedureTemplate(methodName, " {:extern} ", String.Empty, String.Empty, parameters, returnType, true);

            return t.TransformText();
        }

        public class SubtypeComparer : IComparer<IMethodReference>
        {
            public int Compare(IMethodReference a, IMethodReference b)
            {
                if (TypeHelper.Type1DerivesFromOrIsTheSameAsType2(a.ContainingType.ResolvedType, b.ContainingType.ResolvedType))
                    return 0;
                else
                    return 1;
            }
        }

        public static IList<IMethodReference> PotentialCalleesUsingCHA(MethodCallInstruction invocation, ClassHierarchyAnalysis CHA)
        {
            var result = new List<IMethodReference>();
            var unsolvedCallee = invocation.Method;
            switch(invocation.Operation)
            {
                case MethodCallOperation.Static:
                    result.Add(unsolvedCallee);
                    break;
                case MethodCallOperation.Virtual:
                    var receiver = invocation.Arguments[0];
                    var calleeTypes = new List<ITypeReference>(CHA.GetAllSubtypes(receiver.Type));
                    calleeTypes.Add(receiver.Type);
                    var candidateCalless = calleeTypes.Select(t => t.FindMethodImplementation(unsolvedCallee));
                    foreach(var candidate in candidateCalless) // improved this
                    {
                        if (!result.Contains(candidate)) 
                            result.Add(candidate);
                    }

                    //result.AddRange(candidateCalless);
                    break;
            }

            result.Sort(new SubtypeComparer()); // improved this
            return result;
        }


        public static IMethodReference FindMethodImplementation(this ITypeReference receiverType, IMethodReference method)
        {
            var result = method;

            while (receiverType != null && !method.ContainingType.TypeEquals(receiverType))
            {
                var receiverTypeDef = receiverType.ResolvedType;
                if (receiverTypeDef == null) break;

                var matchingMethod = receiverTypeDef.Methods.SingleOrDefault(m => m.Name.UniqueKey == method.Name.UniqueKey && MemberHelper.SignaturesAreEqual(m, method));

                if (matchingMethod != null)
                {
                    result = matchingMethod;
                    break;
                }
                else
                {
                    receiverType = receiverTypeDef.BaseClasses.SingleOrDefault();
                }

            }

            return result;
        }
        public static bool TypeEquals(this ITypeReference type1, ITypeReference type2)
        {
            return TypeHelper.TypesAreEquivalent(type1, type2);
        }


        /*
        public static String GetMethodDefinition(IMethodReference methodRef, bool IsExtern)
        {
            var methodName = Helpers.GetMethodName(methodRef);
            var arguments = Helpers.GetParametersWithBoogieType(methodRef);
            var returnType = Helpers.GetMethodBoogieReturnType(methodRef);

            var head = String.Empty;

            if (methodRef.Type.TypeCode != PrimitiveTypeCode.Void)
                head = String.Format("procedure {5} {0} {1}({2}) returns (r : {3}){4}", 
                            IsExtern ? " {:extern}" : String.Empty, 
                            methodName, 
                            arguments, 
                            returnType, 
                            IsExtern ? ";" : String.Empty, 
                            IsMain(methodRef) ? " {:entrypoint}" : String.Empty);
            else
                head = String.Format("procedure {4} {0}  {1}({2}){3}", IsExtern ? " {:extern}" : String.Empty,
                                                                        methodName, 
                                                                        arguments, 
                                                                        IsExtern ? ";" : String.Empty, 
                                                                        IsMain(methodRef) ? " {:entrypoint}" : String.Empty);

            return head;
        }*/

        public static bool IsMain(IMethodReference methodRef)
		{
			if (methodRef.Name == null) return false;
			return methodRef.Name.Value=="Main";
		}

        // name of procedures should be followed by the C# types of the arguments
        // void foo(int i) should be like foo$int(...)
        // this function returns $int
        public static String GetArityWithNonBoogieTypes(IMethodReference methodRef)
        {
            return String.Join("", methodRef.Parameters.Select(v => "$" + v.Type));
        }

        public static String GetParametersWithBoogieType(IMethodReference methodRef)
        {
            var parameters = String.Empty;
            IMethodDefinition methodDef = methodRef as IMethodDefinition;
            if (methodDef != null)
                parameters =  String.Join(",", methodDef.Parameters.Select(v => v.Name + " : " + GetBoogieType(v.Type)));
            else
                parameters = String.Join(",", methodRef.Parameters.Select(v => String.Format("param{0}", v.Index) + " : " + GetBoogieType(v.Type)));

            if (methodRef.CallingConvention.HasFlag(Microsoft.Cci.CallingConvention.HasThis))
                parameters = String.Format("this : Ref{0}{1}", methodRef.ParameterCount > 0 ? "," : String.Empty, parameters);

            parameters = NormalizeStringForCorral(parameters);
            return parameters;
        }

        public static bool IsCompiledGeneratedClass(this INamedTypeDefinition typeAsClassResolved)
        {
            var result = typeAsClassResolved != null && typeAsClassResolved.Attributes.Any(attrib => attrib.Type.GetName().Equals("CompilerGeneratedAttribute"));
            return result;
        }

        public static bool IsCompilerGenerated(this ITypeReference type)
        {
            var resolvedClass = type.ResolvedType as INamedTypeDefinition;

            if (resolvedClass != null)
            {
                return resolvedClass.IsCompiledGeneratedClass();
            }
            return false;
        }

        // workaround
        public static Boolean IsExternal(IMethodDefinition methodDefinition)
        {
            if (methodDefinition.IsConstructor)
            {
                var methodName = Helpers.GetMethodName(methodDefinition);
                if (methodName.Equals("System.Object.#ctor"))
                    return true;
            }

            if (methodDefinition.IsExternal)
                return true;

            return false;
        }

        public static string GetNormalizedType(ITypeReference type)
        {
            var result = type.ToString();
            // Do this well 
            result = result.Replace('<', '$').Replace('>', '$').Replace(", ", "$"); // for example containing type for delegates
            result = NormalizeStringForCorral(result);

            return result;
        }

        public static string NormalizeStringForCorral(string s)
        {
            return s.Replace("::", ".")// for example: static fields
                .Replace("<>", "__");  // class compiled generated
                
            //return s; // .Replace('<', '_').Replace('>', '_');
        }

        public static bool IsConstructor(IMethodReference method)
        {
            return method.Name.Value == ".ctor";
        }


        public static class Strings
        {
            static ISet<string> stringLiterals = new HashSet<string>();
            public static string varNameForStringLiteral(string literal)
            {
                // String literal will start and end with '"'.
                System.Diagnostics.Contracts.Contract.Assume(literal[0] == '"' && literal[literal.Length - 1] == '"');
                stringLiterals.Add(literal);
                return String.Format("$string_literal_{0}", literal.Substring(1, literal.Length - 2));
            }
            public static string fixStringLiteral(IValue v)
            {
                System.Diagnostics.Contracts.Contract.Assume(v.Type.TypeCode.Equals(PrimitiveTypeCode.String));
                string vStr = v.ToString();
                if (v is Constant)
                {
                    vStr = varNameForStringLiteral(vStr);
                    stringLiterals.Add(v.ToString());
                }
                return vStr;
            }

            public static void writeStringConsts(System.IO.StreamWriter sw)
            {
                foreach (var lit in stringLiterals)
                {
                    sw.WriteLine(
                        String.Format("\tvar {0} : Ref;", Helpers.Strings.varNameForStringLiteral(lit))
                        );
                }
            }
        }
    }
	public static class Extensions
	{
		public static string FullName(this ITypeReference tref)
		{
			return TypeHelper.GetTypeName(tref, NameFormattingOptions.Signature | NameFormattingOptions.TypeParameters);
		}
		public static string GetName(this ITypeReference tref)
		{
			if (tref is INamedTypeReference)
				return (tref as INamedTypeReference).Name.Value;

			return TypeHelper.GetTypeName(tref, NameFormattingOptions.OmitContainingType | NameFormattingOptions.OmitContainingNamespace | NameFormattingOptions.SmartTypeName);
		}
	}
}
