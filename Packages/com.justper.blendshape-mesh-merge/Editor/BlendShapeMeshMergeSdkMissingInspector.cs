#if UNITY_EDITOR && !VRC_SDK_VRCSDK3
using UnityEditor;

namespace BlendShapeMerge
{
    [CustomEditor(typeof(BlendShapeMeshMerge))]
    public class BlendShapeMeshMergeSdkMissingInspector : Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox(
                "BlendShape Mesh Merge needs the VRChat SDK - Avatars package. " +
                "Install it, then allow Unity to finish compiling.",
                MessageType.Error);
        }
    }
}
#endif
