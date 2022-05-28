#nullable enable

using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Runtime.Serialization;
using Godot.Collections;
using Godot.NativeInterop;

namespace Godot.Bridge
{
    // TODO: Make class internal once we replace LookupScriptsInAssembly (the only public member) with source generators
    public static partial class ScriptManagerBridge
    {
        private static ConcurrentDictionary<AssemblyLoadContext, ConcurrentDictionary<Type, byte>>
            _alcData = new();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void OnAlcUnloading(AssemblyLoadContext alc)
        {
            if (_alcData.TryRemove(alc, out var typesInAlc))
            {
                foreach (var type in typesInAlc.Keys)
                {
                    if (_scriptTypeBiMap.RemoveByScriptType(type, out IntPtr scriptPtr) &&
                        !_pathTypeBiMap.TryGetScriptPath(type, out _))
                    {
                        // For scripts without a path, we need to keep the class qualified name for reloading
                        _scriptDataForReload.TryAdd(scriptPtr,
                            (type.Assembly.GetName().Name, type.FullName ?? type.ToString()));
                    }

                    _pathTypeBiMap.RemoveByScriptType(type);
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AddTypeForAlcReloading(Type type)
        {
            var alc = AssemblyLoadContext.GetLoadContext(type.Assembly);
            if (alc == null)
                return;

            var typesInAlc = _alcData.GetOrAdd(alc,
                static alc =>
                {
                    alc.Unloading += OnAlcUnloading;
                    return new();
                });
            typesInAlc.TryAdd(type, 0);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void TrackAlcForUnloading(AssemblyLoadContext alc)
        {
            _ = _alcData.GetOrAdd(alc,
                static alc =>
                {
                    alc.Unloading += OnAlcUnloading;
                    return new();
                });
        }

        private static ScriptTypeBiMap _scriptTypeBiMap = new();
        private static PathScriptTypeBiMap _pathTypeBiMap = new();

        private static ConcurrentDictionary<IntPtr, (string? assemblyName, string classFullName)>
            _scriptDataForReload = new();

        [UnmanagedCallersOnly]
        internal static void FrameCallback()
        {
            try
            {
                Dispatcher.DefaultGodotTaskScheduler?.Activate();
            }
            catch (Exception e)
            {
                ExceptionUtils.LogException(e);
            }
        }

        [UnmanagedCallersOnly]
        internal static unsafe IntPtr CreateManagedForGodotObjectBinding(godot_string_name* nativeTypeName,
            IntPtr godotObject)
        {
            // TODO: Optimize with source generators and delegate pointers

            try
            {
                using var stringName = StringName.CreateTakingOwnershipOfDisposableValue(
                    NativeFuncs.godotsharp_string_name_new_copy(CustomUnsafe.AsRef(nativeTypeName)));
                string nativeTypeNameStr = stringName.ToString();

                Type nativeType = TypeGetProxyClass(nativeTypeNameStr) ?? throw new InvalidOperationException(
                    "Wrapper class not found for type: " + nativeTypeNameStr);
                var obj = (Object)FormatterServices.GetUninitializedObject(nativeType);

                var ctor = nativeType.GetConstructor(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);

                obj.NativePtr = godotObject;

                _ = ctor!.Invoke(obj, null);

                return GCHandle.ToIntPtr(CustomGCHandle.AllocStrong(obj));
            }
            catch (Exception e)
            {
                ExceptionUtils.LogException(e);
                return IntPtr.Zero;
            }
        }

        [UnmanagedCallersOnly]
        internal static unsafe godot_bool CreateManagedForGodotObjectScriptInstance(IntPtr scriptPtr,
            IntPtr godotObject,
            godot_variant** args, int argCount)
        {
            // TODO: Optimize with source generators and delegate pointers

            try
            {
                // Performance is not critical here as this will be replaced with source generators.
                Type scriptType = _scriptTypeBiMap.GetScriptType(scriptPtr);
                var obj = (Object)FormatterServices.GetUninitializedObject(scriptType);

                var ctor = scriptType
                    .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(c => c.GetParameters().Length == argCount)
                    .FirstOrDefault();

                if (ctor == null)
                {
                    if (argCount == 0)
                    {
                        throw new MissingMemberException(
                            $"Cannot create script instance. The class '{scriptType.FullName}' does not define a parameterless constructor.");
                    }
                    else
                    {
                        throw new MissingMemberException(
                            $"The class '{scriptType.FullName}' does not define a constructor that takes x parameters.");
                    }
                }

                var parameters = ctor.GetParameters();
                int paramCount = parameters.Length;

                var invokeParams = new object?[paramCount];

                for (int i = 0; i < paramCount; i++)
                {
                    invokeParams[i] = Marshaling.ConvertVariantToManagedObjectOfType(
                        *args[i], parameters[i].ParameterType);
                }

                obj.NativePtr = godotObject;

                _ = ctor.Invoke(obj, invokeParams);


                return godot_bool.True;
            }
            catch (Exception e)
            {
                ExceptionUtils.LogException(e);
                return godot_bool.False;
            }
        }

        [UnmanagedCallersOnly]
        internal static unsafe void GetScriptNativeName(IntPtr scriptPtr, godot_string_name* outRes)
        {
            try
            {
                // Performance is not critical here as this will be replaced with source generators.
                if (!_scriptTypeBiMap.TryGetScriptType(scriptPtr, out Type? scriptType))
                {
                    *outRes = default;
                    return;
                }

                var native = Object.InternalGetClassNativeBase(scriptType);

                var field = native?.GetField("NativeName", BindingFlags.DeclaredOnly | BindingFlags.Static |
                                                           BindingFlags.Public | BindingFlags.NonPublic);

                if (field == null)
                {
                    *outRes = default;
                    return;
                }

                var nativeName = (StringName?)field.GetValue(null);

                if (nativeName == null)
                {
                    *outRes = default;
                    return;
                }

                *outRes = NativeFuncs.godotsharp_string_name_new_copy((godot_string_name)nativeName.NativeValue);
            }
            catch (Exception e)
            {
                ExceptionUtils.LogException(e);
                *outRes = default;
            }
        }

        [UnmanagedCallersOnly]
        internal static void SetGodotObjectPtr(IntPtr gcHandlePtr, IntPtr newPtr)
        {
            try
            {
                var target = (Object?)GCHandle.FromIntPtr(gcHandlePtr).Target;
                if (target != null)
                    target.NativePtr = newPtr;
            }
            catch (Exception e)
            {
                ExceptionUtils.LogException(e);
            }
        }

        private static Type? TypeGetProxyClass(string nativeTypeNameStr)
        {
            // Performance is not critical here as this will be replaced with a generated dictionary.

            if (nativeTypeNameStr[0] == '_')
                nativeTypeNameStr = nativeTypeNameStr.Substring(1);

            Type? wrapperType = typeof(Object).Assembly.GetType("Godot." + nativeTypeNameStr);

            if (wrapperType == null)
            {
                wrapperType = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "GodotSharpEditor")?
                    .GetType("Godot." + nativeTypeNameStr);
            }

            static bool IsStatic(Type type) => type.IsAbstract && type.IsSealed;

            if (wrapperType != null && IsStatic(wrapperType))
            {
                // A static class means this is a Godot singleton class. If an instance is needed we use Godot.Object.
                return typeof(Object);
            }

            return wrapperType;
        }

        // Called from GodotPlugins
        // ReSharper disable once UnusedMember.Local
        public static void LookupScriptsInAssembly(Assembly assembly)
        {
            static void LookupScriptForClass(Type type)
            {
                var scriptPathAttr = type.GetCustomAttributes(inherit: false)
                    .OfType<ScriptPathAttribute>()
                    .FirstOrDefault();

                if (scriptPathAttr == null)
                    return;

                _pathTypeBiMap.Add(scriptPathAttr.Path, type);

                if (AlcReloadCfg.IsAlcReloadingEnabled)
                {
                    AddTypeForAlcReloading(type);
                }
            }

            var assemblyHasScriptsAttr = assembly.GetCustomAttributes(inherit: false)
                .OfType<AssemblyHasScriptsAttribute>()
                .FirstOrDefault();

            if (assemblyHasScriptsAttr == null)
                return;

            if (assemblyHasScriptsAttr.RequiresLookup)
            {
                // This is supported for scenarios where specifying all types would be cumbersome,
                // such as when disabling C# source generators (for whatever reason) or when using a
                // language other than C# that has nothing similar to source generators to automate it.

                var typeOfGodotObject = typeof(Object);

                foreach (var type in assembly.GetTypes())
                {
                    if (type.IsNested)
                        continue;

                    if (!typeOfGodotObject.IsAssignableFrom(type))
                        continue;

                    LookupScriptForClass(type);
                }
            }
            else
            {
                // This is the most likely scenario as we use C# source generators

                var scriptTypes = assemblyHasScriptsAttr.ScriptTypes;

                if (scriptTypes != null)
                {
                    for (int i = 0; i < scriptTypes.Length; i++)
                    {
                        LookupScriptForClass(scriptTypes[i]);
                    }
                }
            }
        }

        [UnmanagedCallersOnly]
        internal static unsafe void RaiseEventSignal(IntPtr ownerGCHandlePtr,
            godot_string_name* eventSignalName, godot_variant** args, int argCount, godot_bool* outOwnerIsNull)
        {
            try
            {
                var owner = (Object?)GCHandle.FromIntPtr(ownerGCHandlePtr).Target;

                if (owner == null)
                {
                    *outOwnerIsNull = godot_bool.True;
                    return;
                }

                *outOwnerIsNull = godot_bool.False;

                owner.InternalRaiseEventSignal(CustomUnsafe.AsRef(eventSignalName),
                    new NativeVariantPtrArgs(args), argCount);
            }
            catch (Exception e)
            {
                ExceptionUtils.LogException(e);
                *outOwnerIsNull = godot_bool.False;
            }
        }

        [UnmanagedCallersOnly]
        internal static unsafe void GetScriptSignalList(IntPtr scriptPtr, godot_dictionary* outRetSignals)
        {
            try
            {
                // Performance is not critical here as this will be replaced with source generators.
                using var signals = new Dictionary();

                Type? top = _scriptTypeBiMap.GetScriptType(scriptPtr);
                Type native = Object.InternalGetClassNativeBase(top);

                while (top != null && top != native)
                {
                    // Legacy signals

                    foreach (var signalDelegate in top
                                 .GetNestedTypes(BindingFlags.DeclaredOnly | BindingFlags.NonPublic |
                                                 BindingFlags.Public)
                                 .Where(nestedType => typeof(Delegate).IsAssignableFrom(nestedType))
                                 .Where(@delegate => @delegate.GetCustomAttributes().OfType<SignalAttribute>().Any()))
                    {
                        var invokeMethod = signalDelegate.GetMethod("Invoke");

                        if (invokeMethod == null)
                            throw new MissingMethodException(signalDelegate.FullName, "Invoke");

                        var signalParams = new Collections.Array();

                        foreach (var parameters in invokeMethod.GetParameters())
                        {
                            var paramType = Marshaling.ConvertManagedTypeToVariantType(
                                parameters.ParameterType, out bool nilIsVariant);
                            signalParams.Add(new Dictionary()
                            {
                                { "name", parameters.Name },
                                { "type", paramType },
                                { "nil_is_variant", nilIsVariant }
                            });
                        }

                        signals.Add(signalDelegate.Name, signalParams);
                    }

                    // Event signals

                    var foundEventSignals = top.GetEvents(
                            BindingFlags.DeclaredOnly | BindingFlags.Instance |
                            BindingFlags.NonPublic | BindingFlags.Public)
                        .Where(ev => ev.GetCustomAttributes().OfType<SignalAttribute>().Any())
                        .Select(ev => ev.Name);

                    var fields = top.GetFields(
                        BindingFlags.DeclaredOnly | BindingFlags.Instance |
                        BindingFlags.NonPublic | BindingFlags.Public);

                    foreach (var eventSignalField in fields
                                 .Where(f => typeof(Delegate).IsAssignableFrom(f.FieldType))
                                 .Where(f => foundEventSignals.Contains(f.Name)))
                    {
                        var delegateType = eventSignalField.FieldType;
                        var invokeMethod = delegateType.GetMethod("Invoke");

                        if (invokeMethod == null)
                            throw new MissingMethodException(delegateType.FullName, "Invoke");

                        var signalParams = new Collections.Array();

                        foreach (var parameters in invokeMethod.GetParameters())
                        {
                            var paramType = Marshaling.ConvertManagedTypeToVariantType(
                                parameters.ParameterType, out bool nilIsVariant);
                            signalParams.Add(new Dictionary()
                            {
                                { "name", parameters.Name },
                                { "type", paramType },
                                { "nil_is_variant", nilIsVariant }
                            });
                        }

                        signals.Add(eventSignalField.Name, signalParams);
                    }

                    top = top.BaseType;
                }

                *outRetSignals = NativeFuncs.godotsharp_dictionary_new_copy((godot_dictionary)signals.NativeValue);
            }
            catch (Exception e)
            {
                ExceptionUtils.LogException(e);
                *outRetSignals = NativeFuncs.godotsharp_dictionary_new();
            }
        }

        [UnmanagedCallersOnly]
        internal static unsafe godot_bool HasScriptSignal(IntPtr scriptPtr, godot_string* signalName)
        {
            try
            {
                // Performance is not critical here as this will be replaced with source generators.
                using var signals = new Dictionary();

                string signalNameStr = Marshaling.ConvertStringToManaged(*signalName);

                Type? top = _scriptTypeBiMap.GetScriptType(scriptPtr);
                Type native = Object.InternalGetClassNativeBase(top);

                while (top != null && top != native)
                {
                    // Legacy signals

                    if (top
                        .GetNestedTypes(BindingFlags.DeclaredOnly | BindingFlags.NonPublic | BindingFlags.Public)
                        .Where(nestedType => typeof(Delegate).IsAssignableFrom(nestedType))
                        .Where(@delegate => @delegate.GetCustomAttributes().OfType<SignalAttribute>().Any())
                        .Any(signalDelegate => signalDelegate.Name == signalNameStr)
                       )
                    {
                        return godot_bool.True;
                    }

                    // Event signals

                    if (top.GetEvents(
                            BindingFlags.DeclaredOnly | BindingFlags.Instance |
                            BindingFlags.NonPublic | BindingFlags.Public)
                        .Where(ev => ev.GetCustomAttributes().OfType<SignalAttribute>().Any())
                        .Any(eventSignal => eventSignal.Name == signalNameStr)
                       )
                    {
                        return godot_bool.True;
                    }

                    top = top.BaseType;
                }

                return godot_bool.False;
            }
            catch (Exception e)
            {
                ExceptionUtils.LogException(e);
                return godot_bool.False;
            }
        }

        [UnmanagedCallersOnly]
        internal static godot_bool ScriptIsOrInherits(IntPtr scriptPtr, IntPtr scriptPtrMaybeBase)
        {
            try
            {
                if (!_scriptTypeBiMap.TryGetScriptType(scriptPtr, out Type? scriptType))
                    return godot_bool.False;

                if (!_scriptTypeBiMap.TryGetScriptType(scriptPtrMaybeBase, out Type? maybeBaseType))
                    return godot_bool.False;

                return (scriptType == maybeBaseType || maybeBaseType.IsAssignableFrom(scriptType)).ToGodotBool();
            }
            catch (Exception e)
            {
                ExceptionUtils.LogException(e);
                return godot_bool.False;
            }
        }

        [UnmanagedCallersOnly]
        internal static unsafe godot_bool AddScriptBridge(IntPtr scriptPtr, godot_string* scriptPath)
        {
            try
            {
                lock (_scriptTypeBiMap.ReadWriteLock)
                {
                    if (!_scriptTypeBiMap.IsScriptRegistered(scriptPtr))
                    {
                        string scriptPathStr = Marshaling.ConvertStringToManaged(*scriptPath);

                        if (!_pathTypeBiMap.TryGetScriptType(scriptPathStr, out Type? scriptType))
                            return godot_bool.False;

                        _scriptTypeBiMap.Add(scriptPtr, scriptType);
                    }
                }

                return godot_bool.True;
            }
            catch (Exception e)
            {
                ExceptionUtils.LogException(e);
                return godot_bool.False;
            }
        }

        [UnmanagedCallersOnly]
        internal static unsafe void GetOrCreateScriptBridgeForPath(godot_string* scriptPath, godot_ref* outScript)
        {
            string scriptPathStr = Marshaling.ConvertStringToManaged(*scriptPath);

            if (!_pathTypeBiMap.TryGetScriptType(scriptPathStr, out Type? scriptType))
            {
                NativeFuncs.godotsharp_internal_new_csharp_script(outScript);
                return;
            }

            GetOrCreateScriptBridgeForType(scriptType, outScript);
        }

        private static unsafe void GetOrCreateScriptBridgeForType(Type scriptType, godot_ref* outScript)
        {
            lock (_scriptTypeBiMap.ReadWriteLock)
            {
                if (_scriptTypeBiMap.TryGetScriptPtr(scriptType, out IntPtr scriptPtr))
                {
                    // Use existing
                    NativeFuncs.godotsharp_ref_new_from_ref_counted_ptr(out *outScript, scriptPtr);
                    return;
                }

                // This path is slower, but it's only executed for the first instantiation of the type
                CreateScriptBridgeForType(scriptType, outScript);
            }
        }

        internal static unsafe void GetOrLoadOrCreateScriptForType(Type scriptType, godot_ref* outScript)
        {
            static bool GetPathOtherwiseGetOrCreateScript(Type scriptType, godot_ref* outScript,
                [MaybeNullWhen(false)] out string scriptPath)
            {
                lock (_scriptTypeBiMap.ReadWriteLock)
                {
                    if (_scriptTypeBiMap.TryGetScriptPtr(scriptType, out IntPtr scriptPtr))
                    {
                        // Use existing
                        NativeFuncs.godotsharp_ref_new_from_ref_counted_ptr(out *outScript, scriptPtr);
                        scriptPath = null;
                        return false;
                    }

                    // This path is slower, but it's only executed for the first instantiation of the type

                    if (_pathTypeBiMap.TryGetScriptPath(scriptType, out scriptPath))
                        return true;

                    CreateScriptBridgeForType(scriptType, outScript);
                    scriptPath = null;
                    return false;
                }
            }

            if (GetPathOtherwiseGetOrCreateScript(scriptType, outScript, out string? scriptPath))
            {
                // This path is slower, but it's only executed for the first instantiation of the type

                // This must be done outside the read-write lock, as the script resource loading can lock it
                using godot_string scriptPathIn = Marshaling.ConvertStringToNative(scriptPath);
                if (!NativeFuncs.godotsharp_internal_script_load(scriptPathIn, outScript).ToBool())
                {
                    GD.PushError($"Cannot load script for type '{scriptType.FullName}'. Path: '{scriptPath}'.");

                    // If loading of the script fails, best we can do create a new script
                    // with no path, as we do for types without an associated script file.
                    GetOrCreateScriptBridgeForType(scriptType, outScript);
                }
            }
        }

        private static unsafe void CreateScriptBridgeForType(Type scriptType, godot_ref* outScript)
        {
            NativeFuncs.godotsharp_internal_new_csharp_script(outScript);
            IntPtr scriptPtr = outScript->Reference;

            // Caller takes care of locking
            _scriptTypeBiMap.Add(scriptPtr, scriptType);

            NativeFuncs.godotsharp_internal_reload_registered_script(scriptPtr);
        }

        [UnmanagedCallersOnly]
        internal static void RemoveScriptBridge(IntPtr scriptPtr)
        {
            try
            {
                lock (_scriptTypeBiMap.ReadWriteLock)
                {
                    _scriptTypeBiMap.Remove(scriptPtr);
                }
            }
            catch (Exception e)
            {
                ExceptionUtils.LogException(e);
            }
        }

        [UnmanagedCallersOnly]
        internal static godot_bool TryReloadRegisteredScriptWithClass(IntPtr scriptPtr)
        {
            try
            {
                lock (_scriptTypeBiMap.ReadWriteLock)
                {
                    if (_scriptTypeBiMap.TryGetScriptType(scriptPtr, out _))
                    {
                        // NOTE:
                        // Currently, we reload all scripts, not only the ones from the unloaded ALC.
                        // As such, we need to handle this case instead of treating it as an error.
                        NativeFuncs.godotsharp_internal_reload_registered_script(scriptPtr);
                        return godot_bool.True;
                    }

                    if (!_scriptDataForReload.TryGetValue(scriptPtr, out var dataForReload))
                    {
                        GD.PushError("Missing class qualified name for reloading script");
                        return godot_bool.False;
                    }

                    _ = _scriptDataForReload.TryRemove(scriptPtr, out _);

                    if (dataForReload.assemblyName == null)
                    {
                        GD.PushError(
                            $"Missing assembly name of class '{dataForReload.classFullName}' for reloading script");
                        return godot_bool.False;
                    }

                    var scriptType = ReflectionUtils.FindTypeInLoadedAssemblies(dataForReload.assemblyName,
                        dataForReload.classFullName);

                    if (scriptType == null)
                    {
                        // The class was removed, can't reload
                        return godot_bool.False;
                    }

                    // ReSharper disable once RedundantNameQualifier
                    if (!typeof(Godot.Object).IsAssignableFrom(scriptType))
                    {
                        // The class no longer inherits Godot.Object, can't reload
                        return godot_bool.False;
                    }

                    _scriptTypeBiMap.Add(scriptPtr, scriptType);

                    NativeFuncs.godotsharp_internal_reload_registered_script(scriptPtr);

                    return godot_bool.True;
                }
            }
            catch (Exception e)
            {
                ExceptionUtils.LogException(e);
                return godot_bool.False;
            }
        }

        [UnmanagedCallersOnly]
        internal static unsafe void UpdateScriptClassInfo(IntPtr scriptPtr, godot_bool* outTool,
            godot_dictionary* outRpcFunctionsDest, godot_ref* outBaseScript)
        {
            try
            {
                // Performance is not critical here as this will be replaced with source generators.
                var scriptType = _scriptTypeBiMap.GetScriptType(scriptPtr);

                *outTool = scriptType.GetCustomAttributes(inherit: false)
                    .OfType<ToolAttribute>()
                    .Any().ToGodotBool();

                if (!(*outTool).ToBool() && scriptType.IsNested)
                {
                    *outTool = (scriptType.DeclaringType?.GetCustomAttributes(inherit: false)
                        .OfType<ToolAttribute>()
                        .Any() ?? false).ToGodotBool();
                }

                if (!(*outTool).ToBool() && scriptType.Assembly.GetName().Name == "GodotTools")
                    *outTool = godot_bool.True;

                // RPC functions

                Dictionary<string, Dictionary> rpcFunctions = new();

                Type? top = scriptType;
                Type native = Object.InternalGetClassNativeBase(top);

                while (top != null && top != native)
                {
                    foreach (var method in top.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance |
                                                          BindingFlags.NonPublic | BindingFlags.Public))
                    {
                        if (method.IsStatic)
                            continue;

                        string methodName = method.Name;

                        if (rpcFunctions.ContainsKey(methodName))
                            continue;

                        var rpcAttr = method.GetCustomAttributes(inherit: false)
                            .OfType<RPCAttribute>().FirstOrDefault();

                        if (rpcAttr == null)
                            continue;

                        var rpcConfig = new Dictionary();

                        rpcConfig["rpc_mode"] = (long)rpcAttr.Mode;
                        rpcConfig["call_local"] = rpcAttr.CallLocal;
                        rpcConfig["transfer_mode"] = (long)rpcAttr.TransferMode;
                        rpcConfig["channel"] = rpcAttr.TransferChannel;

                        rpcFunctions.Add(methodName, rpcConfig);
                    }

                    top = top.BaseType;
                }

                *outRpcFunctionsDest =
                    NativeFuncs.godotsharp_dictionary_new_copy(
                        (godot_dictionary)((Dictionary)rpcFunctions).NativeValue);

                var baseType = scriptType.BaseType;
                if (baseType != null && baseType != native)
                {
                    GetOrLoadOrCreateScriptForType(baseType, outBaseScript);
                }
                else
                {
                    *outBaseScript = default;
                }
            }
            catch (Exception e)
            {
                ExceptionUtils.LogException(e);
                *outTool = godot_bool.False;
                *outRpcFunctionsDest = NativeFuncs.godotsharp_dictionary_new();
            }
        }

        [UnmanagedCallersOnly]
        internal static unsafe godot_bool SwapGCHandleForType(IntPtr oldGCHandlePtr, IntPtr* outNewGCHandlePtr,
            godot_bool createWeak)
        {
            try
            {
                var oldGCHandle = GCHandle.FromIntPtr(oldGCHandlePtr);

                object? target = oldGCHandle.Target;

                if (target == null)
                {
                    CustomGCHandle.Free(oldGCHandle);
                    *outNewGCHandlePtr = IntPtr.Zero;
                    return godot_bool.False; // Called after the managed side was collected, so nothing to do here
                }

                // Release the current weak handle and replace it with a strong handle.
                var newGCHandle = createWeak.ToBool() ?
                    CustomGCHandle.AllocWeak(target) :
                    CustomGCHandle.AllocStrong(target);

                CustomGCHandle.Free(oldGCHandle);
                *outNewGCHandlePtr = GCHandle.ToIntPtr(newGCHandle);
                return godot_bool.True;
            }
            catch (Exception e)
            {
                ExceptionUtils.LogException(e);
                *outNewGCHandlePtr = IntPtr.Zero;
                return godot_bool.False;
            }
        }

        // ReSharper disable once InconsistentNaming
        [SuppressMessage("ReSharper", "NotAccessedField.Local")]
        [StructLayout(LayoutKind.Sequential)]
        private ref struct godotsharp_property_info
        {
            // Careful with padding...
            public godot_string_name Name; // Not owned
            public godot_string HintString;
            public int Type;
            public int Hint;
            public int Usage;
            public godot_bool Exported;

            public void Dispose()
            {
                HintString.Dispose();
            }
        }

        [UnmanagedCallersOnly]
        internal static unsafe void GetPropertyInfoList(IntPtr scriptPtr,
            delegate* unmanaged<IntPtr, godot_string*, void*, int, void> addPropInfoFunc)
        {
            try
            {
                Type scriptType = _scriptTypeBiMap.GetScriptType(scriptPtr);
                GetPropertyInfoListForType(scriptType, scriptPtr, addPropInfoFunc);
            }
            catch (Exception e)
            {
                ExceptionUtils.LogException(e);
            }
        }

        private static unsafe void GetPropertyInfoListForType(Type type, IntPtr scriptPtr,
            delegate* unmanaged<IntPtr, godot_string*, void*, int, void> addPropInfoFunc)
        {
            try
            {
                var getGodotPropertiesMetadataMethod = type.GetMethod(
                    "GetGodotPropertiesMetadata",
                    BindingFlags.DeclaredOnly | BindingFlags.Static |
                    BindingFlags.NonPublic | BindingFlags.Public);

                if (getGodotPropertiesMetadataMethod == null)
                    return;

                var properties = (System.Collections.Generic.List<PropertyInfo>?)
                    getGodotPropertiesMetadataMethod.Invoke(null, null);

                if (properties == null || properties.Count <= 0)
                    return;

                int length = properties.Count;

                // There's no recursion here, so it's ok to go with a big enough number for most cases
                // stackMaxSize = stackMaxLength * sizeof(godotsharp_property_info)
                const int stackMaxLength = 32;
                bool useStack = length < stackMaxLength;

                godotsharp_property_info* interopProperties;

                if (useStack)
                {
                    // Weird limitation, hence the need for aux:
                    // "In the case of pointer types, you can use a stackalloc expression only in a local variable declaration to initialize the variable."
                    var aux = stackalloc godotsharp_property_info[length];
                    interopProperties = aux;
                }
                else
                {
                    interopProperties = ((godotsharp_property_info*)NativeMemory.Alloc((nuint)length))!;
                }

                try
                {
                    for (int i = 0; i < length; i++)
                    {
                        var property = properties[i];

                        godotsharp_property_info interopProperty = new()
                        {
                            Type = (int)property.Type,
                            Name = (godot_string_name)property.Name.NativeValue, // Not owned
                            Hint = (int)property.Hint,
                            HintString = Marshaling.ConvertStringToNative(property.HintString),
                            Usage = (int)property.Usage,
                            Exported = property.Exported.ToGodotBool()
                        };

                        interopProperties[i] = interopProperty;
                    }

                    using godot_string currentClassName = Marshaling.ConvertStringToNative(type.Name);

                    addPropInfoFunc(scriptPtr, &currentClassName, interopProperties, length);

                    // We're borrowing the StringName's without making an owning copy, so the
                    // managed collection needs to be kept alive until `addPropInfoFunc` returns.
                    GC.KeepAlive(properties);
                }
                finally
                {
                    for (int i = 0; i < length; i++)
                        interopProperties[i].Dispose();

                    if (!useStack)
                        NativeMemory.Free(interopProperties);
                }
            }
            catch (Exception e)
            {
                ExceptionUtils.LogException(e);
            }
        }

        // ReSharper disable once InconsistentNaming
        [SuppressMessage("ReSharper", "NotAccessedField.Local")]
        [StructLayout(LayoutKind.Sequential)]
        private ref struct godotsharp_property_def_val_pair
        {
            // Careful with padding...
            public godot_string_name Name; // Not owned
            public godot_variant Value;

            public void Dispose()
            {
                Value.Dispose();
            }
        }

        [UnmanagedCallersOnly]
        internal static unsafe void GetPropertyDefaultValues(IntPtr scriptPtr,
            delegate* unmanaged<IntPtr, void*, int, void> addDefValFunc)
        {
            try
            {
                Type? top = _scriptTypeBiMap.GetScriptType(scriptPtr);
                Type native = Object.InternalGetClassNativeBase(top);

                while (top != null && top != native)
                {
                    GetPropertyDefaultValuesForType(top, scriptPtr, addDefValFunc);

                    top = top.BaseType;
                }
            }
            catch (Exception e)
            {
                ExceptionUtils.LogException(e);
            }
        }

        [SkipLocalsInit]
        private static unsafe void GetPropertyDefaultValuesForType(Type type, IntPtr scriptPtr,
            delegate* unmanaged<IntPtr, void*, int, void> addDefValFunc)
        {
            try
            {
                var getGodotPropertyDefaultValuesMethod = type.GetMethod(
                    "GetGodotPropertyDefaultValues",
                    BindingFlags.DeclaredOnly | BindingFlags.Static |
                    BindingFlags.NonPublic | BindingFlags.Public);

                if (getGodotPropertyDefaultValuesMethod == null)
                    return;

                var defaultValues = (System.Collections.Generic.Dictionary<StringName, object>?)
                    getGodotPropertyDefaultValuesMethod.Invoke(null, null);

                if (defaultValues == null || defaultValues.Count <= 0)
                    return;

                int length = defaultValues.Count;

                // There's no recursion here, so it's ok to go with a big enough number for most cases
                // stackMaxSize = stackMaxLength * sizeof(godotsharp_property_def_val_pair)
                const int stackMaxLength = 32;
                bool useStack = length < stackMaxLength;

                godotsharp_property_def_val_pair* interopDefaultValues;

                if (useStack)
                {
                    // Weird limitation, hence the need for aux:
                    // "In the case of pointer types, you can use a stackalloc expression only in a local variable declaration to initialize the variable."
                    var aux = stackalloc godotsharp_property_def_val_pair[length];
                    interopDefaultValues = aux;
                }
                else
                {
                    interopDefaultValues = ((godotsharp_property_def_val_pair*)NativeMemory.Alloc((nuint)length))!;
                }

                try
                {
                    int i = 0;
                    foreach (var defaultValuePair in defaultValues)
                    {
                        godotsharp_property_def_val_pair interopProperty = new()
                        {
                            Name = (godot_string_name)defaultValuePair.Key.NativeValue, // Not owned
                            Value = Marshaling.ConvertManagedObjectToVariant(defaultValuePair.Value)
                        };

                        interopDefaultValues[i] = interopProperty;

                        i++;
                    }

                    addDefValFunc(scriptPtr, interopDefaultValues, length);

                    // We're borrowing the StringName's without making an owning copy, so the
                    // managed collection needs to be kept alive until `addDefValFunc` returns.
                    GC.KeepAlive(defaultValues);
                }
                finally
                {
                    for (int i = 0; i < length; i++)
                        interopDefaultValues[i].Dispose();

                    if (!useStack)
                        NativeMemory.Free(interopDefaultValues);
                }
            }
            catch (Exception e)
            {
                ExceptionUtils.LogException(e);
            }
        }
    }
}
