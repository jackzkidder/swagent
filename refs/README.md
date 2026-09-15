# SOLIDWORKS interop assemblies

This folder holds the five SOLIDWORKS interop DLLs the build references. They
are **not in this repository**: they are Dassault Systèmes' files, shipped with
SOLIDWORKS, and redistributing them is not ours to do. `.gitignore` excludes
`refs/*.dll` for that reason.

**You do not need any of this to use SwAgent.** Install the MSI from the
Releases page. This folder matters only if you are building from source.

## What to copy

From a machine with SOLIDWORKS installed, copy these five files here:

```
SolidWorks.Interop.sldworks.dll
SolidWorks.Interop.swconst.dll
SolidWorks.Interop.swpublished.dll
SolidWorks.Interop.swcommands.dll
SolidWorks.Interop.swdocumentmgr.dll
```

They normally live in:

```
C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist\
```

If they are not there, search the SOLIDWORKS install folder for
`SolidWorks.Interop.sldworks.dll` - the location has moved between releases,
and some installs keep a `CLR2` subfolder alongside the current one. Take the
set from one place; do not mix versions.

`Directory.Build.props` points the build at this folder through the `RepoRefs`
property, so nothing else needs changing once the files are in place.

## Which version to copy

Compile against the **oldest SOLIDWORKS release you intend to support**, not the
newest. A COM interop built against 2022 loads happily on a 2025 seat, because
the cast to `ISldWorks` resolves through `QueryInterface` on a stable IID rather
than through managed type identity. The reverse does not work.

The interops that were used to build the published release are SOLIDWORKS 2025
(version 33.5), so the supported range today is 2025 only. That is a known
deviation from the intended policy, recorded in the main README.
