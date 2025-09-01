using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;
using UnityEngine;
using System.Runtime.InteropServices;

public static class WeatherManager
{
    public static unsafe void PrintWeatherDefs()
    {
        try
        {
            var manager = GameObject.Find("TimeOfDayManager");
            if (manager == null)
            {
                MelonLogger.Msg("[Weather] TimeOfDayManager not found.");
                return;
            }

            var obj = (Il2CppObjectBase)(object)manager;
            IntPtr klass = IL2CPP.il2cpp_object_get_class(obj.Pointer);
            IntPtr field = IL2CPP.il2cpp_class_get_field_from_name(klass, "_weatherDefList");
            if (field == IntPtr.Zero)
            {
                MelonLogger.Msg("[Weather] _weatherDefList field not found.");
                return;
            }

            IntPtr listPtr = Marshal.ReadIntPtr(obj.Pointer, IL2CPP.il2cpp_field_get_offset(field));
            if (listPtr == IntPtr.Zero)
            {
                MelonLogger.Msg("[Weather] weatherDefList is null.");
                return;
            }

            IntPtr listClass = IL2CPP.il2cpp_object_get_class(listPtr);
            IntPtr getEnumerator = IL2CPP.il2cpp_class_get_method_from_name(listClass, "GetEnumerator", 0);
            if (getEnumerator == IntPtr.Zero)
            {
                MelonLogger.Msg("[Weather] GetEnumerator not found.");
                return;
            }

            IntPtr exc = IntPtr.Zero;
            IntPtr enumerator = IL2CPP.il2cpp_runtime_invoke(getEnumerator, listPtr, null, ref exc);
            if (exc != IntPtr.Zero || enumerator == IntPtr.Zero)
            {
                MelonLogger.Msg("[Weather] Failed to get enumerator.");
                return;
            }

            IntPtr enumClass = IL2CPP.il2cpp_object_get_class(enumerator);
            IntPtr moveNext = IL2CPP.il2cpp_class_get_method_from_name(enumClass, "MoveNext", 0);
            IntPtr getCurrent = IL2CPP.il2cpp_class_get_method_from_name(enumClass, "get_Current", 0);
            if (moveNext == IntPtr.Zero || getCurrent == IntPtr.Zero)
            {
                MelonLogger.Msg("[Weather] Enumerator methods not found.");
                return;
            }

            int index = 0;
            while (true)
            {
                exc = IntPtr.Zero;
                IntPtr res = IL2CPP.il2cpp_runtime_invoke(moveNext, enumerator, null, ref exc);
                if (exc != IntPtr.Zero || res == IntPtr.Zero) break;
                bool hasNext = (*(byte*)IL2CPP.il2cpp_object_unbox(res)) != 0;
                if (!hasNext) break;

                exc = IntPtr.Zero;
                IntPtr cur = IL2CPP.il2cpp_runtime_invoke(getCurrent, enumerator, null, ref exc);
                if (exc != IntPtr.Zero) break;

                string val = SafeToString(cur);
                MelonLogger.Msg($"[WeatherDef {index++}] {val}");
            }
        }
        catch (Exception e)
        {
            MelonLogger.Error("[Weather] " + e);
        }
    }

    private static unsafe string SafeToString(IntPtr objPtr)
    {
        if (objPtr == IntPtr.Zero) return "null";
        try
        {
            IntPtr klass = IL2CPP.il2cpp_object_get_class(objPtr);
            IntPtr toString = IL2CPP.il2cpp_class_get_method_from_name(klass, "ToString", 0);
            if (toString == IntPtr.Zero) return "<no ToString>";
            IntPtr exc = IntPtr.Zero;
            IntPtr strPtr = IL2CPP.il2cpp_runtime_invoke(toString, objPtr, null, ref exc);
            if (exc != IntPtr.Zero || strPtr == IntPtr.Zero) return "<exception>";
            return IL2CPP.Il2CppStringToManaged(strPtr);
        }
        catch
        {
            return "<err>";
        }
    }
}

