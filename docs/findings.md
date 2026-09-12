# Empirical findings

Things learned by running against a real SOLIDWORKS 2025 SP5 seat (interop
33.5.0.53) that contradict either the documentation, the common guidance, or a
reasonable assumption. Each one cost a debugging cycle; none of them would have
been caught by code that compiles and returns success.

Reproduce any of these with the probe modes in the harness:

```
SwAgent.Harness.exe --probe      # plane orientation and bounding box order
SwAgent.Harness.exe --cutprobe   # cut end conditions and direction
```

---

## 1. `swEndCondThroughAllBoth` is refused by `FeatureCut4`

**Symptom.** `FeatureCut4` returns `null`. No exception, no error code, no
indication of which of its 27 arguments was objectionable. A rebuild afterwards
reports clean, because nothing was created.

**What does not fix it.** Passing `Sd = false` (not single-ended) alongside it,
which is the obvious reading — `ThroughAllBoth` is inherently two-ended, so the
single-ended flag looks like the contradiction. It is not; that combination is
refused too.

**What works.** Express it as a genuinely two-ended feature rather than via the
constant:

| Argument | Value |
|---|---|
| `Sd` | `false` (two-ended) |
| `T1` | `swEndCondThroughAll` |
| `T2` | `swEndCondThroughAll` |

The `swEndCondThroughAllBoth` constant exists in `swEndConditions_e` (value 9)
and is simply not accepted on this path. Our `EndCondition.ThroughAllBoth` maps
to the table above — see `ExtrudeOp.EndConditionDir1` / `EndConditionDir2`.

**Why it matters.** A through hole sketched on the same plane the material was
extruded from cuts in the wrong direction under plain `ThroughAll`, and removes
nothing, so SOLIDWORKS refuses the feature. `ThroughAllBoth` is the
direction-proof formulation and therefore the right default for a through hole.
Without it the agent has to guess `Reverse`, and a guess that builds
successfully in the wrong direction is precisely the failure mode we cannot
detect from mass alone.

---

## 2. Cut direction does not follow boss direction

On this seat, a boss extruded from the Front plane and a cut from that same
plane run in **opposite** default directions. From `--cutprobe`:

| End condition | Reverse | Result |
|---|---|---|
| ThroughAll | false | refused (`null`) |
| ThroughAll | **true** | Cut-Extrude1, 23.2146 cm3 |
| Blind 10mm | false | refused (`null`) |
| Blind 10mm | **true** | Cut-Extrude1, 23.2146 cm3 |
| ThroughAllBoth | n/a | Cut-Extrude1, 23.2146 cm3 |

Do not assume a cut and a boss from the same sketch plane go the same way.
Prefer `ThroughAllBoth` for through features; for blind cuts the direction must
be verified, not assumed.

---

## 3. Sketch plane orientation is template-dependent — do not assume it

The standard claim is that the Front plane is the XY plane with normal +Z. On
this seat's default part template, reading `ISketch.ModelToSketchTransform`
directly gives:

| Plane | sketch X | sketch Y | extrude normal |
|---|---|---|---|
| Front Plane | model +Y | model +Z | model **+X** |
| Top Plane | model +Y | model -X | model +Z |
| Right Plane | model -X | model +Z | model +Y |

`GetPartBox` **is** in ordinary `[xmin, ymin, zmin, xmax, ymax, zmax]` order —
that part of the documentation is accurate, and `GetBodyBox` agrees with it.
The surprise is the plane orientation, not the box.

**Consequence for tests.** Reference parts assert on the bounding box
dimensions **sorted by size**, not on which global axis each landed on. A part
built on a template with rotated reference planes is dimensionally correct and
would fail an `X == 60` assertion. Sorted dimensions still catch the error that
actually matters — wrong size, including the 1000x unit error — while not
failing on a legitimate template difference. See
`PartMeasurements.SortedDimsMm`.

**Open question.** Whether this template is non-standard or whether the
convention is more variable than usually claimed. Either way the code must not
depend on it, so this is not on the critical path. Worth re-checking against a
stock template before making orientation claims in the product.

---

## 4. `IModelDoc2.GetErrors()` / `GetWarnings()` do not exist

Remembered from somewhere; not on the interface. The real API for rebuild state
is on `IModelDocExtension`:

```csharp
int count = doc.Extension.GetWhatsWrongCount();
doc.Extension.GetWhatsWrong(out object features, out object codes, out object warnings);
```

This is better than what was assumed: it names the **specific failing
features**, so the agent can be told *what* failed rather than merely *that*
something did — which is the difference between a targeted fix and a blind
undo.

---

## 5. `ShowFeatureErrorDialog` must be turned off

A rebuild error can raise a modal dialog on the SOLIDWORKS thread. A modal
dialog blocks that thread until a human clicks it, and the agent is not a human:
the loop would hang with no timeout and no error, which the user experiences as
the add-in freezing their CAD session.

`SwSession.Rebuild` sets `doc.ShowFeatureErrorDialog = false` before every
rebuild. Any other code path that can trigger a rebuild must do the same.

---

## 6. `ISketchManager.ActiveSketch` exists — use it

`InsertSketch` toggles, and the usual advice is to track sketch state yourself.
That is necessary but not sufficient: the user can click things in SOLIDWORKS
while the agent works, and private bookkeeping drifts out of step with reality.

`ActiveSketch` returns the open sketch or `null`, so the state can be
**verified** rather than merely tracked. `SketchOps` cross-checks against it
before every toggle and corrects its own bookkeeping from it.

---

## 7. `Marshal.GetActiveObject` does not always see a running SOLIDWORKS

The first harness run failed to attach to an already-running session and started
a second one. Likely a Running Object Table registration or elevation mismatch
(SOLIDWORKS run as administrator, harness not, or vice versa).

Not a problem for the add-in, which is handed its pointer in `ConnectToSW` and
must never `Dispatch` its own. It *is* a problem for the harness, which can
silently start a second CAD session and leave it running. Check for strays:

```powershell
Get-Process SLDWORKS | Select-Object Id, StartTime
```

---

## 8. regasm cannot load an add-in whose interops are not copied local

The near-universal advice for SOLIDWORKS add-ins is Copy Local = False on the
interop references, so the add-in binds to whatever SOLIDWORKS itself loaded.
That advice is right about runtime and silent about registration.

`regasm` has to load the add-in type to find its `[ComRegisterFunction]`, and
loading the type means resolving `ISwAddin` from `swpublished`. regasm is a
separate process whose probing path is the .NET Framework directory, and
**SOLIDWORKS does not install its interops into the GAC** (verified: no entries
under `C:\Windows\Microsoft.NET\assembly\GAC_MSIL`). So:

```
RegAsm : error RA0000 : Could not load file or assembly
'SolidWorks.Interop.swpublished, Version=33.5.0.53, ...'
```

and **nothing is registered at all** - no CLSID key, no Addins key.

Copying them is safe. They are strong-named COM interop wrappers; on a seat
running this exact version the CLR reuses the already-loaded identity, and on a
newer seat our older copy loads alongside theirs while the cast to `ISldWorks`
still succeeds, because RCW casts resolve through COM `QueryInterface` on a
stable IID rather than through managed type identity. This is exactly why
compiling against the oldest supported interop is the standard strategy.

---

## 9. `$(Configuration)` is empty in Directory.Build.props

`Directory.Build.props` is imported **before** the SDK assigns a default
`Configuration`, so this looks reasonable and is a trap:

```xml
<OutputPath>$(MSBuildProjectDirectory)in\$(Configuration)\</OutputPath>
```

It expands with `$(Configuration)` empty, producing `bin
et48\`. Builds then
succeed while writing to a directory nothing reads, and the previous binary sits
in `bin\Debug
et48\` looking current. A test run then exercises stale code
and passes, which is the worst possible outcome.

The symptom to watch for: a build that reports success in about a second while
the output file's timestamp does not move.

Use the SDK's own switch instead, which is what the platform-in-path behaviour
is actually controlled by:

```xml
<AppendPlatformToOutputPath>false</AppendPlatformToOutputPath>
```

---

## 10. SOLIDWORKS locks the add-in DLL while it is loaded

Once the add-in is registered and SOLIDWORKS has loaded it, the DLL and
everything it references are locked:

```
error MSB3021: Unable to copy ... SwAgent.Core.dll ...
The file is locked by: "SolidWorks (3740)"
```

SOLIDWORKS must be closed to rebuild the add-in. The Core library and the
headless harness build fine while it runs, which is another reason the COM layer
lives in a project with no UI dependency: the tight development loop does not
require restarting CAD.

---

## Naming collisions

`SolidWorks.Interop.sldworks` is a large namespace of generically-named types,
and `using` it shadows parts of `System`. Hit so far:

| Ours | Collides with | Resolution |
|---|---|---|
| `Measure` | `SolidWorks.Interop.sldworks.Measure` | renamed to `PartMeasure` |
| `Environment` | `SolidWorks.Interop.sldworks.Environment` | qualify as `System.Environment` |

`View`, `Body`, `Feature`, `Sketch`, `Annotation` and `Attribute` are all taken
too. Check before naming anything generically.

---

## 11. `InsertFeatureShell` returns void, and an impossible shell still "works"

Two problems, and the second is the nasty one.

**It is on `IModelDoc2`, not `IFeatureManager`** - the only feature in this
codebase that is. Searching `IFeatureManager` for "Shell" finds only
`GetPlasticsShellType` and leads you to conclude the API does not exist.

**It returns `void`.** No feature object, no success flag, no error - exactly
like `EditUndo2`. The only way to know whether it did anything is to measure
the volume before and after.

**And a thickness too large for the part does not fail.** On a 100 mm cube:

| Shell thickness | Result | Volume |
|---|---|---|
| 5 mm | correct hollow box | 1000 -> 230.5 cm3 |
| 60 mm | reported success | 1000 -> **992 cm3** |

A 60 mm wall cannot hollow a 100 mm cube - the walls from opposing faces
overlap - so the honest answer is a refusal. SOLIDWORKS instead produces a
nearly-solid body and reports nothing wrong.

The volume check catches a literal no-op but cannot catch this, because
"8 cm3 removed" is only wrong if you know roughly how hollow the part was meant
to be. The tool does not know that; the agent does. So `sw_shell` reports how
much material it removed, and the design-intent contract catches the rest - the
declared volume for a hollow box will not match 992 cm3.

Layered, deliberately: the tool catches what a tool can see, and the contract
catches what only intent can judge.

---

## 12. A shell needs twice its thickness in every direction

Worth stating plainly because the arithmetic is easy to get wrong: hollowing a
body means fitting a wall in from BOTH sides, so a `t` mm wall needs at least
`2t` mm of material across every direction. A 5 mm shell needs 10 mm of
thickness everywhere; a 60 mm shell needs 120 mm, which a 100 mm cube does not
have.
