# Changelog

## 1.8.2 - 2026-08-05

- Play Mode preview now ignores disabled avatars elsewhere in the scene.
- Inactive accessories inside the active avatar are still processed normally.

## 1.8.1 - 2026-07-31

- Runs Portable BlendShape Injector, when installed, before merging. Shapes an
  accessory adds to the avatar's own body must exist while the meshes are still
  separate.
- No change when that tool is not installed.

## 1.8.0 - 2026-07-31

- Added Automatic bone connection mode.
- Automatically skips this tool's bone linking when a supported armature-linking component already handles the merged accessory bones.
- Added clear manual overrides for forcing this tool or disabling bone connection.
- Kept older prefab bone settings unchanged until the user or package author selects a new mode.

## 1.7.1 - 2026-07-31

- Removed the unnecessary Scene Reload requirement for Play Mode preview.
- Play Mode preview now works with normal and fast Enter Play Mode settings.

## 1.7.0 - 2026-07-31

- Fixed PhysBone descendants being detached from an intentional duplicate PhysBone root.
- Fixed merged geometry, normals, tangents, bindposes, bounds, and blendshape deltas when source and target renderer transforms differ.
- Fixed multi-frame blendshapes by combining the union of frame weights and interpolating both meshes.
- Combined multiple source shapes that intentionally map to one target shape.
- Preserved UV0-UV7 with their dimensions and preserved variable bone influences.
- Preserved extra multi-pass material slots instead of dropping them.
- Prevented deletion of GameObjects that can still be used as skin or PhysBone transforms.
- Added full preflight validation before any merge is applied.
- Added dependency ordering, cycle detection, duplicate-source detection, and nested merge-group ownership.
- Added clear errors for unreadable meshes, non-triangle topology, invalid skinning, invalid mappings, and invalid fallback bones.
- Added explicit **Selected Meshes** and **All Child Meshes** modes while preserving older prefab behavior.
- Simplified the inspector wording and renamed Ready Check to Setup Check.
- Corrected partial and empty speech-shape reporting.
- Prevented inspector repaint warnings from spamming the Console.
- Disabled unsafe Play Mode preview when Scene Reload is off.
- Improved failed/interrupted-build cleanup, stale-folder cleanup, and SDK-missing behavior.
- Made all user-facing descriptions independent of optional avatar setup systems.

## 1.6.0 - 2026-07-27

- Rewrote inspector explanations in simpler language.
- Added a visible version number and simpler inline explanations.
- Removed the disabled legacy inspector implementation.
- Changed generated mesh storage to a unique folder for each build.
- Cleanup now deletes only the folder created by that build.
- Added installation, setup, PhysBone, distribution, and troubleshooting documentation.
- Added stable Unity metadata to the master distribution folder.

## 1.5.0

- Preserved PhysBone roots and their full custom bone chains.
- Prevented intentional duplicate PhysBone roots from remapping to same-named avatar bones.
- Improved custom bone placement.
- Corrected merged renderer bounds.
- Improved source validation and readiness reporting.
- Preserved zero and negative blendshape frame weights.
- Improved material slot folding and source blendshape weight preservation.
