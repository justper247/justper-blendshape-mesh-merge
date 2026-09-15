// ============================================================
//  BlendShape Mesh Merge - build-time processor  (v1.8.2)
//
//  Merges grouped meshes into a target renderer, combining
//  blendshapes by name. Default target is the face mesh, so
//  native lipsync drives merged meshes with no delay.
//
//  Runs at upload and on entering Play Mode (preview). Order
//  -20000 -- runs before other build-time mesh optimizers so
//  they don't strip the merged mesh's unused blendshapes.
//
//  Triangle submeshes are required. All eight UV channels and
//  variable bone influences are preserved.
// ============================================================

#if UNITY_EDITOR && VRC_SDK_VRCSDK3
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.Collections;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase;
using VRC.SDKBase.Editor.BuildPipeline;

namespace BlendShapeMerge
{
    // ------------------------------------------------------------
    //  Core processor
    // ------------------------------------------------------------

    public static class BlendShapeMergeProcessor
    {
        const string LogPrefix = "[BlendShapeMeshMerge] ";
        public const string GeneratedFolder = "Assets/BlendShapeMeshMerge.Generated";
        const string GeneratedSessionKey = "BlendShapeMeshMerge.ActiveGeneratedFolder";
        static string generatedSessionFolder;

        [InitializeOnLoadMethod]
        static void ScheduleStaleGeneratedCleanup()
        {
            EditorApplication.delayCall += CleanupStaleGeneratedFolders;
        }

        static void CleanupStaleGeneratedFolders()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += CleanupStaleGeneratedFolders;
                return;
            }
            if (Application.isPlaying || !AssetDatabase.IsValidFolder(GeneratedFolder)) return;

            string active = SessionState.GetString(GeneratedSessionKey, "");
            string prefix = GeneratedFolder + "/Build_";
            foreach (string guid in AssetDatabase.FindAssets("", new[] { GeneratedFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string parent = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
                if (parent != GeneratedFolder || !path.StartsWith(prefix, StringComparison.Ordinal) ||
                    path == active || !AssetDatabase.IsValidFolder(path))
                    continue;
                AssetDatabase.DeleteAsset(path);
            }
            DeleteGeneratedRootIfEmpty();
        }

        internal sealed class MergePlan
        {
            public BlendShapeMeshMerge Marker;
            public SkinnedMeshRenderer Target;
            public bool IsFaceTarget;
            public List<SkinnedMeshRenderer> Sources;
            public bool ConnectMatchingBones;
            public int OriginalOrder;
        }

        internal static VRCAvatarDescriptor FindOwningDescriptor(Component component)
        {
            return component != null
                ? component.GetComponentInParent<VRCAvatarDescriptor>(true)
                : null;
        }

        internal static List<BlendShapeMeshMerge> GetOwnedMarkers(VRCAvatarDescriptor descriptor)
        {
            return descriptor.GetComponentsInChildren<BlendShapeMeshMerge>(true)
                .Where(marker => marker != null && FindOwningDescriptor(marker) == descriptor)
                .ToList();
        }

        /// <summary>
        /// Runs every merge group (one BlendShapeMeshMerge marker = one group)
        /// owned by this avatar. The complete plan is validated and dependency
        /// sorted before any renderer is changed.
        /// Throws on unrecoverable problems. Returns the number of meshes merged.
        /// </summary>
        public static int ProcessAvatar(GameObject avatarRoot, bool saveAssets)
        {
            // Tools that add blendshapes to the avatar's own meshes must finish
            // first, while the accessory meshes are still separate.
            PreMergeInjectorBridge.Run(avatarRoot, saveAssets);

            var descriptor = avatarRoot.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null)
                descriptor = avatarRoot.GetComponentInChildren<VRCAvatarDescriptor>(true);
            if (descriptor == null)
                throw new Exception("No VRCAvatarDescriptor found on the avatar.");

            var plans = BuildMergePlans(descriptor);
            if (plans.Count == 0) return 0;

            // Capture the avatar-side lookup before any source is consumed.
            var avatarBoneLookup = BuildAvatarBoneLookup(descriptor, plans);

            int merged = 0;
            var modifiedTargets = new HashSet<SkinnedMeshRenderer>();
            var generatedMeshes = new HashSet<Mesh>();
            foreach (var plan in plans)
            {
                merged += MergeOne(descriptor, plan.Target, plan.IsFaceTarget, plan.Marker,
                    plan.Sources, plan.ConnectMatchingBones, avatarBoneLookup, generatedMeshes);
                modifiedTargets.Add(plan.Target);
            }

            if (merged > 0 && saveAssets)
            {
                var consumedTargets = new HashSet<SkinnedMeshRenderer>(
                    plans.SelectMany(plan => plan.Sources));
                foreach (var t in modifiedTargets)
                    if (t != null && !consumedTargets.Contains(t))
                        SaveGeneratedMesh(t, avatarRoot.name);
            }

            return merged;
        }

        internal static List<MergePlan> BuildMergePlans(VRCAvatarDescriptor descriptor, bool deepMeshValidation = true)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));

            var markers = GetOwnedMarkers(descriptor);
            var plans = new List<MergePlan>(markers.Count);
            for (int i = 0; i < markers.Count; i++)
            {
                var marker = markers[i];
                var target = ResolveTarget(descriptor, marker, out bool isFaceTarget);
                ValidateRendererForMerge(target, "target", deepMeshValidation);

                bool selectedMode = marker.EffectiveSourceSelectionMode ==
                                    BlendShapeMeshMerge.SourceSelectionMode.SelectedMeshes;
                if (selectedMode)
                {
                    if (marker.sourceRenderers == null || marker.sourceRenderers.Count == 0)
                        throw new Exception($"'{marker.name}': add at least one Accessory Mesh, or choose All Child Meshes.");
                    if (marker.sourceRenderers.Any(source => source == null))
                        throw new Exception($"'{marker.name}': an Accessory Mesh row is empty. Assign a mesh or remove the row.");
                    if (marker.sourceRenderers.Distinct().Count() != marker.sourceRenderers.Count)
                        throw new Exception($"'{marker.name}': the same Accessory Mesh is listed more than once.");
                }

                var sources = ResolveSources(marker, target);
                if (sources.Count == 0)
                    throw new Exception($"'{marker.name}': no accessory mesh was found.");

                foreach (var source in sources)
                {
                    if (source == target)
                        throw new Exception($"'{marker.name}': '{source.name}' cannot be both the Target Mesh and an Accessory Mesh.");
                    if (FindOwningDescriptor(source) != descriptor)
                        throw new Exception($"'{marker.name}': accessory mesh '{source.name}' is outside avatar '{descriptor.name}'.");
                    if (!isFaceTarget && source == descriptor.VisemeSkinnedMesh)
                        throw new Exception($"'{marker.name}': the avatar Face Mesh cannot be used as an Accessory Mesh.");
                    ValidateRendererForMerge(source, "accessory", deepMeshValidation);
                    Matrix4x4 sourceToTarget = target.transform.worldToLocalMatrix *
                                               source.transform.localToWorldMatrix;
                    if (!HasUsableSpaceTransform(sourceToTarget))
                        throw new Exception($"'{marker.name}': accessory mesh '{source.name}' has a zero or invalid " +
                                            "transform scale.");
                }

                bool connectMatchingBones = ShouldConnectMatchingBones(marker, sources);
                if (connectMatchingBones && marker.attachToBone)
                {
                    var animator = descriptor.GetComponent<Animator>();
                    if (animator == null || !animator.isHuman)
                        throw new Exception($"'{marker.name}': Attach Extra Bone Chains requires a humanoid Animator.");
                    if (animator.GetBoneTransform(marker.attachBone) == null)
                        throw new Exception($"'{marker.name}': the avatar does not contain the selected Attach To bone.");
                }

                ValidateShapeMappings(marker, target, sources);
                plans.Add(new MergePlan
                {
                    Marker = marker,
                    Target = target,
                    IsFaceTarget = isFaceTarget,
                    Sources = sources,
                    ConnectMatchingBones = connectMatchingBones,
                    OriginalOrder = i
                });
            }

            var sourceOwners = new Dictionary<SkinnedMeshRenderer, BlendShapeMeshMerge>();
            foreach (var plan in plans)
            foreach (var source in plan.Sources)
            {
                if (sourceOwners.TryGetValue(source, out var owner))
                    throw new Exception($"Accessory mesh '{source.name}' is selected by both '{owner.name}' and " +
                                        $"'{plan.Marker.name}'. Each mesh can belong to only one merge group.");
                sourceOwners.Add(source, plan.Marker);
            }

            return SortPlansByDependency(plans);
        }

        static List<MergePlan> SortPlansByDependency(List<MergePlan> plans)
        {
            var incoming = plans.ToDictionary(plan => plan, _ => 0);
            var outgoing = plans.ToDictionary(plan => plan, _ => new HashSet<MergePlan>());

            foreach (var consumer in plans)
            foreach (var source in consumer.Sources)
            foreach (var producer in plans)
            {
                if (producer == consumer || producer.Target != source) continue;
                if (outgoing[producer].Add(consumer)) incoming[consumer]++;
            }

            var ready = plans.Where(plan => incoming[plan] == 0)
                .OrderBy(plan => plan.IsFaceTarget ? 1 : 0)
                .ThenBy(plan => plan.OriginalOrder)
                .ToList();
            var sorted = new List<MergePlan>(plans.Count);
            while (ready.Count > 0)
            {
                var next = ready[0];
                ready.RemoveAt(0);
                sorted.Add(next);
                foreach (var dependent in outgoing[next])
                {
                    incoming[dependent]--;
                    if (incoming[dependent] != 0) continue;
                    ready.Add(dependent);
                    ready = ready.OrderBy(plan => plan.IsFaceTarget ? 1 : 0)
                        .ThenBy(plan => plan.OriginalOrder)
                        .ToList();
                }
            }

            if (sorted.Count != plans.Count)
                throw new Exception("Merge groups contain a target/source cycle. Remove the circular mesh references.");
            return sorted;
        }

        /// <summary>The renderer a group merges into: the descriptor's face mesh
        /// (default) or the marker's custom target.</summary>
        internal static SkinnedMeshRenderer ResolveTarget(
            VRCAvatarDescriptor descriptor, BlendShapeMeshMerge marker, out bool isFaceTarget)
        {
            isFaceTarget = marker.mergeInto == BlendShapeMeshMerge.TargetMode.FaceMesh;
            if (isFaceTarget)
            {
                var face = descriptor.VisemeSkinnedMesh;
                if (descriptor.lipSync != VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape || face == null)
                    throw new Exception($"'{marker.name}': Face Mesh is unavailable. Set the avatar's Lip Sync to " +
                                        "Viseme Blend Shape and assign its Face Mesh, or choose Specific Avatar Mesh.");
                if (face.sharedMesh == null)
                    throw new Exception($"'{marker.name}': Face Mesh '{face.name}' has no mesh assigned.");
                return face;
            }

            var target = marker.targetRenderer;
            if (target == null)
                throw new Exception($"'{marker.name}': choose a Target Mesh.");
            if (target == descriptor.VisemeSkinnedMesh)
                throw new Exception($"'{marker.name}': this is the avatar's Face Mesh. Choose Face Mesh under " +
                                    "Merge Into to enable speech-shape matching.");
            if (target.sharedMesh == null)
                throw new Exception($"'{marker.name}': Target Mesh '{target.name}' has no mesh assigned.");
            if (FindOwningDescriptor(target) != descriptor)
                throw new Exception($"'{marker.name}': Target Mesh '{target.name}' is not part of avatar '{descriptor.name}'.");
            return target;
        }

        // ------------------------------------------------------------

        /// <summary>Side-effect-free source discovery used by build validation and
        /// by the inspector.</summary>
        internal static List<SkinnedMeshRenderer> ResolveSources(BlendShapeMeshMerge marker, SkinnedMeshRenderer mergeTarget)
        {
            var sources = new List<SkinnedMeshRenderer>();
            bool selected = marker.EffectiveSourceSelectionMode ==
                            BlendShapeMeshMerge.SourceSelectionMode.SelectedMeshes;
            if (selected && marker.sourceRenderers != null)
                foreach (var r in marker.sourceRenderers)
                    if (r != null && !sources.Contains(r)) sources.Add(r);
            if (!selected)
                sources.AddRange(marker.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                    .Where(renderer => renderer.GetComponentInParent<BlendShapeMeshMerge>(true) == marker));
            return sources;
        }

        internal static bool ShouldConnectMatchingBones(BlendShapeMeshMerge marker,
            IEnumerable<SkinnedMeshRenderer> sources)
        {
            switch (marker.EffectiveBoneConnectionMode)
            {
                case BlendShapeMeshMerge.BoneConnectionMode.Automatic:
                    return !HasExternalArmatureLink(marker, sources);
                case BlendShapeMeshMerge.BoneConnectionMode.UseThisTool:
                    return true;
                case BlendShapeMeshMerge.BoneConnectionMode.DoNotConnect:
                    return false;
                default:
                    return marker.remapBonesByName;
            }
        }

        internal static bool HasExternalArmatureLink(BlendShapeMeshMerge marker,
            IEnumerable<SkinnedMeshRenderer> sources = null)
        {
            if (marker == null) return false;
            var sourceList = sources?.Where(source => source != null).Distinct().ToList()
                             ?? ResolveSources(marker, null);
            var candidates = new HashSet<MonoBehaviour>();

            void AddComponents(GameObject gameObject)
            {
                if (gameObject == null) return;
                foreach (var component in gameObject.GetComponents<MonoBehaviour>())
                    if (component != null) candidates.Add(component);
            }

            foreach (var component in marker.GetComponentsInChildren<MonoBehaviour>(true))
                if (component != null) candidates.Add(component);

            var descriptor = FindOwningDescriptor(marker);
            void AddAncestorComponents(Transform transform)
            {
                while (transform != null)
                {
                    AddComponents(transform.gameObject);
                    if (descriptor != null && transform == descriptor.transform) break;
                    transform = transform.parent;
                }
            }

            AddAncestorComponents(marker.transform);
            foreach (var source in sourceList)
            {
                AddAncestorComponents(source.transform);
                AddAncestorComponents(source.rootBone);
                foreach (var bone in source.bones)
                    if (bone != null) AddAncestorComponents(bone);
            }

            foreach (var candidate in candidates)
                if (TryGetExternalLinkRoot(candidate, out var linkRoot) &&
                    IsLinkRootRelevant(marker, sourceList, linkRoot))
                    return true;
            return false;
        }

        static bool TryGetExternalLinkRoot(MonoBehaviour component, out Transform linkRoot)
        {
            linkRoot = null;
            if (component == null) return false;
            Type componentType = component.GetType();

            // Modular armature components are regular MonoBehaviours. Match by
            // type name so this tool has no compile-time package dependency.
            if (componentType.Name == "ModularAvatarMergeArmature" &&
                componentType.Namespace != null &&
                componentType.Namespace.IndexOf("modular", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                linkRoot = component.transform;
                return true;
            }

            // VRCFury stores features in a generic component. Inspect its public
            // feature list through reflection, again without adding a dependency.
            if (componentType.FullName != "VF.Model.VRCFury") return false;
            try
            {
                MethodInfo getFeatures = componentType.GetMethod("GetAllFeatures",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!(getFeatures?.Invoke(component, null) is IEnumerable features)) return false;
                foreach (object feature in features)
                {
                    if (feature?.GetType().FullName != "VF.Model.Feature.ArmatureLink") continue;
                    FieldInfo propBoneField = feature.GetType().GetField("propBone",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var propBone = propBoneField?.GetValue(feature) as GameObject;
                    linkRoot = propBone != null ? propBone.transform : component.transform;
                    return true;
                }
            }
            catch
            {
                // A changed external package API should never break avatar builds.
                // The user can still select This Tool or Do Not Connect manually.
            }
            return false;
        }

        static bool IsLinkRootRelevant(BlendShapeMeshMerge marker,
            List<SkinnedMeshRenderer> sources, Transform linkRoot)
        {
            if (linkRoot == null) return false;
            if (linkRoot == marker.transform || linkRoot.IsChildOf(marker.transform)) return true;

            foreach (var source in sources)
            {
                if (source.transform == linkRoot || source.transform.IsChildOf(linkRoot)) return true;
                if (source.rootBone == linkRoot ||
                    (source.rootBone != null && source.rootBone.IsChildOf(linkRoot))) return true;
                foreach (var bone in source.bones)
                    if (bone == linkRoot || (bone != null && bone.IsChildOf(linkRoot))) return true;
            }
            return false;
        }

        internal static void ValidateRendererForMerge(SkinnedMeshRenderer renderer, string role,
            bool deepMeshValidation = true)
        {
            if (renderer == null)
                throw new Exception($"A {role} renderer is missing.");
            var mesh = renderer.sharedMesh;
            if (mesh == null)
                throw new Exception($"{role} renderer '{renderer.name}' has no mesh assigned.");
            if (!mesh.isReadable)
                throw new Exception($"Mesh '{mesh.name}' on {role} renderer '{renderer.name}' is not readable. " +
                                    "Enable Read/Write in its import settings.");
            if (mesh.vertexCount == 0)
                throw new Exception($"Mesh '{mesh.name}' on {role} renderer '{renderer.name}' has no vertices.");
            if (mesh.subMeshCount == 0)
                throw new Exception($"Mesh '{mesh.name}' on {role} renderer '{renderer.name}' has no submeshes.");
            for (int i = 0; i < mesh.subMeshCount; i++)
                if (mesh.GetTopology(i) != MeshTopology.Triangles)
                    throw new Exception($"Mesh '{mesh.name}' submesh {i + 1} uses {mesh.GetTopology(i)}. " +
                                        "Only triangle submeshes can be merged.");

            if (!deepMeshValidation) return;

            for (int shape = 0; shape < mesh.blendShapeCount; shape++)
            {
                int frames = mesh.GetBlendShapeFrameCount(shape);
                if (frames == 0)
                    throw new Exception($"Blendshape '{mesh.GetBlendShapeName(shape)}' on mesh '{mesh.name}' has no frames.");
                float previous = float.NegativeInfinity;
                for (int frame = 0; frame < frames; frame++)
                {
                    float weight = mesh.GetBlendShapeFrameWeight(shape, frame);
                    if (float.IsNaN(weight) || float.IsInfinity(weight))
                        throw new Exception($"Blendshape '{mesh.GetBlendShapeName(shape)}' on mesh '{mesh.name}' " +
                                            "has an invalid frame weight.");
                    if (frame > 0 && weight <= previous)
                        throw new Exception($"Blendshape '{mesh.GetBlendShapeName(shape)}' on mesh '{mesh.name}' " +
                                            "does not have strictly increasing frame weights.");
                    previous = weight;
                }
            }

            var bindposes = mesh.bindposes;
            using (var bonesPerVertex = mesh.GetBonesPerVertex())
            using (var allWeights = mesh.GetAllBoneWeights())
            {
                bool hasSkin = bindposes.Length > 0 || allWeights.Length > 0;
                if (!hasSkin) return; // supported as a rigid one-bone mesh
                if (bindposes.Length == 0 || bonesPerVertex.Length != mesh.vertexCount ||
                    renderer.bones == null || renderer.bones.Length < bindposes.Length)
                    throw new Exception($"Mesh '{mesh.name}' on renderer '{renderer.name}' has incomplete skinning data.");

                int expectedWeightCount = 0;
                for (int i = 0; i < bonesPerVertex.Length; i++)
                {
                    if (bonesPerVertex[i] == 0)
                        throw new Exception($"Mesh '{mesh.name}' has a vertex with no bone influence.");
                    expectedWeightCount += bonesPerVertex[i];
                }
                if (expectedWeightCount != allWeights.Length)
                    throw new Exception($"Mesh '{mesh.name}' on renderer '{renderer.name}' has inconsistent bone weights.");

                int weightCursor = 0;
                for (int vertex = 0; vertex < bonesPerVertex.Length; vertex++)
                {
                    float totalWeight = 0f;
                    int influenceCount = bonesPerVertex[vertex];
                    for (int influence = 0; influence < influenceCount; influence++)
                    {
                        var weight = allWeights[weightCursor++];
                        if (float.IsNaN(weight.weight) || float.IsInfinity(weight.weight) || weight.weight < 0f)
                            throw new Exception($"Mesh '{mesh.name}' on renderer '{renderer.name}' has an invalid " +
                                                $"bone weight at vertex {vertex}.");
                        if (weight.boneIndex < 0 || weight.boneIndex >= bindposes.Length)
                            throw new Exception($"Mesh '{mesh.name}' on renderer '{renderer.name}' uses an invalid " +
                                                $"bone index at vertex {vertex}.");
                        if (weight.weight > 0f && renderer.bones[weight.boneIndex] == null)
                            throw new Exception($"Renderer '{renderer.name}' has a missing bone used by mesh " +
                                                $"'{mesh.name}' at vertex {vertex}.");
                        totalWeight += weight.weight;
                    }

                    if (float.IsNaN(totalWeight) || float.IsInfinity(totalWeight) || totalWeight <= 0f)
                        throw new Exception($"Mesh '{mesh.name}' on renderer '{renderer.name}' has a vertex with " +
                                            $"no usable bone influence (vertex {vertex}).");
                    if (Mathf.Abs(totalWeight - 1f) > 0.01f)
                        throw new Exception($"Mesh '{mesh.name}' on renderer '{renderer.name}' has bone weights " +
                                            $"that do not add up to 1 at vertex {vertex}.");
                }
            }
        }

        static bool HasUsableSpaceTransform(Matrix4x4 matrix)
        {
            // Judge degeneracy relative to the three axis lengths. An absolute
            // determinant cutoff incorrectly rejects valid uniformly-small objects
            // (for example, scale 0.001 on every axis).
            for (int i = 0; i < 16; i++)
                if (float.IsNaN(matrix[i]) || float.IsInfinity(matrix[i]))
                    return false;

            float xLength = matrix.MultiplyVector(Vector3.right).magnitude;
            float yLength = matrix.MultiplyVector(Vector3.up).magnitude;
            float zLength = matrix.MultiplyVector(Vector3.forward).magnitude;
            if (float.IsNaN(xLength) || float.IsInfinity(xLength) || xLength <= 0.00000001f ||
                float.IsNaN(yLength) || float.IsInfinity(yLength) || yLength <= 0.00000001f ||
                float.IsNaN(zLength) || float.IsInfinity(zLength) || zLength <= 0.00000001f)
                return false;

            double axisVolume = (double)xLength * yLength * zLength;
            double relativeVolume = Math.Abs((double)matrix.determinant) / axisVolume;
            return !double.IsNaN(relativeVolume) && !double.IsInfinity(relativeVolume) &&
                   relativeVolume > 0.000001d;
        }

        static void ValidateShapeMappings(BlendShapeMeshMerge marker, SkinnedMeshRenderer target,
            List<SkinnedMeshRenderer> sources)
        {
            if (marker.shapeMappings == null || marker.shapeMappings.Count == 0) return;

            var targetNames = new HashSet<string>();
            for (int i = 0; i < target.sharedMesh.blendShapeCount; i++)
                targetNames.Add(target.sharedMesh.GetBlendShapeName(i));

            var sourceNames = new HashSet<string>();
            foreach (var source in sources)
                for (int i = 0; i < source.sharedMesh.blendShapeCount; i++)
                    sourceNames.Add(source.sharedMesh.GetBlendShapeName(i));

            var mappedSources = new HashSet<string>();
            for (int i = 0; i < marker.shapeMappings.Count; i++)
            {
                var mapping = marker.shapeMappings[i];
                string row = $"Different-Name Match {i + 1}";
                if (mapping == null || string.IsNullOrWhiteSpace(mapping.sourceShape) ||
                    string.IsNullOrWhiteSpace(mapping.targetShape))
                    throw new Exception($"'{marker.name}': {row} is incomplete. Choose both shapes or remove the row.");
                if (!sourceNames.Contains(mapping.sourceShape))
                    throw new Exception($"'{marker.name}': {row} accessory shape '{mapping.sourceShape}' was not found.");
                if (!targetNames.Contains(mapping.targetShape))
                    throw new Exception($"'{marker.name}': {row} avatar shape '{mapping.targetShape}' was not found.");
                if (!mappedSources.Add(mapping.sourceShape))
                    throw new Exception($"'{marker.name}': accessory shape '{mapping.sourceShape}' is mapped more than once.");
            }
        }

        static int MergeOne(VRCAvatarDescriptor descriptor, SkinnedMeshRenderer mergeTarget, bool isFaceTarget,
            BlendShapeMeshMerge marker, List<SkinnedMeshRenderer> sources, bool connectMatchingBones,
            Dictionary<string, Transform> avatarBoneLookup, HashSet<Mesh> generatedMeshes)
        {
            var mergedCustomBones = new HashSet<Transform>();
            var mergedPhysBoneRoots = new HashSet<Transform>();
            foreach (var src in sources)
                MergeRenderer(descriptor, mergeTarget, marker, src, isFaceTarget, avatarBoneLookup,
                    mergedCustomBones, mergedPhysBoneRoots, generatedMeshes, connectMatchingBones);

            // Bone placement is scoped only to bones used by merged meshes.
            // Sibling meshes that stay separate are never touched.
            if (connectMatchingBones)
                DistributeMergedBones(descriptor, marker, avatarBoneLookup, mergedCustomBones, mergedPhysBoneRoots);

            UnityEngine.Object.DestroyImmediate(marker);
            return sources.Count;
        }

        /// <summary>
        /// Name-based placement scoped to the bones the merged meshes use (passed
        /// in) — never the whole marker subtree, so kept-separate
        /// meshes under the same marker are left untouched. A used bone whose parent
        /// maps to an avatar bone is reparented (world-preserving) under it; a used
        /// bone whose parent is a non-bone container (chain/orphan root) is anchored
        /// under Attach Bone; a used bone whose parent is another used bone travels
        /// with that parent.
        /// </summary>
        static void DistributeMergedBones(VRCAvatarDescriptor descriptor, BlendShapeMeshMerge marker,
            Dictionary<string, Transform> lookup, HashSet<Transform> customBones, HashSet<Transform> physBoneRoots)
        {
            if (customBones.Count == 0) return;

            // Renderer.bones need not contain PhysBone roots, non-deforming helper
            // transforms, or every intermediate link. Preserve the path only as far
            // as the relevant PhysBone root. Walking above it would retain the whole
            // prop container and prevent the root itself from following the avatar.
            var hierarchy = new HashSet<Transform>(customBones.Where(t => t != null));
            var localPhysBoneRoots = new HashSet<Transform>(
                physBoneRoots.Where(root => root != null && !lookup.ContainsValue(root)));
            foreach (var root in localPhysBoneRoots) hierarchy.Add(root);

            Transform FindContainingPhysBoneRoot(Transform bone)
            {
                Transform nearest = null;
                foreach (var root in localPhysBoneRoots)
                {
                    if (bone == root) return root;
                    if (!bone.IsChildOf(root)) continue;
                    if (nearest == null || root.IsChildOf(nearest)) nearest = root;
                }
                return nearest;
            }

            foreach (var bone in hierarchy.ToArray())
            {
                var physBoneRoot = FindContainingPhysBoneRoot(bone);
                if (bone == physBoneRoot) continue;

                var parent = bone.parent;
                while (parent != null && !lookup.ContainsValue(parent))
                {
                    bool insidePhysBoneChain = physBoneRoot != null &&
                                               (parent == physBoneRoot || parent.IsChildOf(physBoneRoot));
                    if (!insidePhysBoneChain && lookup.ContainsKey(parent.name)) break;
                    hierarchy.Add(parent);
                    if (parent == physBoneRoot) break;
                    parent = parent.parent;
                }
            }

            Transform attachFallback = null;
            if (marker.attachToBone)
            {
                var animator = descriptor.GetComponent<Animator>();
                if (animator != null && animator.isHuman)
                    attachFallback = animator.GetBoneTransform(marker.attachBone);
            }

            // Resolve targets first (by ORIGINAL parent), then reparent, so mutating
            // the hierarchy can't change what a later bone's parent maps to.
            var moves = new List<(Transform bone, Transform parent)>();
            foreach (var t in hierarchy)
            {
                if (t == null || t.parent == null) continue;
                // A PhysBone may deliberately use a duplicate humanoid bone (for
                // example a prop's own "Head") as its root. Keep that transform so
                // the component still drives its children, but attach the root under
                // the matching avatar bone rather than remapping it away.
                if (localPhysBoneRoots.Contains(t) && lookup.TryGetValue(t.name, out var matchingAvatarBone)
                    && matchingAvatarBone != t)
                    moves.Add((t, matchingAvatarBone));
                else if (hierarchy.Contains(t.parent))
                    continue; // keep children attached to their source/PhysBone chain
                else if (lookup.TryGetValue(t.parent.name, out var avatarParent))
                    moves.Add((t, avatarParent));                 // parent is a matched avatar bone
                else if (attachFallback != null)
                    moves.Add((t, attachFallback));               // chain/orphan root -> fallback anchor
                // else: parent is another merged bone -> it travels with that parent
            }
            foreach (var (bone, parent) in moves)
                if (bone.parent != parent) bone.SetParent(parent, worldPositionStays: true);

            if (moves.Count > 0)
                Debug.Log(LogPrefix + $"'{marker.name}': placed {moves.Count} custom bone(s) " +
                          "onto their matching avatar bones (armature-link style).");
        }

        // 'face' is the merge target: the descriptor's face mesh by default, or
        // any custom renderer when the group targets one. Unmatched source bones
        // are added to customBones for scoped armature-link distribution.
        static void MergeRenderer(VRCAvatarDescriptor descriptor, SkinnedMeshRenderer face,
            BlendShapeMeshMerge marker, SkinnedMeshRenderer src, bool isFaceTarget,
            Dictionary<string, Transform> avatarBoneLookup, HashSet<Transform> customBones,
            HashSet<Transform> physBoneRoots, HashSet<Mesh> generatedMeshes,
            bool connectMatchingBones)
        {
            var tMesh = face.sharedMesh;
            var sMesh = src.sharedMesh;
            int tV = tMesh.vertexCount;
            int sV = sMesh.vertexCount;

            // Convert every source-local value into the target renderer's local
            // space. Without this, differently positioned/scaled renderers jump or
            // deform after being merged.
            Matrix4x4 sourceToTarget = face.transform.worldToLocalMatrix * src.transform.localToWorldMatrix;
            float transformDeterminant = sourceToTarget.determinant;
            if (!HasUsableSpaceTransform(sourceToTarget))
                throw new Exception($"'{src.name}' has a zero or invalid transform scale and cannot be merged safely.");
            Matrix4x4 targetToSource = sourceToTarget.inverse;
            Matrix4x4 sourceNormalToTarget = targetToSource.transpose;

            // --- 2. Skinning data (synthesize a single bone for rigid renderers)
            var targetSkin = GetSkin(tMesh, face);
            var sourceSkin = GetSkin(sMesh, src);
            var tBones = targetSkin.Bones;
            var tBinds = targetSkin.Bindposes;
            var sBones = sourceSkin.Bones;
            var sBinds = sourceSkin.Bindposes;
            bool sSkinned = sourceSkin.IsSkinned;

            // A driven bone must remain the exact transform the PhysBone animates.
            // Name-remapping it would make the simulation run while the mesh follows
            // a different, same-named avatar transform and therefore appears static.
            var sourcePhysBoneRoots = FindPhysBoneRoots(descriptor, sBones);
            foreach (var root in sourcePhysBoneRoots) physBoneRoots.Add(root);

            // Armature-link style bone remap, preserving the current world-space fit:
            // newBind = target.worldToLocal * srcBone.localToWorld * srcBind.
            // Bones that DON'T match an avatar bone are this mesh's own custom bones
            // (jiggle / PhysBone chains); collect them so only THEY get distributed —
            // never bones belonging to sibling meshes we aren't merging.
            if (sSkinned && connectMatchingBones)
            {
                for (int i = 0; i < sBones.Length; i++)
                {
                    var b = sBones[i];
                    if (b == null) continue;
                    if (IsAtOrBelowAny(b, sourcePhysBoneRoots))
                    {
                        customBones.Add(b);
                    }
                    else if (avatarBoneLookup.TryGetValue(b.name, out var target))
                    {
                        if (target != b)
                        {
                            sBinds[i] = target.worldToLocalMatrix * b.localToWorldMatrix * sBinds[i];
                            sBones[i] = target;
                        }
                    }
                    else
                    {
                        customBones.Add(b);
                    }
                }
            }

            // Source bindposes were authored in source-renderer local space.
            // Account for the geometry conversion above.
            for (int i = 0; i < sBinds.Length; i++)
                sBinds[i] = sBinds[i] * targetToSource;

            int boneOffset = tBinds.Length;
            var bones = new Transform[boneOffset + sBinds.Length];
            var binds = new Matrix4x4[boneOffset + sBinds.Length];
            Array.Copy(tBones, bones, boneOffset);
            Array.Copy(tBinds, binds, boneOffset);
            Array.Copy(sBones, 0, bones, boneOffset, sBones.Length);
            Array.Copy(sBinds, 0, binds, boneOffset, sBinds.Length);

            var bonesPerVertex = Combine(targetSkin.BonesPerVertex, sourceSkin.BonesPerVertex,
                tV, sV, (byte)0);
            var weights = new BoneWeight1[targetSkin.Weights.Length + sourceSkin.Weights.Length];
            Array.Copy(targetSkin.Weights, weights, targetSkin.Weights.Length);
            for (int i = 0; i < sourceSkin.Weights.Length; i++)
            {
                var weight = sourceSkin.Weights[i];
                weight.boneIndex += boneOffset;
                weights[targetSkin.Weights.Length + i] = weight;
            }

            // --- 3. Geometry
            var m = new Mesh { name = tMesh.name + "_BlendShapeMerged" };
            m.indexFormat = (tV + sV) > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;

            m.vertices = Combine(tMesh.vertices, TransformPoints(sMesh.vertices, sourceToTarget),
                tV, sV, Vector3.zero);
            var nrm = CombineOptional(tMesh.normals,
                TransformNormals(sMesh.normals, sourceNormalToTarget), tV, sV, Vector3.up);
            if (nrm != null) m.normals = nrm;
            var tan = CombineOptional(tMesh.tangents,
                TransformTangents(sMesh.tangents, sourceToTarget, transformDeterminant < 0f),
                tV, sV, new Vector4(1, 0, 0, -1));
            if (tan != null) m.tangents = tan;
            var col = CombineOptional(tMesh.colors, sMesh.colors, tV, sV, Color.white);
            if (col != null) m.colors = col;
            CopyUvChannels(m, tMesh, sMesh, tV, sV);

            m.bindposes = binds;
            using (var nativeCounts = new NativeArray<byte>(bonesPerVertex, Allocator.Temp))
            using (var nativeWeights = new NativeArray<BoneWeight1>(weights, Allocator.Temp))
                m.SetBoneWeights(nativeCounts, nativeWeights);

            // --- 4. Submeshes + materials. Face slots keep their order/indices so
            //        slot-index material animations stay valid. Source submeshes
            //        that use a material already present on the face mesh fold into
            //        that slot (no extra draw call); the rest become new slots.
            int tSub = tMesh.subMeshCount;
            int sSub = sMesh.subMeshCount;
            var tMats = face.sharedMaterials ?? Array.Empty<Material>();
            var sMats = src.sharedMaterials ?? Array.Empty<Material>();

            var slotTris = new List<List<int>>(Math.Max(tSub, tMats.Length) + Math.Max(sSub, sMats.Length));
            var slotMats = new List<Material>(slotTris.Capacity);
            for (int i = 0; i < tSub; i++)
            {
                slotTris.Add(new List<int>(tMesh.GetTriangles(i)));
                slotMats.Add(i < tMats.Length ? tMats[i] : null);
            }

            // Unity renders material slots beyond the submesh count as extra passes
            // over the final submesh. Turn those passes into explicit duplicate
            // submeshes so they still address the same target geometry after source
            // submeshes are appended.
            if (tMats.Length > tSub)
            {
                var targetLast = tMesh.GetTriangles(tSub - 1);
                for (int i = tSub; i < tMats.Length; i++)
                {
                    slotTris.Add(new List<int>(targetLast));
                    slotMats.Add(tMats[i]);
                }
            }

            int[] sourceLast = null;
            for (int i = 0; i < sSub; i++)
            {
                var mat = i < sMats.Length ? sMats[i] : null;
                var tris = sMesh.GetTriangles(i);
                if (transformDeterminant < 0f)
                    for (int triangle = 0; triangle < tris.Length; triangle += 3)
                    {
                        int swap = tris[triangle + 1];
                        tris[triangle + 1] = tris[triangle + 2];
                        tris[triangle + 2] = swap;
                    }
                for (int j = 0; j < tris.Length; j++) tris[j] += tV;
                if (i == sSub - 1) sourceLast = tris;
                int slot = mat != null ? slotMats.IndexOf(mat) : -1;
                if (slot >= 0)
                {
                    slotTris[slot].AddRange(tris);
                    Debug.Log(LogPrefix + $"'{src.name}' submesh {i} shares material '{mat.name}' with an " +
                              $"existing slot; folded into slot {slot} (saves a draw call).");
                }
                else
                {
                    slotTris.Add(new List<int>(tris));
                    slotMats.Add(mat);
                }
            }

            if (sMats.Length > sSub && sourceLast != null)
                for (int i = sSub; i < sMats.Length; i++)
                {
                    slotTris.Add(new List<int>(sourceLast));
                    slotMats.Add(sMats[i]);
                }

            m.subMeshCount = slotTris.Count;
            for (int i = 0; i < slotTris.Count; i++)
                m.SetTriangles(slotTris[i], i, false);
            var mats = slotMats.ToArray();

            // --- 5. Blendshapes: same-named shapes are combined into one.
            //        A name map lets differently-named source shapes merge into
            //        target shapes (viseme auto-map on face targets + manual pairs).
            var nameMap = BuildShapeNameMap(descriptor, marker, sMesh, tMesh, isFaceTarget);
            int mergedShapeCount = MergeBlendShapes(m, tMesh, sMesh, tV, sV, nameMap,
                sourceToTarget, sourceNormalToTarget, out var appendedSourceShapes);
            if (mergedShapeCount == 0 && sMesh.blendShapeCount > 0 && isFaceTarget)
                Debug.LogWarning(LogPrefix + $"'{sMesh.name}': none of its {sMesh.blendShapeCount} blendshape(s) matched " +
                                  $"a face mesh shape (by name, viseme auto-map, or manual mapping). " +
                                  "The accessory will not follow speech or same-name expressions. " +
                                  "Check the shape names or add Shape Mappings on the component.");

            m.RecalculateBounds();

            // --- 6. Preserve current blendshape weights, then swap in the merged mesh
            int tShapes = tMesh.blendShapeCount;
            var savedWeights = new float[tShapes];
            for (int i = 0; i < tShapes; i++) savedWeights[i] = face.GetBlendShapeWeight(i);
            var savedTargetBounds = face.localBounds;

            face.sharedMesh = m;
            generatedMeshes.Add(m);
            face.bones = bones;
            face.sharedMaterials = mats;

            for (int i = 0; i < tShapes; i++) face.SetBlendShapeWeight(i, savedWeights[i]);
            foreach (var pair in appendedSourceShapes) // (sourceIndex, mergedIndex)
                face.SetBlendShapeWeight(pair.Value, src.GetBlendShapeWeight(pair.Key));

            if (generatedMeshes.Remove(tMesh))
                UnityEngine.Object.DestroyImmediate(tMesh);

            // --- 7. Expand renderer bounds to cover the accessory
            // Preserve prior expansions when several sources merge sequentially;
            // assigning sharedMesh can reset the renderer's custom local bounds.
            var lb = savedTargetBounds;
            var sb = src.localBounds;
            for (int c = 0; c < 8; c++)
            {
                var corner = sb.center + Vector3.Scale(sb.extents, new Vector3(
                    (c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1));
                // localBounds belongs to the renderer's transform, not rootBone.
                lb.Encapsulate(face.transform.InverseTransformPoint(src.transform.TransformPoint(corner)));
            }
            face.localBounds = lb;

            // Remove only the renderer. Its transform can still be a live skin or
            // PhysBone transform, so deleting the GameObject is never safe here.
            UnityEngine.Object.DestroyImmediate(src);
            bool destroySourceMesh = generatedMeshes.Remove(sMesh);

            Debug.Log(LogPrefix + $"Merged '{sMesh.name}' ({sV} verts) into '{face.name}'" +
                      (isFaceTarget ? " (face mesh)" : "") +
                      $" — {mergedShapeCount} blendshape(s) combined by name.");
            if (destroySourceMesh)
                UnityEngine.Object.DestroyImmediate(sMesh);
        }

        // ------------------------------------------------------------
        //  Skinning helpers
        // ------------------------------------------------------------

        sealed class SkinData
        {
            public Transform[] Bones;
            public Matrix4x4[] Bindposes;
            public byte[] BonesPerVertex;
            public BoneWeight1[] Weights;
            public bool IsSkinned;
        }

        static SkinData GetSkin(Mesh mesh, SkinnedMeshRenderer smr)
        {
            var meshBinds = mesh.bindposes;
            var smrBones = smr.bones;
            byte[] counts;
            BoneWeight1[] weights;
            using (var nativeCounts = mesh.GetBonesPerVertex())
            using (var nativeWeights = mesh.GetAllBoneWeights())
            {
                counts = nativeCounts.ToArray();
                weights = nativeWeights.ToArray();
            }

            bool skinned = meshBinds.Length > 0 || weights.Length > 0;
            if (skinned)
            {
                if (meshBinds.Length == 0 || counts.Length != mesh.vertexCount ||
                    smrBones == null || smrBones.Length < meshBinds.Length)
                    throw new Exception($"Renderer '{smr.name}' has incomplete skinning data.");

                var bones = new Transform[meshBinds.Length];
                Array.Copy(smrBones, bones, meshBinds.Length);
                var usedBones = new bool[meshBinds.Length];
                foreach (var weight in weights)
                    if (weight.weight > 0f && weight.boneIndex >= 0 && weight.boneIndex < usedBones.Length)
                        usedBones[weight.boneIndex] = true;
                var unusedFallback = smr.rootBone != null ? smr.rootBone : smr.transform;
                for (int i = 0; i < bones.Length; i++)
                    if (bones[i] == null)
                    {
                        if (usedBones[i])
                            throw new Exception($"Renderer '{smr.name}' has a missing weighted bone at index {i}.");
                        bones[i] = unusedFallback;
                    }

                return new SkinData
                {
                    Bones = bones,
                    Bindposes = meshBinds,
                    BonesPerVertex = counts,
                    Weights = weights,
                    IsSkinned = true
                };
            }

            // Blendshape-only / rigid renderer: one synthetic bone at the
            // renderer transform reproduces its placement and animation.
            counts = Enumerable.Repeat((byte)1, mesh.vertexCount).ToArray();
            weights = new BoneWeight1[mesh.vertexCount];
            for (int i = 0; i < weights.Length; i++)
                weights[i] = new BoneWeight1 { boneIndex = 0, weight = 1f };
            return new SkinData
            {
                Bones = new[] { smr.transform },
                Bindposes = new[] { Matrix4x4.identity },
                BonesPerVertex = counts,
                Weights = weights,
                IsSkinned = false
            };
        }

        internal static List<Transform> FindPhysBoneRoots(VRCAvatarDescriptor descriptor, Transform[] sourceBones)
        {
            var roots = new HashSet<Transform>();
            foreach (var physBone in descriptor.GetComponentsInChildren<VRCPhysBone>(true))
            {
                if (FindOwningDescriptor(physBone) != descriptor) continue;
                var root = physBone.rootTransform != null ? physBone.rootTransform : physBone.transform;
                if (root == null) continue;
                foreach (var bone in sourceBones)
                {
                    if (bone == null || (bone != root && !bone.IsChildOf(root))) continue;
                    roots.Add(root);
                    break;
                }
            }
            return roots.ToList();
        }

        static bool IsAtOrBelowAny(Transform bone, List<Transform> roots)
        {
            foreach (var root in roots)
                if (bone == root || bone.IsChildOf(root)) return true;
            return false;
        }

        static Dictionary<string, Transform> BuildAvatarBoneLookup(
            VRCAvatarDescriptor descriptor, IEnumerable<MergePlan> plans)
        {
            // Prefer known avatar bones, and exclude accessory-owned transforms.
            // This works even when a marker sits on the avatar root or explicitly
            // selects a renderer outside its own subtree.
            var planList = plans.ToList();
            var consumedRenderers = new HashSet<SkinnedMeshRenderer>(
                planList.Where(plan => plan.Sources != null)
                    .SelectMany(plan => plan.Sources));
            var finalPlans = planList
                .Where(plan => plan.Target != null && !consumedRenderers.Contains(plan.Target))
                .ToList();

            var protectedTransforms = new HashSet<Transform> { descriptor.transform };
            var animator = descriptor.GetComponent<Animator>();
            if (animator != null && animator.isHuman)
                for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
                {
                    var bone = animator.GetBoneTransform((HumanBodyBones)i);
                    if (bone != null) protectedTransforms.Add(bone);
                }

            void ProtectRenderer(SkinnedMeshRenderer renderer)
            {
                if (renderer == null) return;
                protectedTransforms.Add(renderer.transform);
                if (renderer.rootBone != null) protectedTransforms.Add(renderer.rootBone);
                foreach (var bone in renderer.bones)
                    if (bone != null) protectedTransforms.Add(bone);
            }

            ProtectRenderer(descriptor.VisemeSkinnedMesh);
            foreach (var plan in finalPlans) ProtectRenderer(plan.Target);

            var excluded = new HashSet<Transform>();
            void ExcludeAccessoryPath(Transform transform)
            {
                // Explicitly selected meshes and bones can live outside the marker
                // subtree. Exclude their accessory-owned ancestor path too, stopping
                // as soon as it reaches a known avatar/final-target transform.
                while (transform != null && transform != descriptor.transform &&
                       !protectedTransforms.Contains(transform))
                {
                    excluded.Add(transform);
                    transform = transform.parent;
                }
            }

            foreach (var plan in planList)
            {
                var marker = plan.Marker;
                if (marker != null && !protectedTransforms.Contains(marker.transform))
                    foreach (var t in marker.GetComponentsInChildren<Transform>(true))
                        if (!protectedTransforms.Contains(t)) excluded.Add(t);

                foreach (var source in plan.Sources)
                {
                    if (source == null) continue;
                    ExcludeAccessoryPath(source.transform);
                    foreach (var bone in source.bones)
                        if (bone != null) ExcludeAccessoryPath(bone);
                }
            }

            var lookup = new Dictionary<string, Transform>();
            void Add(Transform t)
            {
                if (t == null || FindOwningDescriptor(t) != descriptor) return;
                if (!lookup.ContainsKey(t.name)) lookup.Add(t.name, t);
            }

            // Deterministic, high-confidence candidates win duplicate names.
            if (animator != null && animator.isHuman)
                for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
                    Add(animator.GetBoneTransform((HumanBodyBones)i));

            var faceRenderer = descriptor.VisemeSkinnedMesh;
            if (faceRenderer != null)
            {
                Add(faceRenderer.rootBone);
                foreach (var bone in faceRenderer.bones) Add(bone);
            }

            foreach (var plan in finalPlans.OrderByDescending(plan => plan.IsFaceTarget))
            {
                Add(plan.Target != null ? plan.Target.rootBone : null);
                if (plan.Target == null) continue;
                foreach (var bone in plan.Target.bones) Add(bone);
            }
            foreach (var t in descriptor.GetComponentsInChildren<Transform>(true))
            {
                if (FindOwningDescriptor(t) != descriptor) continue;
                if (excluded.Contains(t)) continue;
                Add(t);
            }
            return lookup;
        }

        // ------------------------------------------------------------
        //  Blendshape name mapping + merge
        // ------------------------------------------------------------

        // VRC_AvatarDescriptor.Viseme enum order (Oculus viseme set).
        static readonly string[] VisemeKeys =
            { "sil", "pp", "ff", "th", "dd", "kk", "ch", "ss", "nn", "rr", "aa", "e", "ih", "oh", "ou" };

        static string CanonicalShapeName(string s)
        {
            s = s.Trim().ToLowerInvariant();
            if (s.StartsWith("vrc.v_")) s = s.Substring(6);
            else if (s.StartsWith("v_")) s = s.Substring(2);
            return s;
        }

        /// <summary>
        /// Source shape name -> face shape name, for shapes that should merge
        /// despite different names. Exact same-name matches never need a map
        /// entry. Manual mappings win over viseme auto-mapping.
        /// </summary>
        internal static Dictionary<string, string> BuildShapeNameMap(
            VRCAvatarDescriptor descriptor, BlendShapeMeshMerge marker, Mesh sMesh, Mesh targetMesh,
            bool allowVisemeAutoMap)
        {
            var map = new Dictionary<string, string>();
            var srcNames = new List<string>(sMesh.blendShapeCount);
            for (int i = 0; i < sMesh.blendShapeCount; i++)
                srcNames.Add(sMesh.GetBlendShapeName(i));
            var targetNames = new HashSet<string>();
            for (int i = 0; i < targetMesh.blendShapeCount; i++)
                targetNames.Add(targetMesh.GetBlendShapeName(i));

            if (marker.shapeMappings != null)
                foreach (var sm in marker.shapeMappings)
                    if (sm != null && !string.IsNullOrEmpty(sm.sourceShape) && !string.IsNullOrEmpty(sm.targetShape)
                        && srcNames.Contains(sm.sourceShape) && targetNames.Contains(sm.targetShape)
                        && !map.ContainsKey(sm.sourceShape))
                        map.Add(sm.sourceShape, sm.targetShape);

            if (allowVisemeAutoMap && marker.autoMapVisemes && descriptor.VisemeBlendShapes != null)
            {
                var vb = descriptor.VisemeBlendShapes;
                for (int i = 0; i < vb.Length && i < VisemeKeys.Length; i++)
                {
                    string faceShape = vb[i];
                    if (string.IsNullOrEmpty(faceShape)) continue;
                    if (!targetNames.Contains(faceShape)) continue;
                    if (srcNames.Contains(faceShape)) continue; // exact match already merges
                    foreach (var s in srcNames)
                    {
                        if (map.ContainsKey(s)) continue;
                        if (CanonicalShapeName(s) == VisemeKeys[i]) { map.Add(s, faceShape); break; }
                    }
                }
            }
            return map;
        }

        sealed class ShapeFrames
        {
            public float[] Weights;
            public Vector3[][] Vertices;
            public Vector3[][] Normals;
            public Vector3[][] Tangents;
            public int VertexCount;
        }

        static int MergeBlendShapes(Mesh m, Mesh tMesh, Mesh sMesh, int tV, int sV,
            Dictionary<string, string> nameMap, Matrix4x4 sourceToTarget,
            Matrix4x4 sourceNormalToTarget, out Dictionary<int, int> appendedSourceShapes)
        {
            appendedSourceShapes = new Dictionary<int, int>(); // sourceIndex -> merged index
            int combined = 0;

            // Several differently named source shapes may intentionally map to one
            // target shape. Sum all of them instead of silently dropping all but one.
            var srcByName = new Dictionary<string, List<int>>();
            for (int i = 0; i < sMesh.blendShapeCount; i++)
            {
                string sourceName = sMesh.GetBlendShapeName(i);
                string targetName = nameMap.TryGetValue(sourceName, out var mapped) ? mapped : sourceName;
                if (!srcByName.TryGetValue(targetName, out var indices))
                    srcByName.Add(targetName, indices = new List<int>());
                indices.Add(i);
            }

            int total = tV + sV;
            var dv = new Vector3[total];
            var dn = new Vector3[total];
            var dt = new Vector3[total];
            var tdv = new Vector3[tV];
            var tdn = new Vector3[tV];
            var tdt = new Vector3[tV];
            var sdv = new Vector3[sV];
            var sdn = new Vector3[sV];
            var sdt = new Vector3[sV];
            var workV = new Vector3[sV];
            var workN = new Vector3[sV];
            var workT = new Vector3[sV];

            var handledSrc = new HashSet<int>();
            var usedNames = new HashSet<string>();

            string Unique(string name)
            {
                if (usedNames.Add(name)) return name;
                int n = 2;
                string renamed = name + " (dup)";
                while (!usedNames.Add(renamed)) renamed = name + $" (dup {n++})";
                Debug.LogWarning(LogPrefix + $"Duplicate blendshape name '{name}'; renamed the copy to '{renamed}'.");
                return renamed;
            }

            for (int ti = 0; ti < tMesh.blendShapeCount; ti++)
            {
                string originalName = tMesh.GetBlendShapeName(ti);
                string outputName = Unique(originalName);
                var targetFrames = ReadShapeFrames(tMesh, ti, tV, null, null);
                var sourceIndices = new List<int>();
                if (srcByName.TryGetValue(originalName, out var candidates))
                    foreach (var sourceIndex in candidates)
                        if (handledSrc.Add(sourceIndex)) sourceIndices.Add(sourceIndex);

                if (sourceIndices.Count == 0)
                {
                    for (int frame = 0; frame < targetFrames.Weights.Length; frame++)
                    {
                        Array.Copy(targetFrames.Vertices[frame], 0, dv, 0, tV);
                        Array.Copy(targetFrames.Normals[frame], 0, dn, 0, tV);
                        Array.Copy(targetFrames.Tangents[frame], 0, dt, 0, tV);
                        Array.Clear(dv, tV, sV);
                        Array.Clear(dn, tV, sV);
                        Array.Clear(dt, tV, sV);
                        m.AddBlendShapeFrame(outputName, targetFrames.Weights[frame], dv, dn, dt);
                    }
                    continue;
                }

                combined++;
                var sourceBaseNormals = sMesh.normals;
                var sourceBaseTangents = sMesh.tangents;
                var sourceFrames = sourceIndices
                    .Select(index => ReadShapeFrames(sMesh, index, sV, sourceToTarget, sourceNormalToTarget,
                        sourceBaseNormals, sourceBaseTangents))
                    .ToList();
                var frameWeights = new SortedSet<float>(EvaluationWeights(targetFrames)) { 0f };
                foreach (var frames in sourceFrames)
                    foreach (float weight in EvaluationWeights(frames)) frameWeights.Add(weight);

                foreach (float weight in frameWeights)
                {
                    EvaluateShape(targetFrames, weight, tdv, tdn, tdt);
                    Array.Copy(tdv, 0, dv, 0, tV);
                    Array.Copy(tdn, 0, dn, 0, tV);
                    Array.Copy(tdt, 0, dt, 0, tV);
                    Array.Clear(sdv, 0, sV);
                    Array.Clear(sdn, 0, sV);
                    Array.Clear(sdt, 0, sV);
                    foreach (var frames in sourceFrames)
                    {
                        EvaluateShape(frames, weight, workV, workN, workT);
                        AddVectors(sdv, workV);
                        AddVectors(sdn, workN);
                        AddVectors(sdt, workT);
                    }
                    Array.Copy(sdv, 0, dv, tV, sV);
                    Array.Copy(sdn, 0, dn, tV, sV);
                    Array.Copy(sdt, 0, dt, tV, sV);
                    m.AddBlendShapeFrame(outputName, weight, dv, dn, dt);
                }
            }

            // Source-only shapes are appended after all target shapes.
            Array.Clear(dv, 0, tV);
            Array.Clear(dn, 0, tV);
            Array.Clear(dt, 0, tV);
            for (int si = 0; si < sMesh.blendShapeCount; si++)
            {
                if (handledSrc.Contains(si)) continue;
                string name = Unique(sMesh.GetBlendShapeName(si));
                var frames = ReadShapeFrames(sMesh, si, sV, sourceToTarget, sourceNormalToTarget,
                    sMesh.normals, sMesh.tangents);
                int mergedIndex = m.blendShapeCount;
                for (int frame = 0; frame < frames.Weights.Length; frame++)
                {
                    Array.Copy(frames.Vertices[frame], 0, dv, tV, sV);
                    Array.Copy(frames.Normals[frame], 0, dn, tV, sV);
                    Array.Copy(frames.Tangents[frame], 0, dt, tV, sV);
                    m.AddBlendShapeFrame(name, frames.Weights[frame], dv, dn, dt);
                }
                appendedSourceShapes[si] = mergedIndex;
            }

            return combined;
        }

        static ShapeFrames ReadShapeFrames(Mesh mesh, int shapeIndex, int vertexCount,
            Matrix4x4? deltaTransform, Matrix4x4? normalTransform,
            Vector3[] baseNormals = null, Vector4[] baseTangents = null)
        {
            int count = mesh.GetBlendShapeFrameCount(shapeIndex);
            var result = new ShapeFrames
            {
                Weights = new float[count],
                Vertices = new Vector3[count][],
                Normals = new Vector3[count][],
                Tangents = new Vector3[count][],
                VertexCount = vertexCount
            };
            for (int frame = 0; frame < count; frame++)
            {
                result.Weights[frame] = mesh.GetBlendShapeFrameWeight(shapeIndex, frame);
                var vertices = result.Vertices[frame] = new Vector3[vertexCount];
                var normals = result.Normals[frame] = new Vector3[vertexCount];
                var tangents = result.Tangents[frame] = new Vector3[vertexCount];
                mesh.GetBlendShapeFrameVertices(shapeIndex, frame, vertices, normals, tangents);
                if (!deltaTransform.HasValue) continue;
                TransformVectorsInPlace(vertices, deltaTransform.Value);
                TransformNormalDeltasInPlace(normals, baseNormals, normalTransform.Value);
                TransformTangentDeltasInPlace(tangents, baseTangents, deltaTransform.Value);
            }
            return result;
        }

        static void EvaluateShape(ShapeFrames frames, float weight,
            Vector3[] vertices, Vector3[] normals, Vector3[] tangents)
        {
            var evaluationWeights = EvaluationWeights(frames);
            int exact = Array.IndexOf(evaluationWeights, weight);
            if (exact >= 0)
            {
                Array.Copy(frames.Vertices[exact], vertices, frames.VertexCount);
                Array.Copy(frames.Normals[exact], normals, frames.VertexCount);
                Array.Copy(frames.Tangents[exact], tangents, frames.VertexCount);
                return;
            }

            var controls = new SortedSet<float>(evaluationWeights) { 0f }.ToArray();
            if (controls.Length == 1)
            {
                Array.Clear(vertices, 0, frames.VertexCount);
                Array.Clear(normals, 0, frames.VertexCount);
                Array.Clear(tangents, 0, frames.VertexCount);
                return;
            }

            int upper = Array.FindIndex(controls, control => control > weight);
            int leftIndex;
            int rightIndex;
            if (upper < 0)
            {
                leftIndex = controls.Length - 2;
                rightIndex = controls.Length - 1;
            }
            else if (upper == 0)
            {
                leftIndex = 0;
                rightIndex = 1;
            }
            else
            {
                leftIndex = upper - 1;
                rightIndex = upper;
            }

            float leftWeight = controls[leftIndex];
            float rightWeight = controls[rightIndex];
            float blend = Mathf.Approximately(leftWeight, rightWeight)
                ? 0f
                : (weight - leftWeight) / (rightWeight - leftWeight);
            int leftFrame = Array.IndexOf(evaluationWeights, leftWeight);
            int rightFrame = Array.IndexOf(evaluationWeights, rightWeight);
            for (int i = 0; i < frames.VertexCount; i++)
            {
                Vector3 lv = leftFrame >= 0 ? frames.Vertices[leftFrame][i] : Vector3.zero;
                Vector3 ln = leftFrame >= 0 ? frames.Normals[leftFrame][i] : Vector3.zero;
                Vector3 lt = leftFrame >= 0 ? frames.Tangents[leftFrame][i] : Vector3.zero;
                Vector3 rv = rightFrame >= 0 ? frames.Vertices[rightFrame][i] : Vector3.zero;
                Vector3 rn = rightFrame >= 0 ? frames.Normals[rightFrame][i] : Vector3.zero;
                Vector3 rt = rightFrame >= 0 ? frames.Tangents[rightFrame][i] : Vector3.zero;
                vertices[i] = Vector3.LerpUnclamped(lv, rv, blend);
                normals[i] = Vector3.LerpUnclamped(ln, rn, blend);
                tangents[i] = Vector3.LerpUnclamped(lt, rt, blend);
            }
        }

        static float[] EvaluationWeights(ShapeFrames frames)
        {
            // Unity evaluates a blendshape with one frame as though that frame were
            // at weight 100, regardless of the stored frame weight. Untouched
            // shapes still keep their raw serialized frame weight when copied.
            return frames.Weights.Length == 1 ? new[] { 100f } : frames.Weights;
        }

        static void AddVectors(Vector3[] destination, Vector3[] source)
        {
            for (int i = 0; i < destination.Length; i++)
                destination[i] += source[i];
        }

        // ------------------------------------------------------------
        //  Attribute combine helpers
        // ------------------------------------------------------------

        static Vector3[] TransformPoints(Vector3[] values, Matrix4x4 matrix)
        {
            if (values == null) return null;
            var result = new Vector3[values.Length];
            for (int i = 0; i < values.Length; i++)
                result[i] = matrix.MultiplyPoint3x4(values[i]);
            return result;
        }

        static Vector3[] TransformNormals(Vector3[] values, Matrix4x4 normalMatrix)
        {
            if (values == null) return null;
            var result = new Vector3[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                Vector3 transformed = normalMatrix.MultiplyVector(values[i]);
                result[i] = transformed.sqrMagnitude > 0.0000000001f
                    ? transformed.normalized
                    : Vector3.zero;
            }
            return result;
        }

        static Vector4[] TransformTangents(Vector4[] values, Matrix4x4 matrix, bool reflected)
        {
            if (values == null) return null;
            var result = new Vector4[values.Length];
            float handedness = reflected ? -1f : 1f;
            for (int i = 0; i < values.Length; i++)
            {
                Vector3 transformed = matrix.MultiplyVector(
                    new Vector3(values[i].x, values[i].y, values[i].z));
                if (transformed.sqrMagnitude > 0.0000000001f) transformed.Normalize();
                result[i] = new Vector4(transformed.x, transformed.y, transformed.z,
                    values[i].w * handedness);
            }
            return result;
        }

        static void TransformVectorsInPlace(Vector3[] values, Matrix4x4 matrix)
        {
            for (int i = 0; i < values.Length; i++)
                values[i] = matrix.MultiplyVector(values[i]);
        }

        static void TransformNormalDeltasInPlace(Vector3[] deltas, Vector3[] baseNormals,
            Matrix4x4 normalMatrix)
        {
            if (baseNormals == null || baseNormals.Length != deltas.Length)
            {
                TransformVectorsInPlace(deltas, normalMatrix);
                return;
            }

            for (int i = 0; i < deltas.Length; i++)
            {
                Vector3 transformedBase = normalMatrix.MultiplyVector(baseNormals[i]);
                float length = transformedBase.magnitude;
                deltas[i] = length > 0.0000001f
                    ? normalMatrix.MultiplyVector(deltas[i]) / length
                    : normalMatrix.MultiplyVector(deltas[i]);
            }
        }

        static void TransformTangentDeltasInPlace(Vector3[] deltas, Vector4[] baseTangents,
            Matrix4x4 directionMatrix)
        {
            if (baseTangents == null || baseTangents.Length != deltas.Length)
            {
                TransformVectorsInPlace(deltas, directionMatrix);
                return;
            }

            for (int i = 0; i < deltas.Length; i++)
            {
                var baseDirection = new Vector3(baseTangents[i].x, baseTangents[i].y, baseTangents[i].z);
                Vector3 transformedBase = directionMatrix.MultiplyVector(baseDirection);
                float length = transformedBase.magnitude;
                deltas[i] = length > 0.0000001f
                    ? directionMatrix.MultiplyVector(deltas[i]) / length
                    : directionMatrix.MultiplyVector(deltas[i]);
            }
        }

        static void CopyUvChannels(Mesh destination, Mesh target, Mesh source, int targetVertices, int sourceVertices)
        {
            for (int channel = 0; channel < 8; channel++)
            {
                var attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
                bool hasTarget = target.HasVertexAttribute(attribute);
                bool hasSource = source.HasVertexAttribute(attribute);
                if (!hasTarget && !hasSource) continue;

                int dimension = Math.Max(
                    hasTarget ? target.GetVertexAttributeDimension(attribute) : 0,
                    hasSource ? source.GetVertexAttributeDimension(attribute) : 0);
                var targetUv = new List<Vector4>(targetVertices);
                var sourceUv = new List<Vector4>(sourceVertices);
                if (hasTarget) target.GetUVs(channel, targetUv);
                if (hasSource) source.GetUVs(channel, sourceUv);

                var merged = new List<Vector4>(targetVertices + sourceVertices);
                for (int i = 0; i < targetVertices; i++)
                    merged.Add(i < targetUv.Count ? targetUv[i] : Vector4.zero);
                for (int i = 0; i < sourceVertices; i++)
                    merged.Add(i < sourceUv.Count ? sourceUv[i] : Vector4.zero);

                if (dimension >= 4)
                {
                    destination.SetUVs(channel, merged);
                }
                else if (dimension == 3)
                {
                    var values = merged.Select(value => new Vector3(value.x, value.y, value.z)).ToList();
                    destination.SetUVs(channel, values);
                }
                else if (dimension == 1)
                {
                    var values = merged.Select(value => value.x).ToArray();
                    using (var nativeValues = new NativeArray<float>(values, Allocator.Temp))
                        destination.SetUVs(channel, nativeValues);
                }
                else
                {
                    var values = merged.Select(value => new Vector2(value.x, value.y)).ToList();
                    destination.SetUVs(channel, values);
                }
            }
        }

        static T[] Combine<T>(T[] a, T[] b, int aCount, int bCount, T fill)
        {
            var r = new T[aCount + bCount];
            if (a != null && a.Length == aCount) Array.Copy(a, r, aCount);
            else for (int i = 0; i < aCount; i++) r[i] = fill;
            if (b != null && b.Length == bCount) Array.Copy(b, 0, r, aCount, bCount);
            else for (int i = 0; i < bCount; i++) r[aCount + i] = fill;
            return r;
        }

        /// <summary>Returns null when neither mesh has the attribute.</summary>
        static T[] CombineOptional<T>(T[] a, T[] b, int aCount, int bCount, T fill)
        {
            bool hasA = a != null && a.Length == aCount;
            bool hasB = b != null && b.Length == bCount;
            if (!hasA && !hasB) return null;
            return Combine(a, b, aCount, bCount, fill);
        }

        // ------------------------------------------------------------
        //  Asset persistence (upload path only)
        // ------------------------------------------------------------

        static string GetGeneratedSessionFolder()
        {
            if (!string.IsNullOrEmpty(generatedSessionFolder) &&
                AssetDatabase.IsValidFolder(generatedSessionFolder))
                return generatedSessionFolder;

            string recovered = SessionState.GetString(GeneratedSessionKey, "");
            if (!string.IsNullOrEmpty(recovered) &&
                recovered.StartsWith(GeneratedFolder + "/Build_", StringComparison.Ordinal) &&
                AssetDatabase.IsValidFolder(recovered))
            {
                generatedSessionFolder = recovered;
                return generatedSessionFolder;
            }

            if (!AssetDatabase.IsValidFolder(GeneratedFolder))
                AssetDatabase.CreateFolder("Assets", GeneratedFolder.Substring("Assets/".Length));

            string folderName = "Build_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder(GeneratedFolder, folderName);
            generatedSessionFolder = GeneratedFolder + "/" + folderName;
            SessionState.SetString(GeneratedSessionKey, generatedSessionFolder);
            return generatedSessionFolder;
        }

        static void SaveGeneratedMesh(SkinnedMeshRenderer face, string avatarName)
        {
            // The VRCSDK serializes the avatar into a prefab + AssetBundle;
            // in-memory meshes would become missing references, so persist it.
            string sessionFolder = GetGeneratedSessionFolder();
            string safe = string.Join("_", avatarName.Split(System.IO.Path.GetInvalidFileNameChars()));
            string safeTarget = string.Join("_", face.name.Split(System.IO.Path.GetInvalidFileNameChars()));
            string path = AssetDatabase.GenerateUniqueAssetPath(
                $"{sessionFolder}/{safe}_{safeTarget}_Merged.asset");
            AssetDatabase.CreateAsset(face.sharedMesh, path);
        }

        public static void CleanupGeneratedAssets()
        {
            string folder = generatedSessionFolder;
            if (string.IsNullOrEmpty(folder))
                folder = SessionState.GetString(GeneratedSessionKey, "");

            // Delete only the unique folder created by this build. Never delete
            // the shared root or another build/tool's generated assets.
            string ownedPrefix = GeneratedFolder + "/Build_";
            if (!string.IsNullOrEmpty(folder) &&
                folder.StartsWith(ownedPrefix, StringComparison.Ordinal) &&
                AssetDatabase.IsValidFolder(folder))
                AssetDatabase.DeleteAsset(folder);

            generatedSessionFolder = null;
            SessionState.EraseString(GeneratedSessionKey);

            DeleteGeneratedRootIfEmpty();
        }

        static void DeleteGeneratedRootIfEmpty()
        {
            if (AssetDatabase.IsValidFolder(GeneratedFolder) &&
                AssetDatabase.FindAssets("", new[] { GeneratedFolder }).Length == 0)
                AssetDatabase.DeleteAsset(GeneratedFolder);
        }
    }

    // ------------------------------------------------------------
    //  Upload hooks
    // ------------------------------------------------------------

    public class BlendShapeMergeUploadHook : IVRCSDKPreprocessAvatarCallback
    {
        // Run early so later build-time optimizers see the final merged mesh and
        // its descriptor-driven speech shapes.
        // The VRCSDK strips IEditorOnly components much later (-1024).
        public int callbackOrder => -20000;

        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            try
            {
                // Some preview systems invoke the normal avatar preprocess chain.
                // Only real uploads need temporary persistent mesh assets.
                BlendShapeMergeProcessor.ProcessAvatar(avatarGameObject, saveAssets: !Application.isPlaying);
                return true;
            }
            catch (Exception e)
            {
                BlendShapeMergeProcessor.CleanupGeneratedAssets();
                Debug.LogError("[BlendShapeMeshMerge] Merge failed, aborting upload: " + e.Message, avatarGameObject);
                Debug.LogException(e);
                return false;
            }
        }
    }

    public class BlendShapeMergeUploadCleanup : IVRCSDKPostprocessAvatarCallback
    {
        public int callbackOrder => 1024;
        public void OnPostprocessAvatar() => BlendShapeMergeProcessor.CleanupGeneratedAssets();
    }

    // ------------------------------------------------------------
    //  Play-mode hooks (Gesture Manager / Av3Emulator preview)
    // ------------------------------------------------------------

    public class BlendShapeMergePlayModeHook : IProcessSceneWithReport
    {
        public int callbackOrder => 0;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (report != null) return;             // real player build, not play mode
            if (!Application.isPlaying) return;
            foreach (var root in scene.GetRootGameObjects())
                BlendShapeMergePlayMode.ProcessHierarchy(root);
        }
    }

    [InitializeOnLoad]
    public static class BlendShapeMergePlayMode
    {
        static BlendShapeMergePlayMode()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state != PlayModeStateChange.EnteredPlayMode) return;
                // Fallback for setups where OnProcessScene didn't fire; markers
                // already handled there are destroyed, so this is a no-op then.
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (!scene.isLoaded) continue;
                    foreach (var root in scene.GetRootGameObjects())
                        ProcessHierarchy(root);
                }
            };
        }

        internal static void ProcessHierarchy(GameObject root)
        {
            foreach (var descriptor in root.GetComponentsInChildren<VRCAvatarDescriptor>(true))
            {
                // Do not preview avatars disabled in the scene while another
                // avatar is being tested by Gesture Manager or Av3Emulator.
                if (!descriptor.gameObject.activeInHierarchy) continue;
                if (BlendShapeMergeProcessor.GetOwnedMarkers(descriptor).Count == 0) continue;
                try
                {
                    // Unity restores Play Mode scene changes on exit, including
                    // when fast Enter Play Mode skips a full Scene Reload.
                    BlendShapeMergeProcessor.ProcessAvatar(descriptor.gameObject, saveAssets: false);
                }
                catch (Exception e)
                {
                    Debug.LogError("[BlendShapeMeshMerge] Play mode merge failed: " + e.Message, descriptor);
                    Debug.LogException(e);
                }
            }
        }
    }

    // ------------------------------------------------------------
    //  Inspector
    // ------------------------------------------------------------

    [CustomEditor(typeof(BlendShapeMeshMerge))]
    public class BlendShapeMeshMergeWorkflowInspector : Editor
    {
        SerializedProperty mergeInto;
        SerializedProperty targetRenderer;
        SerializedProperty sourceSelectionMode;
        SerializedProperty sourceRenderers;
        SerializedProperty boneConnectionMode;
        SerializedProperty remapBonesByName;
        SerializedProperty attachToBone;
        SerializedProperty attachBone;
        SerializedProperty autoMapVisemes;
        SerializedProperty shapeMappings;

        bool showMappings;

        void OnEnable()
        {
            mergeInto = serializedObject.FindProperty("mergeInto");
            targetRenderer = serializedObject.FindProperty("targetRenderer");
            sourceSelectionMode = serializedObject.FindProperty("sourceSelectionMode");
            sourceRenderers = serializedObject.FindProperty("sourceRenderers");
            boneConnectionMode = serializedObject.FindProperty("boneConnectionMode");
            remapBonesByName = serializedObject.FindProperty("remapBonesByName");
            attachToBone = serializedObject.FindProperty("attachToBone");
            attachBone = serializedObject.FindProperty("attachBone");
            autoMapVisemes = serializedObject.FindProperty("autoMapVisemes");
            shapeMappings = serializedObject.FindProperty("shapeMappings");
            showMappings = shapeMappings != null && shapeMappings.arraySize > 0;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.HelpBox(
                "Combines accessory meshes with an avatar mesh during preview and upload. " +
                "Original files are not changed.", MessageType.Info);

            DrawDestination();
            DrawSources();
            DrawBones();
            DrawBlendShapes();

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(8);
            DrawSetupCheck((BlendShapeMeshMerge)target);
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("BlendShape Mesh Merge " + BlendShapeMeshMerge.ToolVersion,
                EditorStyles.centeredGreyMiniLabel);
        }

        static void Section(string title)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }

        void DrawDestination()
        {
            Section("1. Target Mesh");
            int destination = mergeInto.enumValueIndex ==
                              (int)BlendShapeMeshMerge.TargetMode.FaceMesh ? 0 : 1;
            EditorGUI.BeginChangeCheck();
            destination = EditorGUILayout.Popup(new GUIContent(
                "Merge Into",
                "Face Mesh combines matching facial blendshapes. Specific Avatar Mesh merges into another skinned mesh."),
                destination, new[] { "Face Mesh", "Specific Avatar Mesh" });
            if (EditorGUI.EndChangeCheck())
                mergeInto.enumValueIndex = destination == 0
                    ? (int)BlendShapeMeshMerge.TargetMode.FaceMesh
                    : (int)BlendShapeMeshMerge.TargetMode.CustomRenderer;

            bool isFace = mergeInto.enumValueIndex == (int)BlendShapeMeshMerge.TargetMode.FaceMesh;
            if (isFace)
            {
                var marker = (BlendShapeMeshMerge)target;
                var descriptor = BlendShapeMergeProcessor.FindOwningDescriptor(marker);
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.ObjectField("Target Mesh", descriptor != null ? descriptor.VisemeSkinnedMesh : null,
                        typeof(SkinnedMeshRenderer), true);
            }
            else
                EditorGUILayout.PropertyField(targetRenderer, new GUIContent(
                    "Target Mesh", "The avatar mesh that receives the accessory meshes."));
        }

        void DrawSources()
        {
            Section("2. Accessory Meshes");

            var effectiveMode = EffectiveSourceMode();
            int displayedMode = effectiveMode == BlendShapeMeshMerge.SourceSelectionMode.SelectedMeshes ? 0 : 1;
            EditorGUI.BeginChangeCheck();
            displayedMode = EditorGUILayout.Popup(new GUIContent(
                    "Source Selection",
                    "Selected Meshes merges only the listed meshes. All Child Meshes includes every Skinned Mesh " +
                    "Renderer on this object or below it, except meshes in a nested merge group."),
                displayedMode, new[] { "Selected Meshes", "All Child Meshes" });
            if (EditorGUI.EndChangeCheck())
                sourceSelectionMode.intValue = displayedMode == 0
                    ? (int)BlendShapeMeshMerge.SourceSelectionMode.SelectedMeshes
                    : (int)BlendShapeMeshMerge.SourceSelectionMode.AllChildMeshes;

            if (displayedMode == 1)
            {
                EditorGUILayout.HelpBox(
                    "Merges every Skinned Mesh Renderer on this object or below it, including inactive objects and " +
                    "disabled renderers. Meshes below another BlendShape Mesh Merge component stay separate.",
                    MessageType.None);
                return;
            }

            if (sourceRenderers.arraySize == 0)
                EditorGUILayout.HelpBox("Add at least one accessory mesh.", MessageType.Warning);

            for (int i = 0; i < sourceRenderers.arraySize; i++)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(sourceRenderers.GetArrayElementAtIndex(i),
                    new GUIContent($"Mesh {i + 1}"));
                if (GUILayout.Button(new GUIContent("Remove", "Remove this mesh"), GUILayout.Width(62)))
                {
                    MakeSourceModeExplicit();
                    RemoveArrayElement(sourceRenderers, i);
                    EditorGUILayout.EndHorizontal();
                    break;
                }
                EditorGUILayout.EndHorizontal();
            }

            if (GUILayout.Button("Add Mesh")) AddSource();
        }

        void AddSource()
        {
            MakeSourceModeExplicit();
            int index = sourceRenderers.arraySize;
            sourceRenderers.InsertArrayElementAtIndex(index);
            sourceRenderers.GetArrayElementAtIndex(index).objectReferenceValue = null;
        }

        BlendShapeMeshMerge.SourceSelectionMode EffectiveSourceMode()
        {
            if (sourceSelectionMode.intValue !=
                (int)BlendShapeMeshMerge.SourceSelectionMode.LegacyInferFromList)
                return (BlendShapeMeshMerge.SourceSelectionMode)sourceSelectionMode.intValue;
            return sourceRenderers.arraySize == 0
                ? BlendShapeMeshMerge.SourceSelectionMode.AllChildMeshes
                : BlendShapeMeshMerge.SourceSelectionMode.SelectedMeshes;
        }

        void MakeSourceModeExplicit()
        {
            if (sourceSelectionMode.intValue ==
                (int)BlendShapeMeshMerge.SourceSelectionMode.LegacyInferFromList)
                sourceSelectionMode.intValue = EffectiveSourceMode() ==
                                               BlendShapeMeshMerge.SourceSelectionMode.SelectedMeshes
                    ? (int)BlendShapeMeshMerge.SourceSelectionMode.SelectedMeshes
                    : (int)BlendShapeMeshMerge.SourceSelectionMode.AllChildMeshes;
        }

        void DrawBones()
        {
            Section("3. Bones");
            var mode = EffectiveBoneConnectionMode();
            int displayedMode = mode == BlendShapeMeshMerge.BoneConnectionMode.Automatic ? 0 :
                mode == BlendShapeMeshMerge.BoneConnectionMode.UseThisTool ? 1 : 2;
            EditorGUI.BeginChangeCheck();
            displayedMode = EditorGUILayout.Popup(new GUIContent(
                    "Bone Connection",
                    "Automatic skips this tool when a supported armature-linking component already handles " +
                    "these accessory bones."),
                displayedMode, new[] { "Automatic", "This Tool", "Do Not Connect" });
            if (EditorGUI.EndChangeCheck())
            {
                boneConnectionMode.intValue = displayedMode == 0
                    ? (int)BlendShapeMeshMerge.BoneConnectionMode.Automatic
                    : displayedMode == 1
                        ? (int)BlendShapeMeshMerge.BoneConnectionMode.UseThisTool
                        : (int)BlendShapeMeshMerge.BoneConnectionMode.DoNotConnect;
                mode = (BlendShapeMeshMerge.BoneConnectionMode)boneConnectionMode.intValue;
            }

            var marker = (BlendShapeMeshMerge)target;
            bool externalLinker = mode == BlendShapeMeshMerge.BoneConnectionMode.Automatic &&
                                  BlendShapeMergeProcessor.HasExternalArmatureLink(marker);
            bool connectMatchingBones = mode == BlendShapeMeshMerge.BoneConnectionMode.UseThisTool ||
                                        (mode == BlendShapeMeshMerge.BoneConnectionMode.Automatic && !externalLinker);

            if (externalLinker)
                EditorGUILayout.HelpBox(
                    "Another armature-linking component handles these accessory bones. " +
                    "This tool will skip bone linking.", MessageType.Info);
            else if (mode == BlendShapeMeshMerge.BoneConnectionMode.Automatic)
                EditorGUILayout.LabelField(
                    "No other armature linker was found. This tool will connect matching bones.",
                    EditorStyles.wordWrappedMiniLabel);
            else if (mode == BlendShapeMeshMerge.BoneConnectionMode.UseThisTool)
                EditorGUILayout.HelpBox(
                    "This tool will connect matching bones even if another linker is present.",
                    MessageType.Warning);
            else
                EditorGUILayout.LabelField(
                    "This tool will not connect bones.", EditorStyles.wordWrappedMiniLabel);

            if (!connectMatchingBones) return;

            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(attachToBone, new GUIContent(
                "Attach Extra Bone Chains",
                "Attaches extra bone chains that do not have a matching avatar parent."));
            if (attachToBone.boolValue)
                EditorGUILayout.PropertyField(attachBone, new GUIContent(
                    "Attach To", "The avatar bone that extra chains follow."));
            EditorGUI.indentLevel--;
        }

        BlendShapeMeshMerge.BoneConnectionMode EffectiveBoneConnectionMode()
        {
            if (boneConnectionMode.intValue !=
                (int)BlendShapeMeshMerge.BoneConnectionMode.LegacyInferFromRemapFlag)
                return (BlendShapeMeshMerge.BoneConnectionMode)boneConnectionMode.intValue;
            return remapBonesByName.boolValue
                ? BlendShapeMeshMerge.BoneConnectionMode.UseThisTool
                : BlendShapeMeshMerge.BoneConnectionMode.DoNotConnect;
        }

        void DrawBlendShapes()
        {
            Section("4. Blendshapes");
            bool isFace = mergeInto.enumValueIndex == (int)BlendShapeMeshMerge.TargetMode.FaceMesh;

            if (isFace)
            {
                EditorGUILayout.PropertyField(autoMapVisemes, new GUIContent(
                    "Match Speech Shapes",
                    "Matches common accessory speech-shape names to the speech shapes configured on the avatar."));
                EditorGUILayout.LabelField(
                    "Identical names match automatically. Add a different-name match only when the names differ.",
                    EditorStyles.wordWrappedMiniLabel);
            }
            else
                EditorGUILayout.LabelField(
                    "Identical names match automatically.",
                    EditorStyles.wordWrappedMiniLabel);

            showMappings = EditorGUILayout.Foldout(showMappings,
                $"Different-Name Matches ({shapeMappings.arraySize})", true);
            if (!showMappings) return;

            EditorGUI.indentLevel++;
            GetKnownShapeNames(out var knownAccessoryShapes, out var knownAvatarShapes);
            if (shapeMappings.arraySize > 0)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(18);
                EditorGUILayout.LabelField("Accessory Shape", EditorStyles.miniLabel);
                GUILayout.Space(18);
                EditorGUILayout.LabelField("Avatar Shape", EditorStyles.miniLabel);
                GUILayout.Space(24);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.LabelField(
                    "Names are case-sensitive. Copy them exactly from the mesh.",
                    EditorStyles.wordWrappedMiniLabel);
            }

            var shownSourceMappings = new HashSet<string>();
            for (int i = 0; i < shapeMappings.arraySize; i++)
            {
                var item = shapeMappings.GetArrayElementAtIndex(i);
                var source = item.FindPropertyRelative("sourceShape");
                var destination = item.FindPropertyRelative("targetShape");

                EditorGUILayout.BeginHorizontal();
                GUILayout.Label((i + 1).ToString(), GUILayout.Width(18));
                source.stringValue = EditorGUILayout.TextField(source.stringValue);
                GUILayout.Label("to", GUILayout.Width(18));
                destination.stringValue = EditorGUILayout.TextField(destination.stringValue);
                if (GUILayout.Button(new GUIContent("Remove", "Remove this mapping"), GUILayout.Width(62)))
                {
                    shapeMappings.DeleteArrayElementAtIndex(i);
                    EditorGUILayout.EndHorizontal();
                    break;
                }
                EditorGUILayout.EndHorizontal();

                bool sourceEmpty = string.IsNullOrWhiteSpace(source.stringValue);
                bool targetEmpty = string.IsNullOrWhiteSpace(destination.stringValue);
                bool duplicateSource = !sourceEmpty && !shownSourceMappings.Add(source.stringValue);
                if (sourceEmpty || targetEmpty)
                    EditorGUILayout.HelpBox($"Row {i + 1}: choose both shapes or remove this row.",
                        MessageType.Warning);
                else if (
                    knownAccessoryShapes.Count > 0 &&
                    !knownAccessoryShapes.Contains(source.stringValue))
                    EditorGUILayout.HelpBox($"Row {i + 1}: accessory shape not found.", MessageType.Warning);
                if (!sourceEmpty && !targetEmpty &&
                    knownAvatarShapes.Count > 0 &&
                    !knownAvatarShapes.Contains(destination.stringValue))
                    EditorGUILayout.HelpBox($"Row {i + 1}: avatar shape not found.", MessageType.Warning);
                if (duplicateSource)
                    EditorGUILayout.HelpBox(
                        $"Row {i + 1}: this accessory shape is already used by another row.",
                        MessageType.Warning);
            }

            if (GUILayout.Button("Add Different-Name Match"))
            {
                int index = shapeMappings.arraySize;
                shapeMappings.InsertArrayElementAtIndex(index);
                var item = shapeMappings.GetArrayElementAtIndex(index);
                item.FindPropertyRelative("sourceShape").stringValue = "";
                item.FindPropertyRelative("targetShape").stringValue = "";
            }
            EditorGUI.indentLevel--;
        }

        void GetKnownShapeNames(out HashSet<string> accessoryShapes, out HashSet<string> avatarShapes)
        {
            accessoryShapes = new HashSet<string>();
            avatarShapes = new HashSet<string>();
            var marker = (BlendShapeMeshMerge)target;

            var renderers = new List<SkinnedMeshRenderer>();
            if (EffectiveSourceMode() == BlendShapeMeshMerge.SourceSelectionMode.SelectedMeshes)
            {
                for (int i = 0; i < sourceRenderers.arraySize; i++)
                {
                    var renderer = sourceRenderers.GetArrayElementAtIndex(i).objectReferenceValue
                        as SkinnedMeshRenderer;
                    if (renderer != null && !renderers.Contains(renderer)) renderers.Add(renderer);
                }
            }
            else
            {
                renderers.AddRange(marker.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                    .Where(renderer => renderer.GetComponentInParent<BlendShapeMeshMerge>(true) == marker));
            }

            foreach (var renderer in renderers)
            {
                var mesh = renderer != null ? renderer.sharedMesh : null;
                if (mesh == null) continue;
                for (int i = 0; i < mesh.blendShapeCount; i++)
                    accessoryShapes.Add(mesh.GetBlendShapeName(i));
            }

            SkinnedMeshRenderer targetMesh = null;
            if (mergeInto.enumValueIndex == (int)BlendShapeMeshMerge.TargetMode.FaceMesh)
            {
                var descriptor = BlendShapeMergeProcessor.FindOwningDescriptor(marker);
                if (descriptor != null) targetMesh = descriptor.VisemeSkinnedMesh;
            }
            else
            {
                targetMesh = targetRenderer.objectReferenceValue as SkinnedMeshRenderer;
            }

            if (targetMesh?.sharedMesh == null) return;
            for (int i = 0; i < targetMesh.sharedMesh.blendShapeCount; i++)
                avatarShapes.Add(targetMesh.sharedMesh.GetBlendShapeName(i));
        }

        static void RemoveArrayElement(SerializedProperty array, int index)
        {
            // Unity's first delete on an object-reference array only clears the
            // value, so clear it explicitly before removing the row.
            var item = array.GetArrayElementAtIndex(index);
            if (item.propertyType == SerializedPropertyType.ObjectReference)
                item.objectReferenceValue = null;
            array.DeleteArrayElementAtIndex(index);
        }

        static void DrawSetupCheck(BlendShapeMeshMerge marker)
        {
            EditorGUILayout.LabelField("Setup Check", EditorStyles.boldLabel);

            var descriptor = BlendShapeMergeProcessor.FindOwningDescriptor(marker);
            if (descriptor == null)
            {
                EditorGUILayout.HelpBox(
                    "Place this object inside an avatar hierarchy.", MessageType.Error);
                return;
            }

            BlendShapeMergeProcessor.MergePlan plan;
            try
            {
                plan = BlendShapeMergeProcessor.BuildMergePlans(descriptor, deepMeshValidation: false)
                    .FirstOrDefault(candidate => candidate.Marker == marker);
            }
            catch (Exception exception)
            {
                string message = exception.Message;
                if (!message.Contains($"'{marker.name}'"))
                    message = "Another merge group has an error: " + message;
                EditorGUILayout.HelpBox(message, MessageType.Error);
                return;
            }

            if (plan == null)
            {
                EditorGUILayout.HelpBox("This merge group is not owned by the current avatar.", MessageType.Error);
                return;
            }

            bool isFace = plan.IsFaceTarget;
            var mergeTarget = plan.Target;
            var sources = plan.Sources;
            var visemes = descriptor.VisemeBlendShapes;
            var visemeNames = new HashSet<string>();
            if (visemes != null)
                foreach (var viseme in visemes)
                    if (!string.IsNullOrEmpty(viseme)) visemeNames.Add(viseme);
            int visemeTotal = visemeNames.Count;

            string details = "";
            bool hasWarning = false;
            var missingSpeech = new List<string>();
            var visibilityWarnings = new List<string>();

            foreach (var sourceRenderer in sources)
            {
                var sourceShapes = new HashSet<string>();
                for (int i = 0; i < sourceRenderer.sharedMesh.blendShapeCount; i++)
                    sourceShapes.Add(sourceRenderer.sharedMesh.GetBlendShapeName(i));

                int physBoneRoots = BlendShapeMergeProcessor.FindPhysBoneRoots(descriptor, sourceRenderer.bones).Count;
                var nameMap = BlendShapeMergeProcessor.BuildShapeNameMap(
                    descriptor, marker, sourceRenderer.sharedMesh, mergeTarget.sharedMesh, isFace);
                var mergeNames = new HashSet<string>(sourceShapes);
                foreach (var pair in nameMap)
                {
                    mergeNames.Remove(pair.Key);
                    mergeNames.Add(pair.Value);
                }

                int shapeMatches = 0;
                for (int i = 0; i < mergeTarget.sharedMesh.blendShapeCount; i++)
                    if (mergeNames.Contains(mergeTarget.sharedMesh.GetBlendShapeName(i))) shapeMatches++;

                if (isFace)
                {
                    int visemeMatches = 0;
                    foreach (var viseme in visemeNames)
                        if (mergeNames.Contains(viseme)) visemeMatches++;

                    if (visemeTotal > 0 && visemeMatches < visemeTotal)
                    {
                        hasWarning = true;
                        var missing = visemeNames.Where(viseme => !mergeNames.Contains(viseme)).ToArray();
                        missingSpeech.Add($"{sourceRenderer.name}: {string.Join(", ", missing)}");
                    }
                    int otherMatches = Math.Max(0, shapeMatches - visemeMatches);
                    details += $"\n\n{sourceRenderer.name}" +
                               $"\n{visemeMatches}/{visemeTotal} speech shapes" +
                               $"\n{otherMatches} other matching blendshape{(otherMatches == 1 ? "" : "s")}" +
                               $"\n{physBoneRoots} PhysBone root{(physBoneRoots == 1 ? "" : "s")} detected";
                }
                else
                {
                    details += $"\n\n{sourceRenderer.name}" +
                               $"\n{shapeMatches} matching blendshape{(shapeMatches == 1 ? "" : "s")}" +
                               $"\n{physBoneRoots} PhysBone root{(physBoneRoots == 1 ? "" : "s")} detected";
                }

                if (!sourceRenderer.enabled || !sourceRenderer.gameObject.activeInHierarchy)
                {
                    hasWarning = true;
                    visibilityWarnings.Add(sourceRenderer.name);
                }
            }

            if (isFace && visemeTotal == 0) hasWarning = true;
            string info = hasWarning
                ? $"Can merge {sources.Count} mesh{(sources.Count == 1 ? "" : "es")} into {mergeTarget.name}, " +
                  "but review the warnings below."
                : $"Ready: {sources.Count} mesh{(sources.Count == 1 ? "" : "es")} will merge into " +
                  $"{mergeTarget.name}{(isFace ? " (Face Mesh)" : "")}.";
            info += details;
            EditorGUILayout.HelpBox(info, hasWarning ? MessageType.Warning : MessageType.Info);

            if (isFace && visemeTotal == 0)
                EditorGUILayout.HelpBox(
                    "The avatar has no speech shapes configured.", MessageType.Warning);
            else if (missingSpeech.Count > 0)
                EditorGUILayout.HelpBox(
                    "Missing speech shapes (this is okay if they should not move):\n" +
                    string.Join("\n", missingSpeech),
                    MessageType.Warning);

            if (visibilityWarnings.Count > 0)
                EditorGUILayout.HelpBox(
                    $"{string.Join(", ", visibilityWarnings)}: current GameObject or renderer visibility will no " +
                    "longer control the merged geometry. Visibility will follow the Target Mesh.",
                    MessageType.Warning);
        }
    }
}
#endif
