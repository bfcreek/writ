---
name: writ-release
description: Run tools/release.ps1 to test, write release notes, commit local changes, bump the patch version, and push the branch and release tag. Use only when the user invokes /writ-release.
disable-model-invocation: true
---

# Writ release

Run the release script. Do not reimplement its version bump or tag push.

From the repository root:

```powershell
powershell -NoProfile -File tools/release.ps1
```

The script always runs tests, writes release notes from commits since the latest tag, commits local changes, and pushes the branch and tag. Do not skip those steps, commit, or push a tag yourself.

When the script finishes, report its result: the tag it pushed, or the reason it stopped.
