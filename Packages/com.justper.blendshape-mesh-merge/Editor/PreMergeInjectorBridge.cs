// ============================================================
//  BlendShape Mesh Merge - pre-merge hand-off
//
//  Portable BlendShape Injector must finish before this tool
//  merges anything: afterwards the accessory's own vertices sit
//  inside the region its correction shape deforms.
//
//  Upload order already guarantees that (-21000 before -20000),
//  but the Play Mode fallbacks of both tools are plain editor
//  callbacks whose order is not defined. Calling the injector
//  from here makes the sequence deterministic in every path.
//
//  Located through reflection so this tool keeps working, and
//  keeps merging, when the injector is not installed.
// ============================================================

#if UNITY_EDITOR && VRC_SDK_VRCSDK3
using System;
using System.Reflection;
using UnityEngine;

namespace BlendShapeMerge
{
    internal static class PreMergeInjectorBridge
    {
        const string ProcessorTypeName = "Justper.PortableBlendShapes.PortableBlendShapeInjectorProcessor";

        static bool searched;
        static MethodInfo processAvatar;

        /// <summary>
        /// Lets the injector generate its shapes first. Any failure is rethrown so
        /// the merge, and the upload, stop rather than producing an avatar whose
        /// body is missing the shape an accessory depends on.
        /// </summary>
        internal static void Run(GameObject avatarRoot, bool saveAssets)
        {
            if (avatarRoot == null) return;

            if (!searched)
            {
                searched = true;
                processAvatar = FindProcessAvatar();
            }
            if (processAvatar == null) return;

            try
            {
                processAvatar.Invoke(null, new object[] { avatarRoot, saveAssets });
            }
            catch (TargetInvocationException e)
            {
                throw e.InnerException ?? e;
            }
        }

        static MethodInfo FindProcessAvatar()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type;
                try { type = assembly.GetType(ProcessorTypeName, false); }
                catch (Exception) { continue; }
                if (type == null) continue;

                return type.GetMethod("ProcessAvatar", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(GameObject), typeof(bool) }, null);
            }
            return null;
        }
    }
}
#endif
