﻿using Backend;
using Backend.Model;
using Backend.ThreeAddressCode;
using Backend.ThreeAddressCode.Instructions;
using Backend.ThreeAddressCode.Values;
using Backend.Visitors;
using Microsoft.Cci;
using Microsoft.Cci.Immutable;
using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TinyBCT;

namespace TinyBCT.Translators
{
    // one instance is created for each translated method
    public class InstructionTranslator
    {
        private ClassHierarchyAnalysis CHA;
        private MethodBody methodBody; // currently used to get ExceptionsInformation
        // Diego: No longer required
        // public Dictionary<string, int> labels = new Dictionary<string, int>();

        // store for extern method called
        // since those methods do not have bodies, they are not translated
        // we need to declared them if they are called
        public static ISet<IMethodReference> ExternMethodsCalled = new HashSet<IMethodReference>();
        // store for methods that have not been found yet but have been called
        // Also necessary to declare them if they end up being missing
        public static ISet<IMethodReference> PotentiallyMissingMethodsCalled = new HashSet<IMethodReference>();

        // Type definitions might not be present in the dll because they are instantiations of generic types,
        // for example, List<int>. For these types, we will need to add the appropriate constants and axioms
        // in Boogie.
        public static ISet<ITypeReference> MentionedClasses = new HashSet<ITypeReference>();

        Translation translation;
        Instruction lastInstruction = null;
        // while translating instructions some variables may be removed
        // for example for delegates there are instructions that are no longer used
        public ISet<IVariable> RemovedVariables { get; } = new HashSet<IVariable>();
        public ISet<IVariable> AddedVariables { get; } = new HashSet<IVariable>();

        // new address modelling only:
        // The variables stored in this set are accessed in a slightly different way than local variables.
        // As such, we need to create the _{var_name} boogie variable for the address but also
        // the {var_name};
        public ISet<IVariable> ShouldCreateValueVariable { get; } = new HashSet<IVariable>();

        private IPrimarySourceLocation prevSourceLocation;

        protected IMethodDefinition method;
        public Dictionary<string, Helpers.BoogieType> temporalVariables;

        public InstructionTranslator(ClassHierarchyAnalysis CHA, MethodBody methodBody)
        {
            this.CHA = CHA;
            this.method = methodBody.MethodDefinition;
            this.methodBody = methodBody;
            temporalVariables = new Dictionary<string, Helpers.BoogieType>();
        }

        public BoogieVariable GetFreshVariable(Helpers.BoogieType type)
        {
            return BoogieVariable.GetTempVar(type, temporalVariables);

        }

        private StringBuilder sb = new StringBuilder();
        public void AddBoogie(string boogie)
        {
            sb.AppendLine(boogie);
        }
        public string Boogie()
        {
            return sb.ToString().Replace("<>", "__");
        }

        public void Translate(/*IList<Instruction> instructions, int idx*/)
        {
            var bg = BoogieGenerator.Instance();

            var instructions = methodBody.Instructions;

            if (!methodBody.MethodDefinition.IsStatic) // this variable cannot be null
            {
                var this_var = methodBody.Parameters[0];
                AddBoogie(bg.Assume(this_var.Name + " != null"));

                var this_var_type = this_var.Type;
                if(this_var_type is IManagedPointerType)
                {
                    this_var_type = (this_var_type as IManagedPointerType).TargetType;
                }
                
                var subtype = bg.Subtype(bg.DynamicType(this_var), this_var_type);
                AddBoogie(bg.Assume(subtype));

                // hack for contractor
                if (methodBody.MethodDefinition.Name.Value.Contains("STATE$"))
                    AddBoogie(bg.AssumeDynamicType(this_var, this_var.Type));
            }

            foreach (var p in methodBody.MethodDefinition.Parameters)
            {
                if (Helpers.IsBoogieRefType(p.Type) && !p.IsByReference && !p.IsOut)
                {
                    // BUG BUG BUG BUG - by ref 
                    var refNotNull = String.Format("{0} == null", p.Name.Value);
                    var paramVariable = methodBody.Parameters.Single(v => v.Name.Equals(p.Name.Value));
                    var subtype = bg.Subtype(bg.DynamicType(paramVariable), paramVariable.Type);
                    var or = string.Format("{0} || {1}", refNotNull, subtype);
                    AddBoogie(bg.Assume(or));
                }
            }

            for (int idx = 0; idx < instructions.Count(); idx++)
            {
                SetState(instructions, idx);
                var currentInstruction = instructions[idx];
                translation.AddLabel(currentInstruction);
                translation.AddLineNumbers(currentInstruction, prevSourceLocation);
                if (currentInstruction.Location != null)
                    prevSourceLocation = currentInstruction.Location;
                instructions[idx].Accept(translation);
                lastInstruction = instructions[idx];
            }
        }

        void SetState(IList<Instruction> instructions, int idx)
        {
            if (AtomicArrayInitializationTranslation.IsAtomicArrayInitializationTranslation(instructions, idx))
                translation = new AtomicArrayInitializationTranslation(this);
            else if (DelegateInvokeTranslation.IsDelegateInvokeTranslation(instructions, idx))
                translation = new DelegateInvokeTranslation(this);
            else if (DelegateCreationTranslation.IsDelegateCreationTranslation(instructions, idx))
                translation = new DelegateCreationTranslation(this);
            else if (ExceptionTranslation.IsExceptionTranslation(instructions, idx))
                translation = new ExceptionTranslation(this);
            else if (ArrayTranslation.IsArrayTranslation(instructions, idx))
                translation = new ArrayTranslation(this);
            else
            {
                if (Settings.NewAddrModelling)
                    translation = new SimpleTranslation(this);
                else
                    // for now this is disable for new memory modeling
                    translation = new NullDereferenceInstrumenter(this);
            }
        }

        protected abstract class Translation : InstructionVisitor
        {
            protected InstructionTranslator instTranslator;
            protected BoogieGenerator boogieGenerator = BoogieGenerator.Instance();
            public Translation(InstructionTranslator p)
            {
                instTranslator = p;
            }

            protected void AddBoogie(string boogie)
            {
                instTranslator.AddBoogie(boogie);
            }

            internal void AddLabel(Instruction instr)
            { 
                string label = instr.Label;
                if(!String.IsNullOrEmpty(label))
                   AddBoogie(String.Format("\t{0}:", label));                    
            }

            internal void AddLineNumbers(Instruction instr, IPrimarySourceLocation prevSourceLocation)
            {
                if (!Settings.EmitLineNumbers)
                    return;
                IPrimarySourceLocation location = instr.Location;

                if (location == null && prevSourceLocation != null)
                {
                    location = prevSourceLocation;
                }
                if(location!=null)
                { 
                    var fileName = location.SourceDocument.Name;
                    var sourceLine = location.StartLine;
                    AddBoogie(boogieGenerator.Assert(String.Format("{{:sourceFile \"{0}\"}} {{:sourceLine {1} }} true", fileName, sourceLine)));
                    //     assert {:first} {:sourceFile "C:\Users\diegog\source\repos\corral\AddOns\AngelicVerifierNull\test\c#\As\As.cs"} {:sourceLine 23} true;
                }
                else 
                {
                    if (Settings.DebugLines)
                    {
                        AddBoogie(boogieGenerator.Assert(String.Format("{{:sourceFile \"{0}\"}} {{:sourceLine {1} }} true", "Empty", 0)));
                    }
                }
            }

            // THIS IS NOT THE HAPPIEST NAME FOR THIS FUNCTIOn
            // THIS FUNCTION CAN BE CALLED EVEN IF METHOD CALL OPERATION IS STATIC!
            // IN THAT CASE IT WILL DIRECTLY CALL funcOnPontentialCallee over "method"
            protected void DynamicDispatch(IMethodReference method, IVariable receiver, MethodCallOperation operation, Func<IMethodReference, string> funcOnPotentialCallee)
            {
                var calless = Helpers.PotentialCalleesUsingCHA(method, receiver, operation, Traverser.CHA);

                // note: analysis-net changed and required to pass a method reference in the LocalVariable constructor
                var getTypeVar = AddNewLocalVariableToMethod("DynamicDispatch_Type_", Types.Instance.PlatformType.SystemObject);

                var args = new List<string>();
                args.Add(boogieGenerator.ReadAddr(receiver).Expr);
                AddBoogie(boogieGenerator.ProcedureCall("System.Object.GetType", args, getTypeVar.Name));

                if (operation == MethodCallOperation.Virtual)
                {

                    if (calless.Count > 0)
                    {

                        // example:if ($tmp6 == T$DynamicDispatch.Dog())
                        AddBoogie(boogieGenerator.If(boogieGenerator.Subtype(getTypeVar, calless.First().ContainingType), funcOnPotentialCallee(calless.First())));

                        int i = 0;
                        foreach (var impl in calless)
                        {
                            MentionedClasses.Add(impl.ContainingType);
                            // first  invocation is not handled in this loop
                            if (i == 0)
                            {
                                i++;
                                continue;
                            }

                            AddBoogie(boogieGenerator.ElseIf(boogieGenerator.Subtype(getTypeVar, impl.ContainingType), funcOnPotentialCallee(impl)));
                            i++;
                        }
                    }

                    // we need to extend the else if chain
                    // Diego: Check to refactor to avoid duplication
                    if (calless.Count > 0)
                    {
                        if (method.ResolvedMethod != null && (method.ResolvedMethod.IsAbstract || method.ResolvedMethod.IsExternal))
                        {
                            if (Settings.AvoidSubtypeCheckingForInterfaces)
                            {
                                AddBoogie(boogieGenerator.Else(funcOnPotentialCallee(method)));
                            }
                            else
                            {
                                AddBoogie(boogieGenerator.ElseIf(boogieGenerator.Subtype(getTypeVar, method.ContainingType), funcOnPotentialCallee(method)));
                                AddBoogie(boogieGenerator.ElseIf(Expression.NotEquals(boogieGenerator.ReadAddr(receiver), boogieGenerator.NullObject()).Expr, boogieGenerator.Assert("false")));
                            }
                        }
                        else
                        {
                            AddBoogie(boogieGenerator.ElseIf(Expression.NotEquals(boogieGenerator.ReadAddr(receiver), boogieGenerator.NullObject()).Expr, boogieGenerator.Assert("false")));
                        }
                    }
                    else
                    {
                        if (method.ResolvedMethod != null && (method.ResolvedMethod.IsAbstract || method.ResolvedMethod.IsExternal))
                        {
                            if (Settings.AvoidSubtypeCheckingForInterfaces)
                            {
                                AddBoogie(funcOnPotentialCallee(method));
                            }
                            else
                            {
                                AddBoogie(boogieGenerator.If(boogieGenerator.Subtype(getTypeVar, method.ContainingType), funcOnPotentialCallee(method)));
                                AddBoogie(boogieGenerator.ElseIf(Expression.NotEquals(boogieGenerator.ReadAddr(receiver), boogieGenerator.NullObject()).Expr, boogieGenerator.Assert("false")));
                            }
                        }
                        else
                        {
                            AddBoogie(boogieGenerator.If(Expression.NotEquals(boogieGenerator.ReadAddr(receiver), boogieGenerator.NullObject()).Expr, boogieGenerator.Assert("false")));
                        }

                    }
                }
                else
                {
                    AddBoogie(funcOnPotentialCallee(method));
                }

                if (Helpers.IsExternal(method.ResolvedMethod) || method.ResolvedMethod.IsAbstract)
                    SimpleTranslation.AddToExternalMethods(method);
            }

            public IVariable AddNewLocalVariableToMethod(string name, ITypeReference type, bool isParameter = false)
            {
                string variableName = String.Format("{0}{1}", name, instTranslator.AddedVariables.Count);
                var tempVar = new LocalVariable(variableName, isParameter, instTranslator.method);
                tempVar.Type = type;
                instTranslator.AddedVariables.Add(tempVar);
                instTranslator.ShouldCreateValueVariable.Add(tempVar);

                return tempVar;
            }

            public string AddSubtypeInformationToExternCall(MethodCallInstruction instruction)
            {
                var methodRef = instruction.Method;
                // IsDelegateInvokation requires containing type != null - not sure if that always holds
                if (methodRef.ResolvedMethod == null || Helpers.IsExternal(methodRef.ResolvedMethod) || DelegateInvokeTranslation.IsDelegateInvokation(instruction))
                    if (instruction.HasResult && Helpers.IsBoogieRefType(methodRef.Type))
                        return boogieGenerator.Assume(boogieGenerator.Subtype(boogieGenerator.DynamicType(instruction.Result), methodRef.Type));

                return String.Empty;
            }
        }

        // http://community.bartdesmet.net/blogs/bart/archive/2008/08/21/how-c-array-initializers-work.aspx
        class AtomicArrayInitializationTranslation : Translation
        {
            public AtomicArrayInitializationTranslation(InstructionTranslator p) : base(p)
            {
            }

            // we expect the following pattern
            // LoadTokenInstruction, MethodCallInstruction(InitializeArray)
            public static bool IsAtomicArrayInitializationTranslation(IList<Instruction> instructions, int idx)
            {
                Instruction ins = instructions[idx];

                if (!Settings.AtomicInit)
                    return false;

                if (ins is LoadTokenInstruction && idx+1 < instructions.Count && instructions[idx+1] is MethodCallInstruction)
                {
                    MethodCallInstruction methodCallIns = instructions[idx + 1] as MethodCallInstruction;
                    if (methodCallIns.Method.Name.Value.Equals("InitializeArray"))
                        return true;
                }

                if (ins is MethodCallInstruction && idx - 1 >= 0 && instructions[idx - 1] is LoadTokenInstruction)
                {
                    MethodCallInstruction methodCallIns = instructions[idx] as MethodCallInstruction;
                    if (methodCallIns.Method.Name.Value.Equals("InitializeArray"))
                        return true;
                }

                return false;
            }

            private static LoadTokenInstruction loadTokenIns = null;

            public override void Visit(LoadTokenInstruction instruction)
            {
                // nothing
                loadTokenIns = instruction;

            }

            public override void Visit(MethodCallInstruction instruction)
            {
                // this atomic initialization is expected to set all elements of the array in one operation
                // we are not setting the specific elements because we should read the .data of the PE
                // we want to havoc every element of the array, and they cannot be null (the elements)

                // before processing this atomic initialization the array was recently created
                // in boogie we set every element to be null after  creation
                // that is done doing an assume like this: assume forall $tmp1: int :: $ArrayContents[ARRAY_REF][$tmp1] == null
                // if we make a new assume statement doing assume forall $tmp1: int :: $ArrayContents[ARRAY_REF][$tmp1] != null
                // our traces set would be empty
                // therefore we will use $HavocArrayElementsNoNull (defined in the prelude)

                IFieldDefinition f = loadTokenIns.Token as IFieldDefinition;              

                // hacky. In the debugger f.Type has property SizeOf which contains this value, but I can't access to it
                var name = f.Type.GetName(); // .....=SIZE
                Contract.Assume(name.Count(c => c == '=') == 1);
                var byteSize = UInt32.Parse(name.Substring(name.IndexOf('=')+1));
                Contract.Assert(byteSize > 0);

                // accessing the element's typecode
                IVariable array = instruction.Arguments[0];
                ArrayType arrayType = array.Type as ArrayType;
                PrimitiveTypeCode elementTypeCode = arrayType.ElementType.TypeCode;

                // i think that the array cannot contain values that do not have a fixed size
                // however if the value size cannot be predicted, we should havoc the array length
                uint elementSize = Helpers.SizeOf(elementTypeCode);
                Contract.Assert(elementSize > 0);
                uint arrayLength = byteSize / elementSize;

                var arrayNotNull = Expression.NotEquals(boogieGenerator.ReadAddr(array), boogieGenerator.NullObject());
                AddBoogie(boogieGenerator.Assume(arrayNotNull.Expr));
                AddBoogie(boogieGenerator.AssumeArrayLength(boogieGenerator.ReadAddr(array), arrayLength.ToString()));
                List<string> args = new List<string>();
                args.Add(boogieGenerator.ReadAddr(array).Expr);
                // sets all array elements different than null
                AddBoogie(boogieGenerator.ProcedureCall("$HavocArrayElementsNoNull", args));

                loadTokenIns = null;
            }
        }

        class NullDereferenceInstrumenter : SimpleTranslation
        {
            public NullDereferenceInstrumenter(InstructionTranslator p) : base(p)
            {
            }

            public override void Visit(MethodCallInstruction instruction)
            {
                var unspecializedMethod = Helpers.GetUnspecializedVersion(instruction.Method);
                // Instance methods, passing 'this'
                if (unspecializedMethod.Parameters.Count() != instruction.Arguments.Count())
                {

                    var refNotNullStr = String.Format("{{:nonnull}} {0} != null", instruction.Arguments.ElementAt(0));
                    if (Settings.CheckNullDereferences)
                    {
                        AddBoogie(boogieGenerator.Assert(refNotNullStr));
                    }
                    else
                    {
                        AddBoogie(boogieGenerator.Assume(refNotNullStr));
                    }
                }

                base.Visit(instruction);
            }
            public override void Visit(LoadInstruction instruction)
            {
                string refString = null;
                if (instruction.Operand is InstanceFieldAccess) // memory access handling
                {
                    var instanceFieldAccess = instruction.Operand as InstanceFieldAccess;
                    refString = instanceFieldAccess.Instance.ToString();
                }
                if (refString != null)
                {
                    var refNotNullStr = String.Format("{{:nonnull}} {0} != null", refString);
                    if (Settings.CheckNullDereferences)
                    {
                        AddBoogie(boogieGenerator.Assert(refNotNullStr));
                    }
                    else
                    {
                        AddBoogie(boogieGenerator.Assume(refNotNullStr));
                    }
                }

                base.Visit(instruction);
            }
            public override void Visit(StoreInstruction instruction)
            {
                string refString = null;
                if (instruction.Result is InstanceFieldAccess) // memory access handling
                {
                    var instanceFieldAccess = instruction.Result as InstanceFieldAccess;
                    refString = instanceFieldAccess.Instance.ToString();
                }
                if (refString != null)
                {
                    var refNotNullStr = String.Format("{{:nonnull}} {0} != null", refString);
                    if (Settings.CheckNullDereferences)
                    {
                        AddBoogie(boogieGenerator.Assert(refNotNullStr));
                    }
                    else
                    {
                        AddBoogie(boogieGenerator.Assume(refNotNullStr));
                    }
                }

                base.Visit(instruction);
            }
        }


        // translates each instruction independently 
        class SimpleTranslation : Translation
        {
            public SimpleTranslation(InstructionTranslator p) : base(p)
            {
            }

            public override void Visit(LoadTokenInstruction instruction)
            {
                AddBoogie(boogieGenerator.HavocResult(instruction));

                //// unexpected LoadTokenInstruction
                //// only handled for array initialization, see the AtomicArrayInitializationTranslation.
                //// if you want to handle array init set atomicInitArray=true in the command line
                //throw new NotImplementedException();
            }
            public override void Visit(NopInstruction instruction)
            {
            }

            public override void Visit(SwitchInstruction instruction)
            {
                StringBuilder sb = new StringBuilder();
                var idx = 0;
                foreach (var target in instruction.Targets)
                {
                    var indexCondition = Expression.ExprEquals(boogieGenerator.ReadAddr(instruction.Operand), idx);
                    var body = boogieGenerator.Goto(target);
                    sb.AppendLine(boogieGenerator.If(indexCondition.Expr, body));
                    idx++;
                }

                var cond = Expression.LessThan(boogieGenerator.ReadAddr(instruction.Operand), instruction.Targets.Count);
                AddBoogie(boogieGenerator.If(cond.Expr, sb.ToString()));
            }

            public override void Visit(UnaryInstruction instruction)
            {
                var exp = Expression.UnaryOperationExpression(instruction.Operand, instruction.Operation);
                var assignment = boogieGenerator.VariableAssignment(instruction.Result, exp);

                AddBoogie(assignment);
            }

            public override void Visit(BinaryInstruction instruction)
            {
                //addLabel(instruction);

                IVariable left = instruction.LeftOperand;
                IVariable right = instruction.RightOperand;


                if (left.Type.TypeCode.Equals(PrimitiveTypeCode.String) ||
                    right.Type.TypeCode.Equals(PrimitiveTypeCode.String))
                {
                    string methodName = Helpers.Strings.GetBinaryMethod(instruction.Operation);
                    
                    var tempVar = instTranslator.GetFreshVariable(Helpers.GetBoogieType(instruction.Result.Type));
                    var arguments = new List<string>();
                    arguments.Add(Helpers.Strings.FixStringLiteral(left, boogieGenerator).Expr);
                    arguments.Add(Helpers.Strings.FixStringLiteral(right, boogieGenerator).Expr);
                    AddBoogie(boogieGenerator.ProcedureCall(methodName, arguments, tempVar));
                    AddBoogie(boogieGenerator.VariableAssignment(instruction.Result, tempVar));
                } else
                {
                    // TODO(rcastano): refactor this. Ideally, there might be different BoogieGenerators,
                    // each making slightly different translations, for example, bitvector representation
                    // of integers as opposed to Boogie int.
                    // When that happens, this should most likely be encapsulated within BoogieGenerator.
                    if (Expression.IsSupportedBinaryOperation(instruction.Operation, Helpers.GetBoogieType(left.Type), Helpers.GetBoogieType(right.Type)))
                    {
                        // mod keyword in boogie returns integer
                        if (BinaryOperation.Rem == instruction.Operation && 
                            Helpers.GetBoogieType(instruction.Result.Type) != Helpers.BoogieType.Int)
                            Contract.Assert(false);

                        var exp = Expression.BinaryOperationExpression(boogieGenerator.ReadAddr(left), boogieGenerator.ReadAddr(right), instruction.Operation);
                        var assignment = boogieGenerator.VariableAssignment(instruction.Result, exp);
                        AddBoogie(assignment);
                    }
                    else
                    {
                        AddBoogie(boogieGenerator.HavocResult(instruction));
                    }
                }
            }

            public override void Visit(UnconditionalBranchInstruction instruction)
            {
                if (instruction.IsLeaveProtectedBlock)
                {
                    // this is a special goto statement
                    // it is used to exit a try or a catch

                    // we should check if there is a finally statement in the same try where this instruction is located
                    // if there is one, we jump to it.

                    // remember that there can be a finally when there are no catches or viceversa

                    var branchOffset = instruction.Offset;

                    // check if it is inside a catch handler
                    var catchContaining = from pb in instTranslator.methodBody.ExceptionInformation
                                     where
                                        branchOffset >= Convert.ToInt32(pb.Handler.Start.Substring(2),16) 
                                        && branchOffset <= Convert.ToInt32(pb.Handler.End.Substring(2), 16)
                                        && pb.Handler.Kind == ExceptionHandlerBlockKind.Catch
                                           orderby Convert.ToInt32(pb.Start.Substring(2), 16) descending
                                           select pb;

                    // check if it is in a try
                    var tryContaining = from pb in instTranslator.methodBody.ExceptionInformation
                                          where
                                             branchOffset >= Convert.ToInt32(pb.Start.Substring(2), 16)
                                             && branchOffset <= Convert.ToInt32(pb.End.Substring(2), 16)
                                             //&& pb.Handler.Kind != ExceptionHandlerBlockKind.Catch
                                          orderby Convert.ToInt32(pb.Start.Substring(2), 16) descending
                                          select pb;

                    ProtectedBlock containingBlock = null;

                    if (catchContaining.Count() > 0 && tryContaining.Count() == 0)
                        containingBlock = catchContaining.First();
                    else if (catchContaining.Count() == 0 && tryContaining.Count() > 0)
                        containingBlock = tryContaining.First();
                    else if (catchContaining.Count() > 0 && tryContaining.Count() > 0)
                    {
                        var cStart = Convert.ToInt32(catchContaining.First().Start.Substring(2), 16);
                        var tStart = Convert.ToInt32(tryContaining.First().Start.Substring(2), 16);
                        containingBlock = cStart > tStart ? catchContaining.First() : tryContaining.First();
                    }

                    // we know where the protected block starts, we look for a finally handler in the same level.
                    var target = from pb in instTranslator.methodBody.ExceptionInformation
                                 where
                                     pb.Start == containingBlock.Start && pb.Handler.Kind == ExceptionHandlerBlockKind.Finally
                                 orderby Convert.ToInt32(pb.Start.Substring(2), 16) descending
                                 select pb.Handler.Start;

                    if (target.Count() > 0) // is there a finally?
                    {
                        AddBoogie(boogieGenerator.Goto(target.First()));
                        return;
                    }

                }

                AddBoogie(boogieGenerator.Goto(instruction.Target));
            }

            public override void Visit(ReturnInstruction instruction)
            {

                // mapping between original parameters and copies created when a modification was spotted
                // corral wants immutable paramters
                IDictionary<IVariable, IVariable> argToNewVariable = null;
                ImmutableArguments.MethodToMapping.TryGetValue(instTranslator.methodBody, out argToNewVariable);

                if (!Settings.NewAddrModelling)
                {
                    foreach (var p in instTranslator.method.Parameters.Where(p => p.IsOut || p.IsByReference))
                    {
                        IVariable pInMethodBody = instTranslator.methodBody.Parameters.Single(v => v.Name.Equals(p.Name.Value));
                        
                        var outVar = BoogieParameter.OutVariable(p);
                        if (argToNewVariable != null && argToNewVariable.ContainsKey(pInMethodBody))
                        {
                            AddBoogie(boogieGenerator.VariableAssignment(outVar, boogieGenerator.ReadAddr(argToNewVariable[pInMethodBody])));
                            // $outNombreDeLaVariableORIGINAL := variableMapeada;
                        } else
                        {
                            var iVariable = instTranslator.methodBody.Parameters.Single(v => v.Name.Equals(p.Name.Value));
                            AddBoogie(boogieGenerator.VariableAssignment(outVar, boogieGenerator.ReadAddr(iVariable)));
                            // $outNombreDeLaVariableORIGINAL := nombreDeLaVariableOriginal;
                        }
                    }
                }

                if (instruction.HasOperand)
                {
                    var boogieResVar = BoogieVariable.ResultVar(Helpers.GetBoogieType(instruction.Operand.Type));
                    AddBoogie(boogieGenerator.VariableAssignment(boogieResVar, boogieGenerator.ReadAddr(instruction.Operand)));
                }

                AddBoogie("return ;");
            }

            public override void Visit(LoadInstruction instruction)
            {
                var instructionOperand = instruction.Operand;

                // hack of old memory modelling
                if (!Settings.NewAddrModelling && instructionOperand is Reference)
                {
                    // Reference loading only found when using "default" keyword.
                    // Ignoring translation, the actual value referenced is used by accessing
                    // instTranslator.lastInstruction [see Visit(InitializeObjectInstruction instruction)]
                    // TODO(rcastano): check that loaded references are only used in the assumed context.
                    instructionOperand = (instructionOperand as Reference).Value;
                }
            
                if (instructionOperand is InstanceFieldAccess) // memory access handling
                {
                    InstanceFieldAccess instanceFieldOp = instructionOperand as InstanceFieldAccess;
                    AddBoogie(boogieGenerator.ReadInstanceField(instanceFieldOp, instruction.Result));

                    if (Helpers.IsBoogieRefType(instanceFieldOp.Type))
                        AddBoogie(boogieGenerator.Assume(boogieGenerator.Subtype(boogieGenerator.DynamicType(instruction.Result), instanceFieldOp.Type)));
                }
                else if (instructionOperand is StaticFieldAccess) // memory access handling
                {
                    // static fields are considered global variables
                    var staticFieldAccess = instructionOperand as StaticFieldAccess;

                    // code generated from (x => x * x) may have a singleton
                    // we will force to initialize the singleton every time it is used
                    // if it is not defined we will have a wrong delegate invokation
                    // the invariant for this is : null || correct delegate
                    // * null case:
                    //      it is correctly initialized
                    // * correct delegate
                    //      * correct invokation
                    // this problem arise becuase static constructors are not called
                    // also there is no invariant for static variables
                    if (staticFieldAccess.Type.ResolvedType.IsDelegate &&
                        staticFieldAccess.Field.ContainingType.IsCompilerGenerated()) 
                    {
                        var staticField = new StaticField(staticFieldAccess);
                        AddBoogie(boogieGenerator.WriteAddr(staticField, boogieGenerator.NullObject()));
                    }

                    AddBoogie(boogieGenerator.ReadStaticField(staticFieldAccess, instruction.Result));
                }

                else if (instructionOperand is StaticMethodReference) // delegates handling
                {
                    // see DelegateTranslation
                    Contract.Assert(false);
                }
                else
                {
                    if (Settings.NewAddrModelling && instructionOperand is Reference)
                    {
                        AddBoogie(boogieGenerator.VariableAssignment(instruction.Result, instructionOperand));
                    } else if (instructionOperand is Dereference)
                    {
                        AddBoogie(boogieGenerator.VariableAssignment(instruction.Result, instructionOperand));
                    } else if (instructionOperand.Type.TypeCode.Equals(PrimitiveTypeCode.String) && instruction.Operand is Constant)
                    {
                        // strings values that appear in the three address code are declared globally in the .bpl
                        // stringVariableName is the name of the declared global variable

                        Expression expr = Helpers.Strings.FixStringLiteral(instructionOperand, boogieGenerator);

                        AddBoogie(boogieGenerator.VariableAssignment(instruction.Result, expr));
                    } else if (instructionOperand is IVariable || instructionOperand is Constant || (instructionOperand is Reference && !Settings.NewAddrModelling))
                    {
                        AddBoogie(boogieGenerator.VariableAssignment(instruction.Result, instructionOperand));
                    } else if (instructionOperand is Reference && Settings.NewAddrModelling)
                    {
                        throw new NotImplementedException();
                    } else
                    {
                        Contract.Assert(false);
                    }
                }

                if (instruction.Result is IVariable &&
                    instruction.Result.Type.TypeCode.Equals(PrimitiveTypeCode.String))
                {
                    MentionedClasses.Add(instruction.Result.Type);
                    AddBoogie(boogieGenerator.AssumeDynamicType(instruction.Result, instruction.Result.Type));
                    AddBoogie(boogieGenerator.AssumeTypeConstructor(instruction.Result, instruction.Result.Type));
                }
            }

            public static void AddToExternalMethods(IMethodReference method)
            {
                ExternMethodsCalled.Add(method.ResolvedMethod);
            }

            private string CallMethod(MethodCallInstruction instruction, List<IVariable> arguments, IMethodReference callee)
            {
                var methodDefinition = callee.ResolvedMethod;
                if (Helpers.IsExternal(methodDefinition))
                    AddToExternalMethods(methodDefinition);
                // Important not to add methods to both sets.
                else if (Helpers.IsCurrentlyMissing(methodDefinition))
                    PotentiallyMissingMethodsCalled.Add(Helpers.GetUnspecializedVersion(methodDefinition));

                var signature = Helpers.GetMethodName(callee);
                var sb = new StringBuilder();

                if (instruction.HasResult)
                {
                    //         call $tmp0 := DynamicDispatch.Mammal.Breathe(a);
                    // the union depends on the type of the arguments
                    var resType = Helpers.GetBoogieType(instruction.Result.Type);
                    var methodType = Helpers.GetMethodBoogieReturnType(Helpers.GetUnspecializedVersion(callee));
                    if (methodType.Equals(resType) || Helpers.IsBoogieRefType(instruction.Result.Type)) // Ref and Union are alias
                    {
                        sb.AppendLine(boogieGenerator.ProcedureCall(callee, arguments, instTranslator, instruction.Result));
                    }
                    else
                    {
                        // TODO(rcastano): reuse variable
                        var boogieVar = instTranslator.GetFreshVariable(Helpers.GetBoogieType(Types.Instance.PlatformType.SystemObject));
                        sb.AppendLine(boogieGenerator.ProcedureCall(callee, arguments, boogieVar));
                        sb.AppendLine(boogieGenerator.VariableAssignment(instruction.Result, Expression.Union2PrimitiveType(resType, boogieVar)));
                    }
                }
                else
                {
                    sb.AppendLine(boogieGenerator.ProcedureCall(callee, arguments, instTranslator));
                }

                sb.AppendLine(AddSubtypeInformationToExternCall(instruction));

                return sb.ToString();
            }

            public override void Visit(MethodCallInstruction instruction)
            {
                // This is check is done because an object creation is splitted into two TAC instructions
                // This prevents to add the same instruction tag twice
                // DIEGO: Removed after fix in analysis framewlrk 
                // if (!Helpers.IsConstructor(instruction.Method))
                //addLabel(instruction);
                //var arguments = "";
                List<string> toAppend = new List<string>();

                var methodName = instruction.Method.ContainingType.FullName() + "." + instruction.Method.Name.Value;
                var copyArgs = ComputeArguments(instruction, toAppend);

                foreach (var line in toAppend)
                {
                    AddBoogie(line);
                }

                if (methodName == "System.Diagnostics.Contracts.Contract.Assert")
                {
                    AddBoogie(boogieGenerator.Assert(instruction.Variables.First()));
                    return;
                }
                else if (methodName == "System.Diagnostics.Contracts.Contract.Assume")
                {
                    AddBoogie(boogieGenerator.Assume(instruction.Variables.First()));
                    return;
                }
                // Diego: BUGBUG. Some non-virtual but non-static call (i.e., on particular instances) are categorized as static!
                // Our examples are compiled agains mscorlib generic types and we need to replace some method with colection stubs
                // All the generics treatment is performed in Dynamic dispatch, so we are not converting some invocations 
                else if (instruction.Operation == MethodCallOperation.Virtual)
                {
                    Func<IMethodReference, string> onPotentialCallee = (potential => CallMethod(instruction, copyArgs, potential));
                    DynamicDispatch(instruction.Method, instruction.Arguments.Count > 0 ? instruction.Arguments[0] : null, instruction.Method.IsStatic ? MethodCallOperation.Static : MethodCallOperation.Virtual, onPotentialCallee);
                    AddBoogie(ExceptionTranslation.HandleExceptionAfterMethodCall(instruction));
                    return;
                }

                var signature = Helpers.GetMethodName(instruction.Method);

                AddBoogie(CallMethod(instruction, copyArgs, instruction.Method));

                AddBoogie(ExceptionTranslation.HandleExceptionAfterMethodCall(instruction));
            }

            private List<IVariable> ComputeArguments(MethodCallInstruction instruction,  List<string> toAppend)
            {
                var copyArgs = new List<IVariable>();

                var unspecializedMethod = Helpers.GetUnspecializedVersion(instruction.Method);
                Contract.Assume(
                    unspecializedMethod.Parameters.Count() == instruction.Arguments.Count() ||
                    unspecializedMethod.Parameters.Count() + 1 == instruction.Arguments.Count());
                // Instance methods, passing 'this'
                if (unspecializedMethod.Parameters.Count() != instruction.Arguments.Count())
                {
                    copyArgs.Add(instruction.Arguments.ElementAt(0));
                }
                for (int i = 0; i < instruction.Method.Parameters.Count(); ++i)
                {
                    int arg_i =
                        unspecializedMethod.Parameters.Count() == instruction.Arguments.Count() ?
                        i : i + 1;
                    var paramType = unspecializedMethod.Parameters.ElementAt(i).IsByReference && Settings.NewAddrModelling ? Helpers.BoogieType.Addr : Helpers.GetBoogieType(unspecializedMethod.Parameters.ElementAt(i).Type);
                    var argType = Helpers.GetBoogieType(instruction.Arguments.ElementAt(arg_i).Type);
                    if (!paramType.Equals(argType))
                    {
                        // TODO(rcastano): try to reuse variables.
                        var localVar = AddNewLocalVariableToMethod("$temp_var_", Types.Instance.PlatformType.SystemObject, false);

                        var bg = boogieGenerator;
                        // intended output: String.Format("\t\t{0} := {2}2Union({1});", localVar, instruction.Arguments.ElementAt(arg_i), argType)
                        toAppend.Add(bg.VariableAssignment(localVar, Expression.PrimitiveType2Union(boogieGenerator.ReadAddr(instruction.Arguments.ElementAt(arg_i)))));

                        copyArgs.Add(localVar);
                    }
                    else
                    {
                        copyArgs.Add(instruction.Arguments.ElementAt(arg_i));
                    }
                }
                return copyArgs;
            }

            public override void Visit(ConditionalBranchInstruction instruction)
            {
                IVariable leftOperand = instruction.LeftOperand;
                IInmediateValue rightOperand = instruction.RightOperand;
                
                var bg = boogieGenerator;
                if (leftOperand.Type.TypeCode.Equals(PrimitiveTypeCode.String) ||
                    rightOperand.Type.TypeCode.Equals(PrimitiveTypeCode.String))
                {
                    string methodName = Helpers.Strings.GetBinaryMethod(instruction.Operation);

                    var tempVar = AddNewLocalVariableToMethod("tempVarStringBinOp_", Types.Instance.PlatformType.SystemBoolean);
                    var arguments = new List<string>();
                    arguments.Add(Helpers.Strings.FixStringLiteral(leftOperand, boogieGenerator).Expr);
                    arguments.Add(Helpers.Strings.FixStringLiteral(rightOperand, boogieGenerator).Expr);
                    AddBoogie(boogieGenerator.ProcedureCall(methodName, arguments, tempVar.ToString()));
                    AddBoogie(bg.If(bg.ReadAddr(tempVar).Expr, bg.Goto(instruction.Target)));
                }
                else
                {
                    AddBoogie(bg.If(bg.BranchOperationExpression(leftOperand, rightOperand, instruction.Operation), bg.Goto(instruction.Target)));
                }
            }

            public override void Visit(CreateObjectInstruction instruction)
            {
                // assume $DynamicType($tmp0) == T$TestType();
                //assume $TypeConstructor($DynamicType($tmp0)) == T$TestType;

                var bg = boogieGenerator;
                AddBoogie(bg.AllocObject(instruction.Result, instTranslator));
                var typeString = Helpers.GetNormalizedType(TypeHelper.UninstantiateAndUnspecialize(instruction.AllocationType));
                InstructionTranslator.MentionedClasses.Add(instruction.AllocationType);
                AddBoogie(bg.AssumeDynamicType(instruction.Result, instruction.AllocationType));
                AddBoogie(bg.AssumeTypeConstructor(bg.ReadAddr(instruction.Result).Expr, typeString));
            }

            public override void Visit(StoreInstruction instruction)
            {
                var instanceFieldAccess = instruction.Result as InstanceFieldAccess; // where it is stored
                var staticFieldAccess = instruction.Result as StaticFieldAccess;
                var dereference = instruction.Result as Dereference;

                if (instanceFieldAccess != null)
                {
                    var p = boogieGenerator.WriteInstanceField(instanceFieldAccess, instruction.Operand);
                    AddBoogie(p);
                }
                else if (staticFieldAccess != null)
                {
                    var p = boogieGenerator.WriteStaticField(staticFieldAccess, instruction.Operand);
                    AddBoogie(p);

                }
                else if (dereference != null && !Settings.NewAddrModelling)
                {
                    AddBoogie(boogieGenerator.VariableAssignment(dereference.Reference, instruction.Operand));
                }
                else if (dereference != null && Settings.NewAddrModelling)
                {
                    var address = new AddressExpression(dereference.Type, boogieGenerator.ReadAddr(dereference.Reference).Expr);
                    AddBoogie(boogieGenerator.WriteAddr(address, boogieGenerator.ReadAddr(instruction.Operand)));
                }
                else
                    throw new NotImplementedException();
            }

            private void ProcessAs(ConvertInstruction instruction)
            {
                //addLabel(instruction);
                var source = instruction.Operand;
                var dest = instruction.Result;
                var type = instruction.ConversionType;
                MentionedClasses.Add(type);
                Contract.Assume(!source.Type.TypeCode.Equals(PrimitiveTypeCode.String));

                var bg = boogieGenerator;
                AddBoogie(bg.VariableAssignment(dest, Expression.As(boogieGenerator.ReadAddr(source), type)));
            }

            private void ProcessTypeConversion(ConvertInstruction instruction)
            {
                var bg = boogieGenerator;
                AddBoogie(bg.HavocResult(instruction));
            }
            public override void Visit(ConvertInstruction instruction)
            {
                if (instruction.Operation == ConvertOperation.Conv)
                {
                    ProcessTypeConversion(instruction);
                } else if (instruction.Operation == ConvertOperation.Box)
                {
                    AddBoogie(boogieGenerator.BoxFrom(instruction.Operand, instruction.Result));
                } else if (instruction.Operation == ConvertOperation.Unbox)
                {
                    if (!Helpers.IsBoogieRefType(instruction.ConversionType))
                        AddBoogie(boogieGenerator.VariableAssignment(instruction.Result, Expression.Union2PrimitiveType(Helpers.GetBoogieType(instruction.ConversionType), boogieGenerator.ReadAddr(instruction.Operand))));
                    else
                    {
                        AddBoogie(boogieGenerator.VariableAssignment(instruction.Result, instruction.Operand));
                    }
                }
                else 
                {
                    ProcessAs(instruction);
                }
            }

            public override void Visit(InitializeObjectInstruction instruction)
            {
                //addLabel(instruction);
                Contract.Assume(instruction.Variables.Count == 1);
                foreach (var var in instruction.Variables)
                {
                    LoadInstruction loadInstruction = this.instTranslator.lastInstruction as LoadInstruction;
                    Contract.Assume(loadInstruction != null);
                    IValue where = null;
                    if(loadInstruction.Operand is Reference)
                    {
                        Reference reference = (Reference)loadInstruction.Operand;
                        //IAssignableValue where = reference.Value as IAssignableValue;
                        where = reference.Value;
                    }
                    else
                    {
                        where = loadInstruction.Operand;
                    }
                    Contract.Assume(where != null);

                    var instanceFieldAccess = where as InstanceFieldAccess; // where it is stored

                    if (instanceFieldAccess != null)
                    {
                        String fieldName = FieldTranslator.GetFieldName(instanceFieldAccess.Field);

                        var bg = boogieGenerator;
                        var desiredTyped = instanceFieldAccess.Field.Type;
                        AddBoogie(bg.WriteInstanceField(instanceFieldAccess, Expression.NullOrZero(desiredTyped)));
                    }
                    else 
                    {
                        // static fields are considered global variables
                        var staticFieldAccess = where as StaticFieldAccess;
                        if (staticFieldAccess != null)
                        {
                            var desiredTyped = staticFieldAccess.Field.Type;
                            AddBoogie(boogieGenerator.WriteStaticField(staticFieldAccess, Expression.NullOrZero(desiredTyped)));
                        }
                        else if(where is IVariable v)
                        {
                            // Diego BUG BUG
                            // We need to handle default(T) properly
                            var desiredTyped = v.Type;
                            AddBoogie(boogieGenerator.VariableAssignment(where as IVariable, Expression.NullOrZero(desiredTyped)));
                        }
                        else
                        {
                            Contract.Assert(false, "Not supported value");
                        }
                    }
                }
            }
        }

        protected class ArrayTranslation : Translation
        {
            public ArrayTranslation(InstructionTranslator p) : base(p)
            {
            }

            // hacky solution - I want to know if we are dealing with only a length instruction or length + convert
            private static bool onlyLengthNoConvert = false;

            public static bool IsArrayTranslation(IList<Instruction> instructions, int idx)
            {
                Instruction ins = instructions[idx];

                if (ins is CreateArrayInstruction)
                    return true;

                var load = ins as LoadInstruction;
                if (load != null && (load.Operand is ArrayElementAccess))
                    return true;

                // Length Access returns an uint
                // we will avoid the conversion from uint to int by directly returning the length

                // Load/Length Convert
                if (load != null && (load.Operand is ArrayLengthAccess) &&
                    idx+1 <= instructions.Count-1 && instructions[idx+1] is ConvertInstruction)
                    return true;

                // Load/Length Convert
                if (ins is ConvertInstruction && idx-1 >= 0 && instructions[idx-1] is LoadInstruction)
                {
                    var op = (instructions[idx - 1] as LoadInstruction).Operand as ArrayLengthAccess;
                    if (op != null)
                        return true;
                }

                // no convert instruction is also acceptable.
                if (load != null && (load.Operand is ArrayLengthAccess))
                {
                    onlyLengthNoConvert = true;
                    return true;
                }

                var store = ins as StoreInstruction;
                if (store != null && (store.Result is ArrayElementAccess /*|| store.Result is ArrayLengthAccess*/))
                    return true;

                return false;
            }

            public override void Visit(CreateArrayInstruction instruction)
            {
                /*
                    call $tmp0 := Alloc();
                    assume $ArrayLength($tmp0) == 1 * 510;
                    assume (forall $tmp1: int :: $ArrayContents[$tmp0][$tmp1] == null);
                */
                AddBoogie(boogieGenerator.AllocObject(instruction.Result, instTranslator));
                AddBoogie(boogieGenerator.AssumeArrayLength(boogieGenerator.ReadAddr(instruction.Result), string.Join<String>(" * ", instruction.UsedVariables.ToArray().Select(v => boogieGenerator.ReadAddr(v).Expr))));
                AddBoogie(boogieGenerator.Assume(String.Format("(forall $tmp1: int :: $ArrayContents[{0}][$tmp1] == null)", boogieGenerator.ReadAddr(instruction.Result).Expr)));
            }

            private static LoadInstruction arrayLengthAccess = null;

            public override void Visit(ConvertInstruction instruction)
            {
                Contract.Assert(arrayLengthAccess != null);

                var op = arrayLengthAccess.Operand as ArrayLengthAccess;
                AddBoogie(boogieGenerator.VariableAssignment(instruction.Result, Expression.ArrayLength(boogieGenerator.ReadAddr(op.Instance))));
                arrayLengthAccess = null;
            }

            public override void Visit(LoadInstruction instruction)
            {
                ArrayElementAccess elementAccess = instruction.Operand as ArrayElementAccess;
                ArrayLengthAccess lengthAccess = instruction.Operand as ArrayLengthAccess;

                if (elementAccess != null || lengthAccess != null)
                {
                    var receiverObject = lengthAccess != null ? lengthAccess.Instance : elementAccess.Array;
                    var refNotNullStr = String.Format("{{:nonnull}} {0} != null", boogieGenerator.ReadAddr(receiverObject).Expr);
                    if (Settings.CheckNullDereferences)
                    {
                        AddBoogie(boogieGenerator.Assert(refNotNullStr));
                    }
                    else
                    {
                        AddBoogie(boogieGenerator.Assume(refNotNullStr));
                    }
                }

                if (lengthAccess != null)
                {
                    if (!onlyLengthNoConvert)
                    {
                        // Length Access returns an uint
                        // we will avoid the conversion from ptr to int by directly returning the length
                        // check ConvertInstruction
                        arrayLengthAccess = instruction;
                    }
                    else
                    {
                        AddBoogie(boogieGenerator.VariableAssignment(instruction.Result, Expression.ArrayLength(boogieGenerator.ReadAddr(lengthAccess.Instance))));
                        onlyLengthNoConvert = false;
                    }

                    return;
                }

                if (elementAccess != null){

                    // multi-dimensional arrays (note that it is not an array of arrays)
                    // analysis-net is not working correctly with them
                    Contract.Assert(elementAccess.Indices.Count == 1);

                    var argType = Helpers.GetBoogieType(elementAccess.Type);
                    ReadArrayContent(instruction.Result, elementAccess.Array, elementAccess.Indices, argType);
                    if (Helpers.IsBoogieRefType(instruction.Result.Type))
                        AddBoogie(boogieGenerator.Assume(boogieGenerator.Subtype(boogieGenerator.DynamicType(instruction.Result), elementAccess.Type)));
                    return;
                }
            }

            // result is the original result variable of the instruction
            private void ReadArrayContent(IVariable insResult, IVariable array, IList<IVariable> indexes, Helpers.BoogieType boogieType)
            {
                var arrayExpr = boogieGenerator.ReadAddr(array);
                var firstIndexExpr = boogieGenerator.ReadAddr(indexes.First());
                if (indexes.Count == 1)
                {
                    // we access the last array of the chain - this one returns the element we want
                    // that element could be a Union (Ref alias) or a non Union type. In the last case we must perform a cast.
                    if (Helpers.IsBoogieRefType(boogieType))
                    {
                        var resExpr = boogieGenerator.ReadAddr(insResult);
                        AddBoogie(boogieGenerator.CallReadArrayElement(insResult, arrayExpr, firstIndexExpr, instTranslator));
                    }
                    else
                    {
                        // Store Union element and then cast it to the correct type

                        var tempBoogieVar = instTranslator.GetFreshVariable(Helpers.GetBoogieType(Types.Instance.PlatformType.SystemObject));
                        AddBoogie(boogieGenerator.CallReadArrayElement(tempBoogieVar, arrayExpr, firstIndexExpr));
                        AddBoogie(boogieGenerator.VariableAssignment(insResult, Expression.Union2PrimitiveType(boogieType, tempBoogieVar)));
                    }
                } else
                {
                    var tempBoogieVar = instTranslator.GetFreshVariable(Helpers.GetBoogieType(Types.Instance.PlatformType.SystemObject));
                    var tempVar = AddNewLocalVariableToMethod("$arrayElement", Types.Instance.PlatformType.SystemObject);
                    AddBoogie(boogieGenerator.CallReadArrayElement(tempBoogieVar, arrayExpr, firstIndexExpr));
                    ReadArrayContent(insResult, tempVar, indexes.Skip(1).ToList(), boogieType);
                }
            }

            public override void Visit(StoreInstruction instruction)
            {
                // multi-dimensional arrays are not supported yet - analysis-net is not working correctly
                ArrayElementAccess res = instruction.Result as ArrayElementAccess;

                Contract.Assert(res != null);

                var receiverObject = res.Array;
                var refNotNullStr = String.Format("{{:nonnull}} {0} != null", boogieGenerator.ReadAddr(receiverObject).Expr);

                if (Settings.CheckNullDereferences)
                {
                    AddBoogie(boogieGenerator.Assert(refNotNullStr));
                }
                else
                {
                    AddBoogie(boogieGenerator.Assume(refNotNullStr));
                }
                var arrayExpr = boogieGenerator.ReadAddr(res.Array);
                var firstIndexExpr = boogieGenerator.ReadAddr(res.Indices[0]);
                if (!Helpers.IsBoogieRefType(res.Type)) // Ref and Union are alias
                {
                    /*
                        assume Union2Int(Int2Union(0)) == 0;
                        $ArrayContents := $ArrayContents[$ArrayContents[a][1] := $ArrayContents[$ArrayContents[a][1]][1 := Int2Union(0)]];
                    */
                    AddBoogie(boogieGenerator.AssumeInverseRelationUnionAndPrimitiveType(boogieGenerator.ReadAddr(instruction.Operand)));
                    AddBoogie(boogieGenerator.CallWriteArrayElement(arrayExpr, firstIndexExpr, Expression.PrimitiveType2Union(boogieGenerator.ReadAddr(instruction.Operand))));
                }
                else
                    AddBoogie(boogieGenerator.CallWriteArrayElement(arrayExpr, firstIndexExpr, boogieGenerator.ReadAddr(instruction.Operand)));
            }
        }

        protected class ExceptionTranslation : Translation
        {
            public ExceptionTranslation(InstructionTranslator p) : base(p)
            {
            }

            public static bool IsExceptionTranslation(IList<Instruction> instructions, int idx)
            {
                if (!Settings.Exceptions)
                    return false;

                // hack to get endfinally
                var nopInstruction = instructions[idx] as NopInstruction;
                if (nopInstruction != null)
                {
                    return nopInstruction.IsEndFinally;
                }

                var unconditionalBranchInstruction = instructions[idx] as UnconditionalBranchInstruction;
                if (unconditionalBranchInstruction != null)
                {
                    return unconditionalBranchInstruction.IsLeaveProtectedBlock;
                }

                if (instructions[idx] is ThrowInstruction || instructions[idx] is CatchInstruction || instructions[idx] is FinallyInstruction)
                    return true;

                return false;
            }

            public override void Visit(NopInstruction instruction)
            {
                /*
                 The endfinally instruction transfers control out of a finally or fault block in the usual manner. 
                 This mean that if the finally block was being executed as a result of a leave statement in a try block, 
                 then execution continues at the next statement following the finally block. 
                 If, on the other hand, the finally block was being executed as a result of an exception having been thrown, 
                 then execution will transfer to the next suitable block of exception handling code. 

                    taken from: "Expert .NET 1.1 Programming"
                 */

                // only encode behaviour if there was an unhandled exception
                var target = GetThrowTarget(instruction);
                var ifBody = String.IsNullOrEmpty(target) ? boogieGenerator.Return() : boogieGenerator.Goto(target);
                AddBoogie(boogieGenerator.If("$Exception != null", ifBody));
            }

            public override void Visit(UnconditionalBranchInstruction instruction)
            {
                //if (instruction.IsLeaveProtectedBlock)
                //{
                    // this is a special goto statement
                    // it is used to exit a try or a catch

                    // we should check if there is a finally statement in the same try where this instruction is located
                    // if there is one, we jump to it.

                    // remember that there can be a finally when there are no catches or viceversa

                    var branchOffset = instruction.Offset;

                    // check if it is inside a catch handler
                    var catchContaining = from pb in instTranslator.methodBody.ExceptionInformation
                                          where
                                             branchOffset >= Convert.ToInt32(pb.Handler.Start.Substring(2), 16)
                                             && branchOffset <= Convert.ToInt32(pb.Handler.End.Substring(2), 16)
                                             && pb.Handler.Kind == ExceptionHandlerBlockKind.Catch
                                          orderby Convert.ToInt32(pb.Start.Substring(2), 16) descending
                                          select pb;

                    // check if it is in a try
                    var tryContaining = from pb in instTranslator.methodBody.ExceptionInformation
                                        where
                                           branchOffset >= Convert.ToInt32(pb.Start.Substring(2), 16)
                                           && branchOffset <= Convert.ToInt32(pb.End.Substring(2), 16)
                                        //&& pb.Handler.Kind != ExceptionHandlerBlockKind.Catch
                                        orderby Convert.ToInt32(pb.Start.Substring(2), 16) descending
                                        select pb;

                    ProtectedBlock containingBlock = null;

                    if (catchContaining.Count() > 0 && tryContaining.Count() == 0)
                        containingBlock = catchContaining.First();
                    else if (catchContaining.Count() == 0 && tryContaining.Count() > 0)
                        containingBlock = tryContaining.First();
                    else if (catchContaining.Count() > 0 && tryContaining.Count() > 0)
                    {
                        var cStart = Convert.ToInt32(catchContaining.First().Start.Substring(2), 16);
                        var tStart = Convert.ToInt32(tryContaining.First().Start.Substring(2), 16);
                        containingBlock = cStart > tStart ? catchContaining.First() : tryContaining.First();
                    }

                    // we know where the protected block starts, we look for a finally handler in the same level.
                    var target = from pb in instTranslator.methodBody.ExceptionInformation
                                 where
                                     pb.Start == containingBlock.Start && pb.Handler.Kind == ExceptionHandlerBlockKind.Finally
                                 orderby Convert.ToInt32(pb.Start.Substring(2), 16) descending
                                 select pb.Handler.Start;

                    if (target.Count() > 0) // is there a finally?
                    {
                        AddBoogie(boogieGenerator.Goto(target.First()));
                        return;
                    }

                //}
                AddBoogie(boogieGenerator.Goto(instruction.Target));
            }

            public override void Visit(TryInstruction instruction)
            {
                // nothing is done for this type of instruciton
            }

            public override void Visit(CatchInstruction instruction)
            {
                // we jump to next catch handler, finally handler or exit method with return.
                var nextHandler = GetNextHandlerIfCurrentCatchNotMatch(instruction);//GetNextExceptionHandlerLabel(instTranslator.methodBody.ExceptionInformation, instruction.Label);
                var body = String.IsNullOrEmpty(nextHandler) ? boogieGenerator.Return() : boogieGenerator.Goto(nextHandler);
                AddBoogie(boogieGenerator.If(boogieGenerator.Negation(boogieGenerator.Subtype("$ExceptionType", instruction.ExceptionType)), body));

                // Exception is handled we reset global variables
                if (instruction.HasResult) // catch with no specific exception type
                    AddBoogie(boogieGenerator.VariableAssignment(instruction.Result, BoogieVariable.ExceptionVar));
                AddBoogie(boogieGenerator.VariableAssignment(BoogieVariable.ExceptionInCatchHandlerVar, BoogieVariable.ExceptionVar));
                AddBoogie(boogieGenerator.VariableAssignment(BoogieVariable.ExceptionInCatchHandlerTypeVar, BoogieVariable.ExceptionTypeVar));
                AddBoogie(boogieGenerator.VariableAssignment(BoogieVariable.ExceptionVar, boogieGenerator.NullObject()));
                AddBoogie(boogieGenerator.VariableAssignment(BoogieVariable.ExceptionTypeVar, boogieGenerator.NullObject()));
            }

            public override void Visit(ThrowInstruction instruction)
            {
                if (instruction.HasOperand)
                {
                    var args = new List<string>();
                    args.Add(boogieGenerator.ReadAddr(instruction.Operand).Expr);
                    AddBoogie(boogieGenerator.ProcedureCall("System.Object.GetType", args, "$ExceptionType"));
                    AddBoogie(boogieGenerator.VariableAssignment(BoogieVariable.ExceptionVar, boogieGenerator.ReadAddr(instruction.Operand)));
                }
                else
                {
                    // rethrow
                    AddBoogie(boogieGenerator.VariableAssignment(BoogieVariable.ExceptionTypeVar, BoogieVariable.ExceptionInCatchHandlerTypeVar));
                    AddBoogie(boogieGenerator.VariableAssignment(BoogieVariable.ExceptionVar, BoogieVariable.ExceptionInCatchHandlerVar));
                }

                var target = GetThrowTarget(instruction);

                if (String.IsNullOrEmpty(target))
                    AddBoogie(boogieGenerator.Return());
                else
                    AddBoogie(boogieGenerator.Goto(target));
            }

            public override void Visit(FinallyInstruction instruction)
            {
                // we need to modify the last instruction
            }

            // if $Exception is not null, the method call instruction is handled as if it was a throw instruction
            // nearest catch handler is searched otherwise exit method.
            public static string HandleExceptionAfterMethodCall(Instruction ins)
            {
                if (!Settings.Exceptions)
                    return string.Empty;

                var sb = new StringBuilder();
                sb.AppendLine("\t\tif ($Exception != null)");
                sb.AppendLine("\t\t{");
                var label = GetThrowTarget(ins);
                if (String.IsNullOrEmpty(label))
                    sb.AppendLine("\t\t\treturn;");
                else
                    sb.AppendLine(String.Format("\t\t\tgoto {0};", label));
                sb.AppendLine("\t\t}");
                return sb.ToString();
            }

            // retrieves all succesors blocks of the block that contains the throw instruction (it could be a method call instruction)
            // we want to jump to the nearest catch handler, so we get the block with the nearest label.
            // if there is none, we should exit the method
            protected static string GetThrowTarget(Instruction instruction)
            {
                var node = Traverser.CFG.Nodes.Where((x => x.Instructions.Contains(instruction))).First();
                var successors = node.Successors.Where(block => block.Instructions.Any(ins => ins is CatchInstruction || ins is FinallyInstruction) && block.Instructions.First().Label != null);

                if (successors.Count() == 0)
                {
                    return string.Empty;
                }

                var offsetToBlock = successors.ToLookup(block => block.Instructions.First().Offset);
                var minOffset = successors.Select(block => block.Instructions.First().Offset).Min();

                return offsetToBlock[minOffset].First().Instructions.First().Label;
            }

            // next handler if current catch is not compatible with the exception type
            protected  string GetNextHandlerIfCurrentCatchNotMatch(Instruction instruction)
            {
                var node = Traverser.CFG.Nodes.Where((x => x.Instructions.Contains(instruction))).First();

                // predecessors of a catch is a try - exceptional cfg add edges to the block that make a try not the catch blocks.
                var successors = node.Predecessors.SelectMany(n => n.Successors).Where(n => n != node && n.Kind != CFGNodeKind.Exit);

                var possibleHandlers = successors.Where(block => block.Instructions.Any(ins => ins is CatchInstruction || ins is FinallyInstruction)
                                                                            && block.Instructions.First().Label != null && // is target of something
                                                                            block.Instructions.First().Offset > instruction.Offset); // handlers after this one.

                if (possibleHandlers.Count() == 0)
                {
                    return string.Empty;
                }

                var offsetToBlock = possibleHandlers.ToLookup(block => block.Instructions.First().Offset);
                var minOffset = possibleHandlers.Select(block => block.Instructions.First().Offset).Min();

                return offsetToBlock[minOffset].First().Instructions.First().Label;
            }
        }

        // it is triggered when Load, CreateObject and MethodCall instructions are seen in this order.
        // Load must be of a static or virtual method (currently only static is supported)
        class DelegateCreationTranslation : Translation
        {
            private static bool IsMethodReference(Instruction ins)
            {
                LoadInstruction loadIns = ins as LoadInstruction;
                if (loadIns != null)
                    return loadIns.Operand is StaticMethodReference || loadIns.Operand is VirtualMethodReference;
                return false;
            }

            // decides if the DelegateTranslation state is set for instruction translation
            public static bool IsDelegateCreationTranslation(IList<Instruction> instructions, int idx)
            {
                // we are going to read LOAD CREATE METHOD
                if (idx + 2 <= instructions.Count - 1)
                {
                    if (instructions[idx] is LoadInstruction &&
                        instructions[idx + 1] is CreateObjectInstruction &&
                        instructions[idx + 2] is MethodCallInstruction)
                    {
                        if (IsMethodReference(instructions[idx]))
                            return true;
                    }
                }

                // we have just read LOAD, we are going to read CREATE and METHOD
                if (idx - 1 >= 0 && idx + 1 <= instructions.Count - 1)
                {
                    if (instructions[idx - 1] is LoadInstruction &&
                        instructions[idx] is CreateObjectInstruction &&
                        instructions[idx + 1] is MethodCallInstruction)
                    {
                        if (IsMethodReference(instructions[idx-1]))
                            return true;
                    }
                }

                // we have just read LOAD, CREATE and we are going to read METHOD
                if (idx - 2 >= 0)
                {
                    if (instructions[idx - 2] is LoadInstruction &&
                        instructions[idx - 1] is CreateObjectInstruction &&
                        instructions[idx] is MethodCallInstruction)
                    {
                        if (IsMethodReference(instructions[idx-2]))
                            return true;
                    }
                }

                return false;
            }

            private static LoadInstruction loadIns = null;
            private static CreateObjectInstruction createObjIns = null;

            public DelegateCreationTranslation(InstructionTranslator p) : base(p) {}

            public override void Visit(LoadInstruction instruction)
            {
                Contract.Assert(instruction.Operand is StaticMethodReference || instruction.Operand is VirtualMethodReference);
                loadIns = instruction;

                instTranslator.RemovedVariables.UnionWith(instruction.ModifiedVariables);

                // continues in CreateObjectInstruction
            }

            public override void Visit(CreateObjectInstruction instruction)
            {
                // we ensure that before this instruction there was a load instruction
                //Contract.Assert(loadIns != null);

                createObjIns = instruction;

                /*var loadDelegateStmt = loadIns.Operand as StaticMethodReference;
                var methodRef = loadDelegateStmt.Method;
                var methodId = DelegateStore.GetMethodIdentifier(methodRef);

                // do we have to type this reference?
                AddBoogie(String.Format("\t\tcall {0}:= CreateDelegate({1}, {2}, {3});", instruction.Result, methodId, "null", "Type0()"));*/
                // continues in MethodCallInstruction
            }

            public override void Visit(MethodCallInstruction instruction)
            {
                Contract.Assert(loadIns != null && createObjIns != null);
                //addLabel(instruction);

                // it can be VirtualMethodReference or StaticMethodReference
                var loadDelegateStmt = (loadIns.Operand as IFunctionReference) ;
                var methodRef = loadDelegateStmt.Method;
                IVariable receiverObject = instruction.Arguments[1];

                Func<IMethodReference, string> d = (potentialMethod) =>
                {
                    var sb = new StringBuilder();

                    var methodId = DelegateStore.GetMethodIdentifier(potentialMethod);

                    // instruction.method.containingtype is the instantiated type
                    // i group them by their uninstanciated type
                    DelegateStore.AddDelegatedMethodToGroup(instruction.Method.ContainingType, potentialMethod);
                    // invoke the correct version of create delegate
                    var normalizedType = Helpers.GetNormalizedTypeForDelegates(instruction.Method.ContainingType);
                    var arguments = new List<string>();
                    arguments.Add(methodId);
                    arguments.Add(boogieGenerator.ReadAddr(receiverObject).Expr);
                    arguments.Add("Type0()");

                    var freshVar = instTranslator.GetFreshVariable(Helpers.GetBoogieType(createObjIns.Result.Type));
                    sb.AppendLine(boogieGenerator.ProcedureCall(String.Format("CreateDelegate_{0}", normalizedType), arguments, freshVar.Expr));
                    // This is a hack.
                    // The first parameter below <createObjIns.Result> (which is an IVariable), when using the new address model, will be used as the address
                    // whereas the second parameter below, <createObjIns.Result.Name> (which is a string) will be copied syntactically.
                    // For example:
                    // memoryInt := WriteInt(memoryInt, _var1, var1); // first one with underscore, second without (this is intentional)
                    sb.AppendLine(boogieGenerator.VariableAssignment(createObjIns.Result, freshVar));
                    sb.AppendLine(ExceptionTranslation.HandleExceptionAfterMethodCall(instruction));

                    return sb.ToString();
                };

                DynamicDispatch(methodRef, receiverObject, methodRef.IsStatic ? MethodCallOperation.Static : MethodCallOperation.Virtual, d);

                loadIns = null;
                createObjIns = null;
            }
        }

        class DelegateInvokeTranslation : Translation
        {
            public DelegateInvokeTranslation(InstructionTranslator p) : base(p) {}

            public override void Visit(MethodCallInstruction instruction)
            {
                //addLabel(instruction);

                // this local variable will hold the InvokeDelegate result 
                // the intent is to translate its type to Union (or Ref they are alias)
                // note: analysis-net changed and required to pass a method reference in the LocalVariable constructor
                var boogieVar = instTranslator.GetFreshVariable(Helpers.GetBoogieType(Types.Instance.PlatformType.SystemObject));

                // create if it doesnt exist yet
                // i want the specialized type
                DelegateStore.CreateDelegateGroup(instruction.Method.ContainingType);

                foreach (var argument in instruction.Arguments.Skip(1)) // first argument is the delegate object
                {
                    if (Helpers.IsBoogieRefType(argument.Type)) // Ref and Union are alias
                        continue;

                    AddBoogie(boogieGenerator.AssumeInverseRelationUnionAndPrimitiveType(boogieGenerator.ReadAddr(argument)));
                    //AddBoogie(String.Format("\t\tassume Union2{0}({0}2Union({1})) == {1};", argType, argument));
                }

                //AddBoogie(String.Format("\t\tassume Union2Int(Int2Union({0})) == {0};", instruction.Arguments[1]));

                /* 
                    // example of desired code
                    var $res_invoke : Union;
                    L_0000:
                    L_0002:
                        $r1 := 1;
                    L_0003:
                        assume Union2Int(Int2Union(1)) == 1; // depends on the delegate type
                        call $res_invoke := InvokeDelegate(f,Int2Union($r1)); -> there can be more than 1 argument
                        r := Union2Int($res_invoke);
                */

                var invokeDelegateArguments = new List<string>();
                invokeDelegateArguments.Add(instruction.Arguments[0].Name);
                foreach (var argument in instruction.Arguments.Skip(1))
                {
                    if (Helpers.IsBoogieRefType(argument.Type)) // Ref and Union are alias
                        invokeDelegateArguments.Add(argument.ToString());
                    else
                        invokeDelegateArguments.Add(Expression.PrimitiveType2Union(boogieGenerator.ReadAddr(argument)).Expr);
                }

                //var arguments = arguments2Union.Count > 0 ? "," + String.Join(",",arguments2Union) : String.Empty;
                //var argumentsString = string.Join(",", invokeDelegateArguments.ToArray());
                // invoke the correct version of invoke delegate
                var normalizedType = Helpers.GetNormalizedTypeForDelegates(instruction.Method.ContainingType);
                if (instruction.HasResult)
                    AddBoogie(boogieGenerator.ProcedureCall(string.Format("InvokeDelegate_{0}", normalizedType), invokeDelegateArguments, boogieVar));
                else
                    AddBoogie(boogieGenerator.ProcedureCall(string.Format("InvokeDelegate_{0}", normalizedType), invokeDelegateArguments));

                if (instruction.HasResult)
                {
                    // the union depends on the type of the arguments
                    var argType = Helpers.GetBoogieType(instruction.Result.Type);
                    if (Helpers.IsBoogieRefType(instruction.Result.Type)) // Ref and Union are alias
                    {
                        // we handle delegate invokations as extern for simplicity
                        // they may point to an extern method
                        AddBoogie(AddSubtypeInformationToExternCall(instruction));
                        AddBoogie(boogieGenerator.VariableAssignment(instruction.Result, boogieVar));
                    }
                    else
                        AddBoogie(boogieGenerator.VariableAssignment(instruction.Result, Expression.Union2PrimitiveType(argType, boogieVar)));
                }
            }

            public static bool IsDelegateInvokation(MethodCallInstruction instruction)
            {
                if (instruction.Method.ContainingType.ResolvedType.IsDelegate &&
                    instruction.Method.Name.Value.Equals("Invoke")) // better way?
                    return true;
                return false;
            }
            public static bool IsDelegateInvokeTranslation(IList<Instruction> instructions, int idx)
            {
                MethodCallInstruction instruction = instructions[idx] as MethodCallInstruction;
                if (instruction == null)
                    return false;

                return IsDelegateInvokation(instruction);
            }
        }
    }

    public class DelegateStore
    {
        internal static IDictionary<IMethodReference, string> methodIdentifiers =
                new Dictionary<IMethodReference, string>();

        // we use a string as a key because hashing ITypeReference is not working as expected

        public static IDictionary<string, ISet<IMethodReference>> MethodGrouping
            = new Dictionary<string, ISet<IMethodReference>>();

        public static IDictionary<string, ITypeReference> MethodGroupType
            = new Dictionary<string, ITypeReference>();

        // this method will receive potential calles which have their types uninstanciated
        public static void AddDelegatedMethodToGroup(ITypeReference tRef, IMethodReference mRef)
        {
            var k = Helpers.GetNormalizedTypeForDelegates(tRef);
            
            if (MethodGrouping.ContainsKey(k))
                MethodGrouping[k].Add(mRef);
            else
            {
                MethodGrouping[k] = new HashSet<IMethodReference>();
                MethodGrouping[k].Add(mRef);
                MethodGroupType[k] = tRef;
            }
        }

        public static  void CreateDelegateGroup(ITypeReference tRef)
        {
            var k = Helpers.GetNormalizedTypeForDelegates(tRef);
            if (MethodGrouping.ContainsKey(k))
                return;

            MethodGrouping[k] = new HashSet<IMethodReference>();
            MethodGroupType[k] = tRef;
        }

        public static string CreateDelegateMethod(string typeRef)
        {
            var sb = new StringBuilder();
            var normalizedType = typeRef;

            sb.AppendLine(String.Format("procedure {{:inline 1}} CreateDelegate_{0}(Method: int, Receiver: Ref, TypeParameters: Ref) returns(c: Ref);", normalizedType));
            sb.AppendLine(String.Format("implementation {{:inline 1}} CreateDelegate_{0}(Method: int, Receiver: Ref, TypeParameters: Ref) returns(c: Ref)", normalizedType));
            sb.AppendLine("{");
            sb.AppendLine("     call c := Alloc();");
            sb.AppendLine("     assume $RefToDelegateReceiver(Method, c) == Receiver;");
            sb.AppendLine("     assume $RefToDelegateTypeParameters(Method, c) == TypeParameters;");

            foreach (var method in MethodGrouping[typeRef])
            {
                var id = methodIdentifiers[method];
                sb.AppendLine(String.Format("     assume $RefToDelegateMethod({0}, c) <==> Method == {0};", id));
            }

            sb.AppendLine("}");

            return sb.ToString();
        }

        public static string CreateDelegateMethod()
        {
            var sb = new StringBuilder();

            foreach (var typeRef in MethodGrouping.Keys)
                sb.AppendLine(CreateDelegateMethod(typeRef));

            return sb.ToString();
        }

        public static string InvokeDelegateMethod()
        {
            var sb = new StringBuilder();

            foreach (var typeRef in MethodGrouping.Keys)
                sb.AppendLine(InvokeDelegateMethod(typeRef));

            return sb.ToString();
        }

        public static string InvokeDelegateMethod(string groupTypeString)
        {
            var sb = new StringBuilder();

            var invokeArity = MethodGroupType[groupTypeString].ResolvedType.Methods.Single(m => m.Name.ToString().Equals("Invoke"));
            
            // we should parametrize the parameters and return's types
            // currently we are only supporting static int -> int methods
            var argInList = new List<string>();
            bool hasReturnVariable = invokeArity.Type.TypeCode != PrimitiveTypeCode.Void;
            // for virtual methods we may have to change this to include the reference to the object receiving the message
            var parameters = invokeArity.ParameterCount > 0 ? ","+ String.Join(",", invokeArity.Parameters.Select(v => String.Format("arg{0}$in", v.Index) + " : Ref")) : String.Empty;

            sb.AppendLine(string.Format("procedure {{:inline 1}} InvokeDelegate_{0}($this: Ref{1}) {2};", groupTypeString, parameters, hasReturnVariable ? "returns ($r: Ref)" : string.Empty));
            sb.AppendLine(string.Format("implementation {{:inline 1}} InvokeDelegate_{0}($this: Ref{1}) {2}", groupTypeString, parameters, hasReturnVariable ? "returns ($r: Ref)" : string.Empty));
            sb.AppendLine("{");

            // we declare a local for each parameter - local will not be union, will be the real type (boogie version)
            // the resultRealType variable is for the return value if any - then it will be casted to Union and will be the return value
            foreach (var v in invokeArity.Parameters)
                sb.AppendLine(String.Format("\tvar local{0} : {1};", v.Index, Helpers.GetBoogieType(v.Type)));

            if (hasReturnVariable)
                sb.AppendLine(String.Format("\tvar resultRealType: {0};", Helpers.GetBoogieType(invokeArity.Type)));

            foreach (var m in MethodGrouping[groupTypeString])
            {
                var id = methodIdentifiers[m];

                var method = Helpers.GetUnspecializedVersion(m);
                sb.AppendLine(String.Format("\tif ($RefToDelegateMethod({0}, $this))", id));
                sb.AppendLine("\t{");

                // we may have to add the receiver object for virtual methods
                var args = new List<string>();
                if (method.CallingConvention.HasFlag(Microsoft.Cci.CallingConvention.HasThis))
                {
                    var receiverObject = String.Format("$RefToDelegateReceiver({0}, $this)", id);
                    args.Add(receiverObject);
                    var refNotNullStr = String.Format("{{:nonnull}} {0} != null", receiverObject);
                    if (Settings.CheckNullDereferences)
                    {
                        sb.AppendLine(BoogieGenerator.Instance().Assert(refNotNullStr));
                    }
                    else
                    {
                        sb.AppendLine(BoogieGenerator.Instance().Assume(refNotNullStr));
                    }
                }

                // every argument is casted to Union (if they are Ref it is not necessary, they are alias)
                // we need the unspecialized version because that's how generics are handled
                foreach (var v in method.Parameters)
                {
                    var argType = Helpers.GetBoogieType(v.Type);
                    //if (argType.Equals("Ref")) // Ref and Union are alias
                    //    AddBoogie(String.Format("\t\tlocal{0} := arg{0}$in;", v.Index));
                    if (!Helpers.IsBoogieRefType(v.Type))
                    {
                        sb.AppendLine(String.Format("\t\tlocal{0} := Union2{1}(arg{0}$in);", v.Index, argType.FirstUppercase()));
                        args.Add(String.Format("local{0}", v.Index));
                    }
                    else
                    {
                        args.Add(String.Format("arg{0}$in", v.Index));
                    }
                }

                if (hasReturnVariable)
                {
                    if (Helpers.IsBoogieRefType(method.Type))
                    {
                        sb.AppendLine(String.Format("\t\tcall $r := {0}({1});", Helpers.GetMethodName(method), String.Join(",", args)));
                    } else
                    {
                        var argType = Helpers.GetBoogieType(method.Type);
                        if (!Helpers.IsBoogieRefType(method.Type))
                        {
                            sb.AppendLine(String.Format("\t\tcall resultRealType := {0}({1});", Helpers.GetMethodName(method), String.Join(",", args)));
                            sb.AppendLine(String.Format("\t\tassume Union2{0}({0}2Union(resultRealType)) == resultRealType;", argType.FirstUppercase()));
                            sb.AppendLine(String.Format("\t\t$r := {0}2Union(resultRealType);", argType.FirstUppercase()));
                        } else
                        {
                            sb.AppendLine(String.Format("\t\tcall $r := {0}({1});", Helpers.GetMethodName(method), String.Join(",", args)));
                        }

                    }
                } else
                {
                    sb.AppendLine(String.Format("\t\tcall {0}({1});", Helpers.GetMethodName(method), String.Join(",", args)));
                }

                sb.AppendLine("\t\treturn;");
                sb.AppendLine("\t}");
            }

            sb.AppendLine("}");

            return sb.ToString();
        }

        public static string DefineMethodsIdentifiers()
        {
            StringBuilder sb = new StringBuilder();

            foreach (var methodId in methodIdentifiers.Values)
                sb.AppendLine(String.Format("const unique {0}: int;", methodId));

            return sb.ToString();
        }

        // This method has a clone? GetMethodName
        public static string GetMethodIdentifier(IMethodReference methodRef)
        {
            var methodId = Helpers.CreateUniqueMethodName(methodRef);

            if (methodIdentifiers.ContainsKey(methodRef))
                return methodIdentifiers[methodRef];

            //var methodName = Helpers.GetMethodName(methodRef);
            //var methodArity = Helpers.GetArityWithNonBoogieTypes(methodRef);

            //// example:  cMain2.objectParameter$System.Object;
            //var methodId = methodName + methodArity;

            methodIdentifiers.Add(methodRef, methodId);

            return methodId;
        }
    }
}
