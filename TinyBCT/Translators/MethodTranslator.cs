﻿using Backend;
using Backend.Analyses;
using Backend.Model;
using Backend.ThreeAddressCode.Values;
using Backend.Transformations;
using Backend.Utils;
using Microsoft.Cci;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TinyBCT.Translators;

namespace TinyBCT
{
    class MethodTranslator
    {
        IMethodDefinition methodDefinition;
        MethodBody methodBody;
        ClassHierarchyAnalysis CHA;

        public static void transformBody(MethodBody methodBody)
        {
            var cfAnalysis = new ControlFlowAnalysis(methodBody);
            //var cfg = cfAnalysis.GenerateNormalControlFlow();
            Traverser.CFG = cfAnalysis.GenerateExceptionalControlFlow();

            var splitter = new WebAnalysis(Traverser.CFG, methodBody.MethodDefinition);
            splitter.Analyze();
            splitter.Transform();

            methodBody.UpdateVariables();

            var typeAnalysis = new TypeInferenceAnalysis(Traverser.CFG, methodBody.MethodDefinition.Type);
            typeAnalysis.Analyze();

            //var forwardCopyAnalysis = new ForwardCopyPropagationAnalysis(Traverser.CFG);
            //forwardCopyAnalysis.Analyze();
            //forwardCopyAnalysis.Transform(methodBody);

            //var backwardCopyAnalysis = new BackwardCopyPropagationAnalysis(Traverser.CFG);
            //backwardCopyAnalysis.Analyze();
            //backwardCopyAnalysis.Transform(methodBody);

            // TinyBCT transformations

            if (!Settings.NewAddrModelling)
            {
                var refAlias = new RefAlias(methodBody);
                refAlias.Transform();

                var immutableArguments = new ImmutableArguments(methodBody);
                immutableArguments.Transform();

                var fieldInitialization = new FieldInitialization(methodBody);
                fieldInitialization.Transform();
            }
            methodBody.RemoveUnusedLabels();
        }


        static bool whitelistContains(string name)
        {
            if (!Settings.DebugLargeDLL)
            {
                return true;
            }
            if (name.Contains("Analyzer1"))
                return true;
            if (name.Contains("Project"))
                return true;
            if (name.Contains("Diagnostic"))
                return true;

            if (name.Contains("MetadataReference"))
                return true;
            if (name.Contains("Location"))
                return true;
            if (name.Contains("Compilation"))
                return true;
            if (name.Contains("Document"))
                return true;

            return false;
        }
        // called from Traverser
        // set in Main
        public static void IMethodDefinitionTraverse(IMethodDefinition mD, IMetadataHost host, ISourceLocationProvider sourceLocationProvider)
        {

            // TODO: Hack to for treating a method as nondet
            var method = mD.ResolvedMethod;
            var methodName =method.ContainingType.FullName() + "." + method.Name.Value;
            if (methodName.Equals("SVX.ContractBase.getNondet"))
            {
                InstructionTranslator.AddToExternalMethods(method);
                return;
            }
            // End Hack
            
            if (!mD.IsExternal)
            {
                try
                {
                    if (whitelistContains(mD.ContainingType.FullName()))
                    {
                        var disassembler = new Disassembler(host, mD, sourceLocationProvider);
                        MethodBody mB = disassembler.Execute();
                        transformBody(mB);

                        MethodTranslator methodTranslator = new MethodTranslator(mD, mB, Traverser.CHA);
                        // todo: improve this piece of code
                        StreamWriter streamWriter = Program.streamWriter;
                        streamWriter.WriteLine(methodTranslator.Translate());
                        Helpers.addTranslatedMethod(mD);
                    }
                }
                catch (InvalidOperationException ex)
                {
                    Console.WriteLine("WARNING: Exception thrown while translating method (omitting): " + BoogieMethod.From(mD).Name);
                    if (!Settings.SilentExceptionsForMethods)
                    {
                        throw ex;
                    }
                }
            }

        }

        public MethodTranslator(IMethodDefinition methodDefinition, MethodBody methodBody, ClassHierarchyAnalysis CHA)
        {
            this.methodDefinition = methodDefinition;
            this.methodBody = methodBody;
            this.CHA = CHA;
        }

        StatementList TranslateInstructions(out Dictionary<string, BoogieVariable> temporalVariables)
        {
            InstructionTranslator instTranslator = new InstructionTranslator(this.CHA, methodBody);
            instTranslator.Translate();

            foreach (var v in instTranslator.RemovedVariables)
                methodBody.Variables.Remove(v);

            foreach (var v in instTranslator.AddedVariables)
                methodBody.Variables.Add(v);
            temporalVariables = instTranslator.temporalVariables;
            return instTranslator.Boogie();
        }

        StatementList TranslateLocalVariables(Dictionary<string, BoogieVariable> temporalVariables)
        {
            StatementList localVariablesStmts = new StatementList();
            var bg = BoogieGenerator.Instance();

            var allVariables = methodBody.Variables.Union(methodBody.Parameters).ToList();
            localVariablesStmts.Add(bg.DeclareLocalVariables(allVariables, temporalVariables));

            localVariablesStmts.Add(bg.AllocLocalVariables(allVariables));

            // replace for class generated by compiler
            return localVariablesStmts;
        }

        String TranslateAttr()
        {
            // commented entrypoint tag - this is only added to the wrappers
            // check StaticInitializer
            return /*Helpers.IsMain(methodDefinition) ? " {:entrypoint}" :*/ String.Empty;

        }

        String TranslateReturnTypeIfAny()
        {
            if (Settings.NewAddrModelling)
            {
                var returnVariables = new List<String>();
                if (!Helpers.GetMethodBoogieReturnType(methodDefinition).Equals(Helpers.BoogieType.Void))
                    returnVariables.Add(String.Format("$result : {0}", Helpers.GetMethodBoogieReturnType(methodDefinition)));

                return String.Format("returns ({0})", String.Join(",", returnVariables));
            } else
            {
                if (Helpers.GetMethodBoogieReturnType(methodDefinition).Equals(Helpers.BoogieType.Void) &&
                     (!methodDefinition.Parameters.Any(p => p.IsByReference || p.IsOut)))
                    return String.Empty;
                else
                {
                    var returnVariables = new List<String>();
                    returnVariables = methodDefinition.Parameters.Where(p => p.IsByReference).Select(p => String.Format("{0}$out : {1}", p.Name.Value, Helpers.GetBoogieType(p))).ToList();
                    if (!Helpers.GetMethodBoogieReturnType(methodDefinition).Equals(Helpers.BoogieType.Void))
                        returnVariables.Add(String.Format("$result : {0}", Helpers.GetMethodBoogieReturnType(methodDefinition)));

                    return String.Format("returns ({0})", String.Join(",", returnVariables));
                }
            }
        }

        public String Translate()
        {
            // instructions must be translated before local variables
            // modification to local variables can ocurr while instruction translation is done
            // for example when delegate creation is detected some local variables are deleted.
            var ins = TranslateInstructions(out Dictionary<string, BoogieVariable> temporalVariables);
            var localVariables = TranslateLocalVariables(temporalVariables);
            var methodName = BoogieMethod.From(methodDefinition).Name;
            var attr = TranslateAttr();
            var parametersWithTypes = Helpers.GetParametersWithBoogieType(methodDefinition);
            var returnTypeIfAny = TranslateReturnTypeIfAny();

            var boogieProcedureTemplate = new BoogieProcedureTemplate(methodName, attr, localVariables, ins, parametersWithTypes, returnTypeIfAny, Helpers.IsExternal(methodDefinition));
            return boogieProcedureTemplate.TransformText();
        }

    }
}
