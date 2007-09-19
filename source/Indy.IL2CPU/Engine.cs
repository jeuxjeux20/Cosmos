﻿// this file supports the VERBOSE_DEBUG define. this makes it emit a bunch of comments in the assembler output.
// note that the tests are supposed to NOT include these comments
// #define VERBOSE_DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Indy.IL2CPU.Assembler;
using Indy.IL2CPU.Assembler.X86;
using Indy.IL2CPU.IL;
using Indy.IL2CPU.IL.X86;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Instruction = Mono.Cecil.Cil.Instruction;

namespace Indy.IL2CPU {
	public class MethodDefinitionComparer: IComparer<MethodDefinition> {
		#region IComparer<MethodDefinition> Members
		public int Compare(MethodDefinition x, MethodDefinition y) {
			return GenerateFullName(x).CompareTo(GenerateFullName(y));
		}
		#endregion

		private static string GenerateFullName(MethodReference aDefinition) {
			StringBuilder sb = new StringBuilder();
			sb.Append(aDefinition.DeclaringType.FullName + "." + aDefinition.Name);
			sb.Append("(");
			foreach (ParameterDefinition param in aDefinition.Parameters) {
				sb.Append(param.ParameterType.FullName);
				sb.Append(",");
			}
			return sb.ToString().TrimEnd(',') + ")";
		}
	}

	public class FieldDefinitionComparer: IComparer<FieldDefinition> {
		#region IComparer<FieldDefinition> Members
		public int Compare(FieldDefinition x, FieldDefinition y) {
			return GenerateFullName(x).CompareTo(GenerateFullName(y));
		}
		#endregion

		private static string GenerateFullName(FieldDefinition aDefinition) {
			StringBuilder sb = new StringBuilder();
			sb.Append(aDefinition.DeclaringType.FullName + "." + aDefinition.Name);
			return sb.ToString().Trim();
		}
	}

	public delegate void DebugLogHandler(string aMessage);

	public enum TargetPlatformEnum {
		x86
	}

	public class Engine {
		protected static Engine mCurrent;
		protected AssemblyDefinition mCrawledAssembly;
		protected DebugLogHandler mDebugLog;
		protected OpCodeMap mMap;
		protected Assembler.Assembler mAssembler;

		/// <summary>
		/// Contains a list of all methods. This includes methods to be processed and already processed.
		/// </summary>
		protected SortedList<MethodDefinition, bool> mMethods = new SortedList<MethodDefinition, bool>(new MethodDefinitionComparer());
		/// <summary>
		/// Contains a list of all static fields. This includes static fields to be processed and already processed.
		/// </summary>
		protected SortedList<FieldDefinition, bool> mStaticFields = new SortedList<FieldDefinition, bool>(new FieldDefinitionComparer());


		/// <summary>
		/// Compiles an assembly to CPU-specific code. The entrypoint of the assembly will be 
		/// crawled to see what is neccessary, same goes for all dependencies.
		/// </summary>
		/// <remarks>For now, only entrypoints without params and return code are supported!</remarks>
		/// <param name="aAssembly">The assembly of which to crawl the entry-point method.</param>
		/// <param name="aTargetPlatform">The platform to target when assembling the code.</param>
		/// <param name="aOutput"></param>
		public void Execute(string aAssembly, TargetPlatformEnum aTargetPlatform, StreamWriter aOutput) {
			mCurrent = this;
			try {
				if (aOutput == null) {
					throw new ArgumentNullException("aOutput");
				}
				mCrawledAssembly = AssemblyFactory.GetAssembly(aAssembly);
				if (mCrawledAssembly.EntryPoint == null) {
					throw new NotSupportedException("Libraries are not supported!");
				}
				using (mAssembler = new Assembler.X86.Assembler(aOutput)) {
					switch (aTargetPlatform) {
						case TargetPlatformEnum.x86: {
								mMap = new X86OpCodeMap();
								break;
							}
						default:
							throw new NotSupportedException("TargetPlatform '" + aTargetPlatform + "' not supported!");
					}
					mAssembler.OutputType = Indy.IL2CPU.Assembler.Assembler.OutputTypeEnum.Console;
					mMap.Initialize(mAssembler);
					IL.Op.QueueMethod += QueueMethod;
					IL.Op.QueueStaticField += QueueStaticField;
					try {
						mMethods.Add(RuntimeEngineRefs.InitializeApplicationRef, false);
						mMethods.Add(RuntimeEngineRefs.FinalizeApplicationRef, false);
						mMethods.Add(mCrawledAssembly.EntryPoint, false);
						// initialize the runtime engine
						mAssembler.Add(
							new Assembler.X86.Call(new Label(RuntimeEngineRefs.InitializeApplicationRef).Name),
							new Assembler.X86.Call(new Label(mCrawledAssembly.EntryPoint).Name));
						if (mCrawledAssembly.EntryPoint.ReturnType.ReturnType.FullName.StartsWith("System.Void", StringComparison.InvariantCultureIgnoreCase)) {
							mAssembler.Add(new Pushd("0"));
						} else {
							mAssembler.Add(new Pushd("eax"));
						}
						mAssembler.Add(new Assembler.X86.Call(new Label(RuntimeEngineRefs.FinalizeApplicationRef).Name));
						ProcessAllMethods();
						ProcessAllStaticFields();
					} finally {
						mAssembler.Flush();
						IL.Op.QueueMethod -= QueueMethod;
						IL.Op.QueueStaticField -= QueueStaticField;
					}
				}
			} finally {
				mCurrent = null;
			}
		}

		private static uint GetValueTypeSize(TypeReference aType) {
			switch (aType.FullName) {
				case "System.Byte":
				case "System.SByte":
					return 1;
				case "System.UInt16":
				case "System.Int16":
					return 2;
				case "System.UInt32":
				case "System.Int32":
					return 4;
				case "System.UInt64":
				case "System.Int64":
					return 8;
					// for now hardcode IntPtr and UIntPtr to be 32-bit
				case "System.UIntPtr":
				case "System.IntPtr":
					return 4;
			}
			throw new Exception("Unable to determine ValueType size!");
		}

		private void ProcessAllStaticFields() {
			FieldDefinition xCurrentField;
			while ((xCurrentField = (from item in mStaticFields.Keys
									 where !mStaticFields[item]
									 select item).FirstOrDefault()) != null) {
				string xFieldName = xCurrentField.DeclaringType.FullName + "." + xCurrentField.Name;
				OnDebugLog("Processing Static Field '{0}', Constant = '{1}'({2})", xFieldName, xCurrentField.Constant, xCurrentField.Constant == null ? "**NULL**" : xCurrentField.Constant.GetType().FullName);
									 	
				xFieldName = DataMember.GetStaticFieldName(xCurrentField);
								if (xCurrentField.HasConstant) {
									// emit the constant, but first find out how we get it.
									System.Diagnostics.Debugger.Break();
								} else {
									uint xTheSize;
									if(xCurrentField.FieldType.IsValueType) {
										xTheSize = GetValueTypeSize(xCurrentField.FieldType);
									}
									else {
										xTheSize = 4;										
									}
									string xTheData = "";
									for (uint i = 0; i < xTheSize; i++) {
										xTheData += "0,";
									}
									xTheData = xTheData.TrimEnd(',');
									mAssembler.DataMembers.Add(new DataMember(xFieldName, "dd", xTheData));

								}
				mStaticFields[xCurrentField] = true;
			}
		}

		private void ProcessAllMethods() {
			MethodDefinition xCurrentMethod;
			while ((xCurrentMethod = (from item in mMethods.Keys
									  where !mMethods[item]
									  select item).FirstOrDefault()) != null) {
				OnDebugLog("Processing method '{0}'", xCurrentMethod.DeclaringType.FullName + "." + xCurrentMethod.Name);
				MethodInformation xMethodInfo;
				{
					MethodInformation.Variable[] xVars = new MethodInformation.Variable[0];
					int xCurOffset = 0;
					if (xCurrentMethod.HasBody) {
						xVars = new MethodInformation.Variable[xCurrentMethod.Body.Variables.Count];
						foreach (VariableDefinition xVarDef in xCurrentMethod.Body.Variables) {
							int xVarSize = 4;
							xVars[xVarDef.Index] = new MethodInformation.Variable(xCurOffset, xVarSize);
							xCurOffset += xVarSize;
						}
					}
					MethodInformation.Argument[] xArgs = new MethodInformation.Argument[xCurrentMethod.Parameters.Count];
					xCurOffset = 0;
					for (int i = xArgs.Length - 1; i >= 0; i--) {
						int xArgSize = 4;
						xArgs[i] = new MethodInformation.Argument(xArgSize, xCurOffset);
						xCurOffset += xArgSize;
					}
					xMethodInfo = new MethodInformation(new Label(xCurrentMethod).Name, xVars, xArgs, !xCurrentMethod.ReturnType.ReturnType.FullName.Contains("System.Void"));
				}
				IL.Op xOp = GetOpFromType(mMap.MethodHeaderOp, null, xMethodInfo);
				xOp.Assembler = mAssembler;
#if VERBOSE_DEBUG
				string comment = "Method: " + xCurrentMethod + "\r\n";
				if (xCurrentMethod.Body == null) {
					comment += "  (No locals)\r\n";
				} else {
					comment += "  Locals:\r\n";
					foreach (VariableDefinition xVarDef in xCurrentMethod.Body.Variables) {
						comment += String.Format("    [{0}] {1}\r\n", xVarDef.Index, xVarDef.Name);
					}
				}
				comment += "  Args:\r\n";
				foreach (ParameterDefinition xParamDef in xCurrentMethod.Parameters) {
					comment += String.Format("    [{0}] {1}\r\n", xParamDef.Sequence, xParamDef.Name);
				}
				foreach (string s in comment.Trim().Split(new string[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)) {
					mAssembler.Add(new Literal(";" + s));
				}
#endif
				xOp.Assemble();
				// what to do if a method doesn't have a body?
				if (xCurrentMethod.HasBody) {
					// todo: add support for types which need different stack size
					foreach (Instruction xInstruction in xCurrentMethod.Body.Instructions) {
						MethodReference xMethodReference = xInstruction.Operand as MethodReference;
						if (xMethodReference != null) {
							#region add methods so that they get processed
							// TODO: find a more efficient way to get the MethodDefinition from a MethodReference
							AssemblyNameReference xAssemblyNameReference = xMethodReference.DeclaringType.Scope as AssemblyNameReference;
							if (xAssemblyNameReference != null) {
								AssemblyDefinition xReferencedMethodAssembly = mCrawledAssembly.Resolver.Resolve(xAssemblyNameReference);
								if (xReferencedMethodAssembly != null) {
									foreach (ModuleDefinition xModule in xReferencedMethodAssembly.Modules) {
										var xReferencedType = xModule.Types[xMethodReference.DeclaringType.FullName];
										if (xReferencedType != null) {
											var xMethodDef = xReferencedType.Methods.GetMethod(xMethodReference.Name, xMethodReference.Parameters);
											if (xMethodDef != null) {
												QueueMethod(xMethodDef);
											}
											var xCtorDef = xReferencedType.Constructors.GetConstructor(false, xMethodReference.Parameters);
											if (xCtorDef != null) {
												QueueMethod(xCtorDef);
											}
											break;
										}
									}
								}
							} else {
								ModuleDefinition xReferencedModule = xMethodReference.DeclaringType.Scope as ModuleDefinition;
								if (xReferencedModule != null) {
									var xReferencedType = xReferencedModule.Types[xMethodReference.DeclaringType.FullName];
									if (xReferencedType != null) {
										var xMethodDef = xReferencedType.Methods.GetMethod(xMethodReference.Name, xMethodReference.Parameters);
										if (xMethodDef != null) {
											QueueMethod(xMethodDef);
										}
									}
								} else {
									OnDebugLog("Error: Unhandled scope: " + xMethodReference.DeclaringType.Scope == null ? "**NULL**" : xMethodReference.DeclaringType.Scope.GetType().FullName);
								}
							}
							#endregion
						}
						xOp = GetOpFromType(mMap.GetOpForOpCode(xInstruction.OpCode.Code), xInstruction, xMethodInfo);
						xOp.Assembler = mAssembler;
						xOp.Assemble();
					}
				} else {
					if (xCurrentMethod.IsPInvokeImpl) {
						HandlePInvoke(xCurrentMethod, xMethodInfo);
					} else {
						mAssembler.Add(new Literal("; Method not being generated yet, as it's handled by an iCall"));
					}
				}
				xOp = GetOpFromType(mMap.MethodFooterOp, null, xMethodInfo);
				xOp.Assembler = mAssembler;
				xOp.Assemble();
				mMethods[xCurrentMethod] = true;
			}
		}

		private static IL.Op GetOpFromType(Type aType, Instruction aInstruction, MethodInformation aMethodInfo) {
			return (IL.Op)Activator.CreateInstance(aType, aInstruction, aMethodInfo);
		}

		public static void QueueStaticField(FieldDefinition aField) {
			if (mCurrent == null) {
				throw new Exception("ERROR: No Current Engine found!");
			}
			if (!mCurrent.mStaticFields.ContainsKey(aField)) {
				mCurrent.mStaticFields.Add(aField, false);
			}
		}

		public static void QueueStaticField(string aAssembly, string aType, string aField, out string aFieldName) {
			if (mCurrent == null) {
				throw new Exception("ERROR: No Current Engine found!");
			}
			AssemblyDefinition xReferencedFieldAssembly;
			if (String.IsNullOrEmpty(aAssembly) || aAssembly == typeof(RuntimeEngine).Assembly.GetName().FullName) {
				xReferencedFieldAssembly = RuntimeEngineRefs.RuntimeAssemblyDef;
			} else {
				xReferencedFieldAssembly = mCurrent.mCrawledAssembly.Resolver.Resolve(aAssembly);
			}
			if (xReferencedFieldAssembly != null) {
				foreach (ModuleDefinition xModule in xReferencedFieldAssembly.Modules) {
					var xReferencedType = xModule.Types[aType];
					if (xReferencedType != null) {
						var xFieldDef = xReferencedType.Fields.GetField(aField);
						if (xFieldDef != null) {
							QueueStaticField(xFieldDef);
							aFieldName = DataMember.GetStaticFieldName(xFieldDef);
							return;
						}
					}
				}
			}
			throw new Exception("Field not found!");
		}

		public static void QueueStaticField(FieldReference aFieldRef) {
			if (mCurrent == null) {
				throw new Exception("ERROR: No Current Engine found!");
			}
			AssemblyNameReference xAssemblyNameReference = aFieldRef.DeclaringType.Scope as AssemblyNameReference;
			if (xAssemblyNameReference != null) {
				AssemblyDefinition xReferencedFieldAssembly;
				if (xAssemblyNameReference.FullName == typeof(RuntimeEngine).Assembly.GetName().FullName) {
					xReferencedFieldAssembly = RuntimeEngineRefs.RuntimeAssemblyDef;
				} else {
					xReferencedFieldAssembly = mCurrent.mCrawledAssembly.Resolver.Resolve(xAssemblyNameReference);
				}
				if (xReferencedFieldAssembly != null) {
					foreach (ModuleDefinition xModule in xReferencedFieldAssembly.Modules) {
						var xReferencedType = xModule.Types[aFieldRef.DeclaringType.FullName];
						if (xReferencedType != null) {
							var xFieldDef = xReferencedType.Fields.GetField(aFieldRef.Name);
							if (xFieldDef != null) {
								QueueStaticField(xFieldDef);
							}
							break;
						}
					}
				}
			} else {
				ModuleDefinition xReferencedModule = aFieldRef.DeclaringType.Scope as ModuleDefinition;
				if (xReferencedModule != null) {
					var xReferencedType = xReferencedModule.Types[aFieldRef.DeclaringType.FullName];
					if (xReferencedType != null) {
						var xFieldDef = xReferencedType.Fields.GetField(aFieldRef.Name);
						if (xFieldDef != null) {
							QueueStaticField(xFieldDef);
						}
					}
				} else {
					mCurrent.OnDebugLog("Error: Unhandled scope: " + aFieldRef.DeclaringType.Scope == null ? "**NULL**" : aFieldRef.DeclaringType.Scope.GetType().FullName);
				}
			}
		}

		// MtW: 
		//		Right now, we only support one engine at a time per AppDomain. This might be changed
		//		later. See for example NHibernate does this with the ICurrentSessionContext interface
		public static void QueueMethod(MethodDefinition aMethod) {
			if (mCurrent == null) {
				throw new Exception("ERROR: No Current Engine found!");
			}
			if (!mCurrent.mMethods.ContainsKey(aMethod)) {
				mCurrent.mMethods.Add(aMethod, false);
			}
		}

		public static void QueueMethod(string aAssembly, string aType, string aMethod) {
			MethodDefinition xMethodDef;
			QueueMethod(aAssembly, aType, aMethod, out xMethodDef);
		}

		public static void QueueMethod(string aAssembly, string aType, string aMethod, out MethodDefinition aMethodDef) {
			if (mCurrent == null) {
				throw new Exception("ERROR: No Current Engine found!");
			}
			AssemblyDefinition xAssemblyDef;
			if (String.IsNullOrEmpty(aAssembly) || aAssembly == typeof(Engine).Assembly.GetName().FullName) {
				xAssemblyDef = AssemblyFactory.GetAssembly(typeof(Engine).Assembly.Location);
			} else {
				xAssemblyDef = mCurrent.mCrawledAssembly.Resolver.Resolve(aAssembly);
			}
			TypeDefinition xTypeDef = null;
			foreach (ModuleDefinition xModDef in xAssemblyDef.Modules) {
				if (xModDef.Types.Contains(aType)) {
					xTypeDef = xModDef.Types[aType];
					break;
				}
			}
			if (xTypeDef == null) {
				throw new Exception("Type '" + aType + "' not found in assembly '" + aAssembly + "'!");
			}
			// todo: find a way to specify one overload of a method
			int xCount = 0;
			aMethodDef = null;
			foreach (MethodDefinition xMethodDef in xTypeDef.Methods) {
				if (xMethodDef.Name == aMethod) {
					QueueMethod(xMethodDef);
					if (aMethodDef == null) {
						aMethodDef = xMethodDef;
					}
					xCount++;
				}
			}
			foreach (MethodDefinition xMethodDef in xTypeDef.Constructors) {
				if (xMethodDef.Name == aMethod) {
					QueueMethod(xMethodDef);
					xCount++;
				}
			}
			if (xCount == 0) {
				throw new Exception("Method '" + aType + "." + aMethod + "' not found in assembly '" + aAssembly + "'!");
			}
		}

		public event DebugLogHandler DebugLog {
			add {
				mDebugLog += value;
			}
			remove {
				mDebugLog -= value;
			}
		}

		private void OnDebugLog(string aMessage, params object[] args) {
			if (mDebugLog != null) {
				mDebugLog(String.Format(aMessage, args));
			}
		}

		private void HandlePInvoke(MethodDefinition aMethod, MethodInformation aMethodInfo) {
			IL.Op xPInvokeMethodBodyOp = (IL.Op)Activator.CreateInstance(mMap.PInvokeMethodBodyOp, aMethod, aMethodInfo);
			xPInvokeMethodBodyOp.Assembler = mAssembler;
			xPInvokeMethodBodyOp.Assemble();
		}
	}
}