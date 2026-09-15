# Sailwind Radio

- This folder owns the Sailwind Radio mod. Follow the Unity and Sailwind parent instructions.
- Product decisions and milestone gates are in `docs/PLAN.md`. The current milestone is A. Later speaker, library and effects requirements are design constraints, not claims of implementation.
- The primary agent owns architecture, documentation and integration. Delegate substantive implementation with bounded paths. Independently review diffs and checks. Use a distinct reviewer for persistence changes.
- Use installed game assemblies as read-only references. Never distribute them or proprietary decompiled source.
- Keep generated references, decompilation and build scratch ignored. Reuse installed tools through explicit paths. Do not modify the original SailingGame project when reusing its Blender or Unity tools.
- Local work does not authorize game deployment, save edits, commits, pushes or publication. User playtesting remains distinct from source and build checks.
- No semicolons in player-facing prose, UI text or config descriptions.
