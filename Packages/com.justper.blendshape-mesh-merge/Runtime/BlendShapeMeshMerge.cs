// ============================================================
//  BlendShape Mesh Merge  (v1.8.2) - runtime marker component
//
//  Non-destructively merges meshes at build time, combining
//  blendshapes by name. One component = one merge group,
//  targeting the face mesh or any custom renderer.
//
//  Setup: place the prefab inside the avatar hierarchy.
//  Merge logic: Editor/BlendShapeMeshMergeProcessor.cs
// ============================================================

using System;
using System.Collections.Generic;
using UnityEngine;
#if VRC_SDK_VRCSDK3
using VRC.SDKBase;
#endif

namespace BlendShapeMerge
{
    [AddComponentMenu("Justper/BlendShape Mesh Merge")]
    [DisallowMultipleComponent]
    public class BlendShapeMeshMerge : MonoBehaviour
#if VRC_SDK_VRCSDK3
        , IEditorOnly
#endif
    {
        public const string ToolVersion = "1.8.2";

        public enum TargetMode { FaceMesh, CustomRenderer }

        public enum SourceSelectionMode
        {
            // Compatibility sentinel for components saved by versions through 1.6.
            // Never display this value in the custom inspector.
            LegacyInferFromList = 0,
            SelectedMeshes = 1,
            AllChildMeshes = 2
        }

        public enum BoneConnectionMode
        {
            // Compatibility sentinel for components saved before version 1.8.
            // Never display this value in the custom inspector.
            LegacyInferFromRemapFlag = 0,
            Automatic = 1,
            UseThisTool = 2,
            DoNotConnect = 3
        }

        [Header("Merge Target")]
        [Tooltip("Face Mesh combines matching facial blendshapes. Specific Avatar Mesh merges into another skinned mesh.")]
        public TargetMode mergeInto = TargetMode.FaceMesh;

        [Tooltip("The avatar mesh that receives the accessory meshes.")]
        public SkinnedMeshRenderer targetRenderer;

        [Header("Sources")]
        [SerializeField, HideInInspector]
        SourceSelectionMode sourceSelectionMode;

        [Tooltip("The accessory meshes to merge into the target.")]
        public List<SkinnedMeshRenderer> sourceRenderers = new List<SkinnedMeshRenderer>();

        [Header("Bones")]
        [SerializeField, HideInInspector]
        BoneConnectionMode boneConnectionMode;

        // Kept for prefab and script compatibility with versions before 1.8.
        [HideInInspector]
        public bool remapBonesByName = true;

        [Header("Fallback Attachment")]
        [Tooltip("Attaches extra bone chains that do not have a matching avatar parent.")]
        public bool attachToBone = true;

        [Tooltip("The avatar bone that extra chains follow.")]
        public HumanBodyBones attachBone = HumanBodyBones.Head;

        [Header("Blendshape Mapping")]
        [Tooltip("Matches common accessory speech-shape names to the speech shapes configured on the avatar.")]
        public bool autoMapVisemes = true;

        [Tooltip("Connects blendshapes whose names differ. Identically named shapes match automatically.")]
        public List<ShapeMapping> shapeMappings = new List<ShapeMapping>();

        public SourceSelectionMode EffectiveSourceSelectionMode
        {
            get
            {
                if (sourceSelectionMode != SourceSelectionMode.LegacyInferFromList)
                    return sourceSelectionMode;

                return sourceRenderers == null || sourceRenderers.Count == 0
                    ? SourceSelectionMode.AllChildMeshes
                    : SourceSelectionMode.SelectedMeshes;
            }
        }

        public BoneConnectionMode EffectiveBoneConnectionMode
        {
            get
            {
                if (boneConnectionMode != BoneConnectionMode.LegacyInferFromRemapFlag)
                    return boneConnectionMode;

                return remapBonesByName
                    ? BoneConnectionMode.UseThisTool
                    : BoneConnectionMode.DoNotConnect;
            }
        }

        public void SetSourceSelectionMode(SourceSelectionMode mode)
        {
            if (mode == SourceSelectionMode.LegacyInferFromList)
                throw new ArgumentException(
                    "LegacyInferFromList is reserved for components saved by older tool versions.",
                    nameof(mode));
            sourceSelectionMode = mode;
        }

        public void SetBoneConnectionMode(BoneConnectionMode mode)
        {
            if (mode == BoneConnectionMode.LegacyInferFromRemapFlag)
                throw new ArgumentException(
                    "LegacyInferFromRemapFlag is reserved for components saved by older tool versions.",
                    nameof(mode));
            boneConnectionMode = mode;
        }

        void Reset()
        {
            sourceSelectionMode = SourceSelectionMode.SelectedMeshes;
            boneConnectionMode = BoneConnectionMode.Automatic;
            if (sourceRenderers == null)
                sourceRenderers = new List<SkinnedMeshRenderer>();
        }

        [Serializable]
        public class ShapeMapping
        {
            public string sourceShape;
            public string targetShape;
        }
    }
}
