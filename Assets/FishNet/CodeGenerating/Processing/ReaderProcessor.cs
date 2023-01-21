﻿using FishNet.CodeGenerating.Helping.Extension;
using FishNet.Serializing;
using MonoFN.Cecil;
using MonoFN.Cecil.Cil;
using MonoFN.Cecil.Rocks;
using SR = System.Reflection;
using System.Collections.Generic;
using System;
using FishNet.CodeGenerating.ILCore;
using FishNet.CodeGenerating.Extension;

namespace FishNet.CodeGenerating.Helping
{

    internal class ReaderProcessor : CodegenBase
    {

        #region Reflection references.
        public TypeDefinition GeneratedReaderClassTypeDef;
        public MethodDefinition GeneratedReaderOnLoadMethodDef;
        public readonly Dictionary<string, MethodReference> InstancedReaderMethods = new Dictionary<string, MethodReference>();
        public readonly Dictionary<string, MethodReference> StaticReaderMethods = new Dictionary<string, MethodReference>();
        public HashSet<TypeReference> AutoPackedMethods = new HashSet<TypeReference>(new TypeReferenceComparer());
        #endregion

        #region Misc.
        /// <summary>
        /// TypeReferences which have already had delegates made for.
        /// </summary>
        private HashSet<TypeReference> _delegatedTypes = new HashSet<TypeReference>();
        #endregion

        #region Const.
        /// <summary>
        /// Namespace to use for generated serializers and delegates.
        /// </summary>
        public const string GENERATED_READER_NAMESPACE = WriterProcessor.GENERATED_WRITER_NAMESPACE;
        /// <summary>
        /// Name to use for generated serializers class.
        /// </summary>
        public const string GENERATED_WRITERS_CLASS_NAME = "GeneratedReaders___Internal";
        /// <summary>
        /// Attributes to use for generated serializers class.
        /// </summary>
        public const TypeAttributes GENERATED_TYPE_ATTRIBUTES = (TypeAttributes.BeforeFieldInit | TypeAttributes.Class | TypeAttributes.AnsiClass |
            TypeAttributes.Public | TypeAttributes.AutoClass | TypeAttributes.Abstract | TypeAttributes.Sealed);
        /// <summary>
        /// Name to use for InitializeOnce method.
        /// </summary>
        public const string INITIALIZEONCE_METHOD_NAME = WriterProcessor.INITIALIZEONCE_METHOD_NAME;
        /// <summary>
        /// Attributes to use for InitializeOnce method within generated serializer classes.
        /// </summary>
        public const MethodAttributes INITIALIZEONCE_METHOD_ATTRIBUTES = WriterProcessor.INITIALIZEONCE_METHOD_ATTRIBUTES;
        /// <summary>
        /// Attritbutes to use for generated serializers.
        /// </summary>
        public const MethodAttributes GENERATED_METHOD_ATTRIBUTES = WriterProcessor.GENERATED_METHOD_ATTRIBUTES;
        /// <summary>
        /// Prefix used which all instanced and user created serializers should start with.
        /// </summary>
        internal const string READ_PREFIX = "Read";
        /// <summary>
        /// Types to exclude from being scanned for auto serialization.
        /// </summary>
        public static System.Type[] EXCLUDED_AUTO_SERIALIZER_TYPES => WriterProcessor.EXCLUDED_AUTO_SERIALIZER_TYPES;
        /// <summary>
        /// Types to exclude from being scanned for auto serialization.
        /// </summary>
        public static string[] EXCLUDED_ASSEMBLY_PREFIXES => WriterProcessor.EXCLUDED_ASSEMBLY_PREFIXES;
        #endregion

        public override bool ImportReferences() => true;

        public bool Process()
        {
            GeneralHelper gh = base.GetClass<GeneralHelper>();

            CreateGeneratedClassData();
            FindInstancedReaders();
            CreateInstancedReaderExtensions();

            void CreateGeneratedClassData()
            {
                GeneratedReaderClassTypeDef = gh.GetOrCreateClass(out _, ReaderGenerator.GENERATED_TYPE_ATTRIBUTES, ReaderGenerator.GENERATED_READERS_CLASS_NAME, null, WriterProcessor.GENERATED_WRITER_NAMESPACE);
                /* If constructor isn't set then try to get or create it
                 * and also add it to methods if were created. */
                GeneratedReaderOnLoadMethodDef = gh.GetOrCreateMethod(GeneratedReaderClassTypeDef, out _, INITIALIZEONCE_METHOD_ATTRIBUTES, INITIALIZEONCE_METHOD_NAME, base.Module.TypeSystem.Void);
                gh.CreateRuntimeInitializeOnLoadMethodAttribute(GeneratedReaderOnLoadMethodDef);

                ILProcessor ppp = GeneratedReaderOnLoadMethodDef.Body.GetILProcessor();
                ppp.Emit(OpCodes.Ret);
                //GeneratedReaderOnLoadMethodDef.DeclaringType.Methods.Remove(GeneratedReaderOnLoadMethodDef);
            }

            void FindInstancedReaders()
            {
                Type pooledWriterType = typeof(PooledReader);
                foreach (SR.MethodInfo methodInfo in pooledWriterType.GetMethods())
                {
                    if (IsSpecialReadMethod(methodInfo))
                        continue;
                    bool autoPackMethod;
                    if (IsIgnoredWriteMethod(methodInfo, out autoPackMethod))
                        continue;

                    MethodReference methodRef = base.ImportReference(methodInfo);
                    /* TypeReference for the return type
                     * of the read method. */
                    TypeReference typeRef = base.ImportReference(methodRef.ReturnType);

                    /* If here all checks pass. */
                    AddReaderMethod(typeRef, methodRef, true, true);
                    if (autoPackMethod)
                        AutoPackedMethods.Add(typeRef);
                }
            }

            return true;
        }


        /// <summary>
        /// Returns if a MethodInfo is considered a special write method.
        /// Special read methods have declared references within this class, and will not have extensions made for them.
        /// </summary>
        public bool IsSpecialReadMethod(SR.MethodInfo methodInfo)
        {
            /* Special methods. */
            if (methodInfo.Name == nameof(PooledReader.ReadPackedWhole))
                return true;
            else if (methodInfo.Name == nameof(PooledReader.ReadArray))
                return true;
            else if (methodInfo.Name == nameof(PooledReader.ReadDictionary))
                return true;

            return false;
        }

        /// <summary>
        /// Returns if a read method should be ignored.
        /// </summary>
        public bool IsIgnoredWriteMethod(SR.MethodInfo methodInfo, out bool autoPackMethod)
        {
            autoPackMethod = false;

            if (base.GetClass<GeneralHelper>().CodegenExclude(methodInfo))
                return true;
            //Not long enough to be a write method.
            else if (methodInfo.Name.Length < READ_PREFIX.Length)
                return true;
            //Method name doesn't start with writePrefix.
            else if (methodInfo.Name.Substring(0, READ_PREFIX.Length) != READ_PREFIX)
                return true;
            SR.ParameterInfo[] parameterInfos = methodInfo.GetParameters();
            //Can have at most one parameter for packing.
            if (parameterInfos.Length > 1)
                return true;
            //If has one parameter make sure it's a packing type.
            if (parameterInfos.Length == 1)
            {
                autoPackMethod = (parameterInfos[0].ParameterType == typeof(AutoPackType));
                if (!autoPackMethod)
                    return true;
            }

            return false;
        }



        /// <summary>
        /// Adds typeRef, methodDef to instanced or readerMethods.
        /// </summary>
        /// <param name="typeRef"></param>
        /// <param name="methodRef"></param>
        /// <param name="useAdd"></param>
        internal void AddReaderMethod(TypeReference typeRef, MethodReference methodRef, bool instanced, bool useAdd)
        {
            string fullName = base.GetClass<GeneralHelper>().RemoveGenericBrackets(typeRef.FullName);
            Dictionary<string, MethodReference> dict = (instanced) ?
                InstancedReaderMethods : StaticReaderMethods;

            if (useAdd)
                dict.Add(fullName, methodRef);
            else
                dict[fullName] = methodRef;
        }


        /// <summary>
        /// Creates a Read delegate for readMethodRef and places it within the generated reader/writer constructor.
        /// </summary>
        /// <param name="readMr"></param>
        /// <param name="diagnostics"></param>
        internal void CreateReadDelegate(MethodReference readMr, bool isStatic)
        {
            GeneralHelper gh = base.GetClass<GeneralHelper>();
            ReaderImports ri = base.GetClass<ReaderImports>();

            if (!isStatic)
            {
                //Supporting Write<T> with types containing generics is more trouble than it's worth.
                if (readMr.IsGenericInstance || readMr.HasGenericParameters)
                    return;
            }

            //Check if ret already exist, if so remove it; ret will be added on again in this method.
            if (GeneratedReaderOnLoadMethodDef.Body.Instructions.Count != 0)
            {
                int lastIndex = (GeneratedReaderOnLoadMethodDef.Body.Instructions.Count - 1);
                if (GeneratedReaderOnLoadMethodDef.Body.Instructions[lastIndex].OpCode == OpCodes.Ret)
                    GeneratedReaderOnLoadMethodDef.Body.Instructions.RemoveAt(lastIndex);
            }
            //Check if already exist.
            ILProcessor processor = GeneratedReaderOnLoadMethodDef.Body.GetILProcessor();
            TypeReference dataTypeRef = readMr.ReturnType;
            if (_delegatedTypes.Contains(dataTypeRef))
            {
                base.LogError($"Generic read already created for {dataTypeRef.FullName}.");
                return;
            }
            else
            {
                _delegatedTypes.Add(dataTypeRef);
            }

            //Create a Func<Reader, T> delegate 
            processor.Emit(OpCodes.Ldnull);
            processor.Emit(OpCodes.Ldftn, readMr);

            GenericInstanceType functionGenericInstance;
            MethodReference functionConstructorInstanceMethodRef;
            bool isAutoPacked = IsAutoPackedType(dataTypeRef);

            //Generate for autopacktype.
            if (isAutoPacked)
            {
                functionGenericInstance = gh.FunctionT3TypeRef.MakeGenericInstanceType(ri.ReaderTypeRef, base.GetClass<WriterImports>().AutoPackTypeRef, dataTypeRef);
                functionConstructorInstanceMethodRef = gh.FunctionT3ConstructorMethodRef.MakeHostInstanceGeneric(base.Session, functionGenericInstance);
            }
            //Not autopacked.
            else
            {
                functionGenericInstance = gh.FunctionT2TypeRef.MakeGenericInstanceType(ri.ReaderTypeRef, dataTypeRef);
                functionConstructorInstanceMethodRef = gh.FunctionT2ConstructorMethodRef.MakeHostInstanceGeneric(base.Session, functionGenericInstance);
            }
            processor.Emit(OpCodes.Newobj, functionConstructorInstanceMethodRef);

            //Call delegate to GeneratedReader<T>.Read
            GenericInstanceType genericInstance = ri.GenericReaderTypeRef.MakeGenericInstanceType(dataTypeRef);
            MethodReference genericReadMethodRef = (isAutoPacked) ?
                    ri.ReadAutoPackSetMethodRef.MakeHostInstanceGeneric(base.Session, genericInstance) :
                    ri.ReadSetMethodRef.MakeHostInstanceGeneric(base.Session, genericInstance);
            processor.Emit(OpCodes.Call, genericReadMethodRef);

            processor.Emit(OpCodes.Ret);
        }

        /// <summary>
        /// Creates reader extension methods for built-in readers.
        /// </summary>
        private void CreateInstancedReaderExtensions()
        {
            if (!FishNetILPP.IsFishNetAssembly(base.Session))
                return;

            GeneralHelper gh = base.GetClass<GeneralHelper>();
            ReaderProcessor gwh = base.GetClass<ReaderProcessor>();

            //List<MethodReference> staticReaders = new List<MethodReference>();
            foreach (KeyValuePair<string, MethodReference> item in InstancedReaderMethods)
            {
                MethodReference itemMr = item.Value;
                if (itemMr.ContainsGenericParameter)
                    continue;
  
                TypeReference returnTr = base.ImportReference(itemMr.ReturnType);

                MethodDefinition md = new MethodDefinition($"InstancedExtension___{itemMr.Name}",
                    WriterProcessor.GENERATED_METHOD_ATTRIBUTES,
                    returnTr);
                //Add extension parameter.
                ParameterDefinition readerPd = gh.CreateParameter(md, typeof(Reader), "reader");
                //Add parameters needed by instanced writer.
                List<ParameterDefinition> otherPds = md.CreateParameters(base.Session, itemMr);
                gh.MakeExtensionMethod(md);
                //
                gwh.GeneratedReaderClassTypeDef.Methods.Add(md);

                ILProcessor processor = md.Body.GetILProcessor();
                //Load writer.
                processor.Emit(OpCodes.Ldarg, readerPd);
                //Load args.
                foreach (ParameterDefinition pd in otherPds)
                    processor.Emit(OpCodes.Ldarg, pd);
                //Call instanced.
                processor.Emit(OpCodes.Callvirt, item.Value);
                processor.Emit(OpCodes.Ret);

                AddReaderMethod(returnTr, md, false, true);
            }
        }

        /// <summary>
        /// Removes typeRef from static/instanced reader methods.
        /// </summary>
        internal void RemoveReaderMethod(TypeReference typeRef, bool instanced)
        {
            string fullName = base.GetClass<GeneralHelper>().RemoveGenericBrackets(typeRef.FullName);
            Dictionary<string, MethodReference> dict = (instanced) ?
                InstancedReaderMethods : StaticReaderMethods;

            dict.Remove(fullName);
        }

        /// <summary>
        /// Creates read instructions returning instructions and outputing variable of read result.
        /// </summary>
        internal List<Instruction> CreateRead(MethodDefinition methodDef, ParameterDefinition readerParameterDef, TypeReference readTypeRef, out VariableDefinition createdVariableDef)
        {
            ILProcessor processor = methodDef.Body.GetILProcessor();
            List<Instruction> insts = new List<Instruction>();
            MethodReference readMr = GetReadMethodReference(readTypeRef);
            if (readMr != null)
            {
                //Make a local variable. 
                createdVariableDef = base.GetClass<GeneralHelper>().CreateVariable(methodDef, readTypeRef);
                //pooledReader.ReadBool();
                insts.Add(processor.Create(OpCodes.Ldarg, readerParameterDef));
                //If an auto pack method then insert default value.
                if (AutoPackedMethods.Contains(readTypeRef))
                {
                    AutoPackType packType = base.GetClass<GeneralHelper>().GetDefaultAutoPackType(readTypeRef);
                    insts.Add(processor.Create(OpCodes.Ldc_I4, (int)packType));
                }


                TypeReference valueTr = readTypeRef;
                /* If generic then find write class for
                 * data type. Currently we only support one generic
                 * for this. */
                if (valueTr.IsGenericInstance)
                {
                    GenericInstanceType git = (GenericInstanceType)valueTr;
                    TypeReference genericTr = git.GenericArguments[0];
                    readMr = readMr.GetMethodReference(base.Session, genericTr);
                }

                insts.Add(processor.Create(OpCodes.Call, readMr));
                //Store into local variable.
                insts.Add(processor.Create(OpCodes.Stloc, createdVariableDef));
                return insts;
            }
            else
            {
                base.LogError("Reader not found for " + readTypeRef.ToString());
                createdVariableDef = null;
                return null;
            }
        }



        /// <summary>
        /// Creates a read for fieldRef and populates it into a created variable of class or struct type.
        /// </summary> 
        internal bool CreateReadIntoClassOrStruct(MethodDefinition readerMd, ParameterDefinition readerPd, MethodReference readMr, VariableDefinition objectVd, FieldReference valueFr)
        {
            if (readMr != null)
            {
                ILProcessor processor = readerMd.Body.GetILProcessor();
                /* How to load object instance. If it's a structure
                 * then it must be loaded by address. Otherwise if
                 * class Ldloc can be used. */
                OpCode loadOpCode = (objectVd.VariableType.IsValueType) ?
                    OpCodes.Ldloca : OpCodes.Ldloc;

                /* If generic then find write class for
                 * data type. Currently we only support one generic
                 * for this. */
                if (valueFr.FieldType.IsGenericInstance)
                {
                    GenericInstanceType git = (GenericInstanceType)valueFr.FieldType;
                    TypeReference genericTr = git.GenericArguments[0];
                    readMr = readMr.GetMethodReference(base.Session, genericTr);
                }

                processor.Emit(loadOpCode, objectVd);
                //reader.
                processor.Emit(OpCodes.Ldarg, readerPd);
                if (IsAutoPackedType(valueFr.FieldType))
                {
                    AutoPackType packType = base.GetClass<GeneralHelper>().GetDefaultAutoPackType(valueFr.FieldType);
                    processor.Emit(OpCodes.Ldc_I4, (int)packType);
                }
                //reader.ReadXXXX().
                processor.Emit(OpCodes.Call, readMr);
                //obj.Field = result / reader.ReadXXXX().
                processor.Emit(OpCodes.Stfld, valueFr);

                return true;
            }
            else
            {
                base.LogError($"Reader not found for {valueFr.FullName}.");
                return false;
            }
        }


        /// <summary>
        /// Creates a read for fieldRef and populates it into a created variable of class or struct type.
        /// </summary>
        internal bool CreateReadIntoClassOrStruct(MethodDefinition methodDef, ParameterDefinition readerPd, MethodReference readMr, VariableDefinition objectVariableDef, MethodReference setMr, TypeReference readTr)
        {
            if (readMr != null)
            {
                ILProcessor processor = methodDef.Body.GetILProcessor();

                /* How to load object instance. If it's a structure
                 * then it must be loaded by address. Otherwise if
                 * class Ldloc can be used. */
                OpCode loadOpCode = (objectVariableDef.VariableType.IsValueType) ?
                    OpCodes.Ldloca : OpCodes.Ldloc;

                /* If generic then find write class for
                 * data type. Currently we only support one generic
                 * for this. */
                if (readTr.IsGenericInstance)
                {
                    GenericInstanceType git = (GenericInstanceType)readTr;
                    TypeReference genericTr = git.GenericArguments[0];
                    readMr = readMr.GetMethodReference(base.Session, genericTr);
                }

                processor.Emit(loadOpCode, objectVariableDef);
                //reader.
                processor.Emit(OpCodes.Ldarg, readerPd);
                if (IsAutoPackedType(readTr))
                {
                    AutoPackType packType = base.GetClass<GeneralHelper>().GetDefaultAutoPackType(readTr);
                    processor.Emit(OpCodes.Ldc_I4, (int)packType);
                }
                //reader.ReadXXXX().
                processor.Emit(OpCodes.Call, readMr);
                //obj.Property = result / reader.ReadXXXX().
                processor.Emit(OpCodes.Call, setMr);

                return true;
            }
            else
            {
                base.LogError($"Reader not found for {readTr.FullName}.");
                return false;
            }
        }




        /// <summary>
        /// Creates generic write delegates for all currently known write types.
        /// </summary>
        internal void CreateStaticMethodDelegates()
        {
            foreach (KeyValuePair<string, MethodReference> item in StaticReaderMethods)
                base.GetClass<ReaderProcessor>().CreateReadDelegate(item.Value, true);
        }


        /// <summary>
        /// Returns if typeRef has a deserializer.
        /// </summary>
        /// <param name="typeRef"></param>
        /// <param name="createMissing"></param>
        /// <returns></returns>
        internal bool HasDeserializer(TypeReference typeRef, bool createMissing)
        {
            bool result = (GetInstancedReadMethodReference(typeRef) != null) ||
                (GetStaticReadMethodReference(typeRef) != null);

            if (!result && createMissing)
            {
                if (!base.GetClass<GeneralHelper>().HasNonSerializableAttribute(typeRef.CachedResolve(base.Session)))
                {
                    MethodReference methodRef = base.GetClass<ReaderGenerator>().CreateReader(typeRef);
                    result = (methodRef != null);
                }
            }

            return result;
        }


        /// <summary>
        /// Returns if typeRef supports auto packing.
        /// </summary>
        /// <param name="typeRef"></param>
        /// <returns></returns>
        internal bool IsAutoPackedType(TypeReference typeRef)
        {
            return AutoPackedMethods.Contains(typeRef);
        }
        /// <summary>
        /// Creates a null check on the first argument and returns a null object if result indicates to do so.
        /// </summary>
        internal void CreateRetOnNull(ILProcessor processor, ParameterDefinition readerParameterDef, VariableDefinition resultVariableDef, bool useBool)
        {
            Instruction endIf = processor.Create(OpCodes.Nop);

            if (useBool)
                CreateReadBool(processor, readerParameterDef, resultVariableDef);
            else
                CreateReadPackedWhole(processor, readerParameterDef, resultVariableDef);

            //If (true or == -1) jmp to endIf. True is null.
            processor.Emit(OpCodes.Ldloc, resultVariableDef);
            if (useBool)
            {
                processor.Emit(OpCodes.Brfalse, endIf);
            }
            else
            {
                //-1
                processor.Emit(OpCodes.Ldc_I4_M1);
                processor.Emit(OpCodes.Bne_Un_S, endIf);
            }
            //Insert null.
            processor.Emit(OpCodes.Ldnull);
            //Exit method.
            processor.Emit(OpCodes.Ret);
            //End of if check.
            processor.Append(endIf);
        }

        /// <summary>
        /// Creates a call to WriteBoolean with value.
        /// </summary>
        /// <param name="processor"></param>
        /// <param name="writerParameterDef"></param>
        /// <param name="value"></param>
        internal void CreateReadBool(ILProcessor processor, ParameterDefinition readerParameterDef, VariableDefinition localBoolVariableDef)
        {
            MethodReference readBoolMethodRef = GetReadMethodReference(base.GetClass<GeneralHelper>().GetTypeReference(typeof(bool)));
            processor.Emit(OpCodes.Ldarg, readerParameterDef);
            processor.Emit(OpCodes.Callvirt, readBoolMethodRef);
            processor.Emit(OpCodes.Stloc, localBoolVariableDef);
        }

        /// <summary>
        /// Creates a call to WritePackWhole with value.
        /// </summary>
        /// <param name="processor"></param>
        /// <param name="value"></param>
        internal void CreateReadPackedWhole(ILProcessor processor, ParameterDefinition readerParameterDef, VariableDefinition resultVariableDef)
        {
            //Reader.
            processor.Emit(OpCodes.Ldarg, readerParameterDef);
            //Reader.ReadPackedWhole().
            processor.Emit(OpCodes.Callvirt, base.GetClass<ReaderImports>().Reader_ReadPackedWhole_MethodRef);
            processor.Emit(OpCodes.Conv_I4);
            processor.Emit(OpCodes.Stloc, resultVariableDef);
        }


        #region GetReaderMethodReference.
        /// <summary>
        /// Returns the MethodReference for typeRef.
        /// </summary>
        /// <param name="typeRef"></param>
        /// <returns></returns>
        internal MethodReference GetInstancedReadMethodReference(TypeReference typeRef)
        {
            string fullName = base.GetClass<GeneralHelper>().RemoveGenericBrackets(typeRef.FullName);
            InstancedReaderMethods.TryGetValue(fullName, out MethodReference methodRef);
            return methodRef;
        }
        /// <summary>
        /// Returns the MethodReference for typeRef.
        /// </summary>
        /// <param name="typeRef"></param>
        /// <returns></returns>
        internal MethodReference GetStaticReadMethodReference(TypeReference typeRef)
        {
            string fullName = base.GetClass<GeneralHelper>().RemoveGenericBrackets(typeRef.FullName);
            StaticReaderMethods.TryGetValue(fullName, out MethodReference methodRef);
            return methodRef;
        }
        /// <summary>
        /// Returns the MethodReference for typeRef favoring instanced or static. Returns null if not found.
        /// </summary>
        /// <param name="typeRef"></param>
        /// <param name="favorInstanced"></param>
        /// <returns></returns>
        internal MethodReference GetReadMethodReference(TypeReference typeRef)
        {
            MethodReference result;
            bool favorInstanced = false;
            if (favorInstanced)
            {
                result = GetInstancedReadMethodReference(typeRef);
                if (result == null)
                    result = GetStaticReadMethodReference(typeRef);
            }
            else
            {
                result = GetStaticReadMethodReference(typeRef);
                if (result == null)
                    result = GetInstancedReadMethodReference(typeRef);
            }

            return result;
        }
        /// <summary>
        /// Returns the MethodReference for typeRef favoring instanced or static.
        /// </summary>
        /// <param name="typeRef"></param>
        /// <param name="favorInstanced"></param>
        /// <returns></returns>
        internal MethodReference GetOrCreateReadMethodReference(TypeReference typeRef)
        {
            bool favorInstanced = false;
            //Try to get existing writer, if not present make one.
            MethodReference readMethodRef = GetReadMethodReference(typeRef);
            if (readMethodRef == null)
                readMethodRef = base.GetClass<ReaderGenerator>().CreateReader(typeRef);

            //If still null then return could not be generated.
            if (readMethodRef == null)
            {
                base.LogError($"Could not create deserializer for {typeRef.FullName}.");
            }
            //Otherwise, check if generic and create writes for generic pararameters.
            else if (typeRef.IsGenericInstance)
            {
                GenericInstanceType git = (GenericInstanceType)typeRef;
                foreach (TypeReference item in git.GenericArguments)
                {
                    MethodReference result = GetOrCreateReadMethodReference(item);
                    if (result == null)
                    {
                        base.LogError($"Could not create deserializer for {item.FullName}.");
                        return null;
                    }
                }
            }

            return readMethodRef;
        }
        #endregion

    }
}