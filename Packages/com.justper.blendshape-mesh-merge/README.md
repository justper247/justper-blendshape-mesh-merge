# BlendShape Mesh Merge

Version 1.8.2

Combines accessory meshes with an avatar mesh during Play Mode and VRChat upload. Original prefabs and mesh files are not changed.

## Setup

1. Place the accessory inside the avatar.
2. Select the object with **BlendShape Mesh Merge**.
3. Choose the target mesh and accessory meshes.
4. Leave **Bone Connection** on **Automatic**.
5. Fix any red messages in **Setup Check**.
6. Enter Play Mode to preview, then upload normally.

## Bone Connection

- **Automatic:** Recommended. Uses another armature-linking system when one is already handling the accessory; otherwise, this tool connects the bones.
- **This Tool:** Always lets this tool connect the bones.
- **Do Not Connect:** Leaves bone connection to another system.

Use only one system to connect the accessory bones. Existing PhysBones and their bone chains are preserved.

## Blendshapes

Matching blendshape names connect automatically. Enable **Match Speech Shapes** for facial movement. Use manual mappings only when the names are different.

If something does not work, check **Setup Check** and the Unity Console for details.
