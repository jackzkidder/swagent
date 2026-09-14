# SwAgent

A SOLIDWORKS add-in that takes a part description in plain English and produces
the whole deliverable — modelled part, dimensioned drawing, STEP, PDF, and
populated custom properties — not just a solid body.

Bring your own Anthropic API key. It is stored encrypted on your machine with
DPAPI and is sent only to `api.anthropic.com`. There is no backend, and no model
geometry, file name or property value leaves the machine.

**Status: works end to end.** Install it, paste an Anthropic key into the task
pane, describe a part, and get the modelled part, a drawing, a STEP and a PDF.
Verified against a real SOLIDWORKS 2025 seat, and packaged as an MSI. See
[Where this actually is](#where-this-actually-is).

---

## Repository layout

```
src/SwAgent.Core/       COM layer and tool implementations. No UI dependency,
                        by design: it must be drivable headlessly or it cannot
                        be tested.
  Infrastructure/       Units, dispatcher, exception boundary, redacting log
  Session/              ISldWorks ownership, selection and rebuild discipline
  Modeling/             Sketches, planes, the single FeatureExtrusion3 wrapper
  Inspection/           Mass, volume, bounding box, feature tree, screenshots
  Batch/                Folder index, dry-run planner, apply runner
  Tools/                Tool registry, typed parameters, the round-trip

src/SwAgent.AddIn/      ISwAddin shell, COM registration, WebView2 task pane,
                        and the real UI-thread dispatcher.

tests/SwAgent.Harness/  Headless test harness. Drives a real SOLIDWORKS session
                        from a plain console process, with no UI present.

refs/                   SOLIDWORKS interop assemblies, checked in so the build
                        does not depend on an install path.
docs/                   API signature reference and empirical findings.
tools/                  Signature dumper, add-in register/unregister.
```

## Building

Requires the .NET SDK (8.0+) and the SOLIDWORKS interops in `refs/`. The
projects target **net48** and build with reference assemblies from NuGet, so no
.NET Framework Developer Pack install is needed.

```bash
dotnet build
```

## Running the tests

The harness drives a real SOLIDWORKS session. It creates scratch documents and
closes them **without saving** — it never writes a file.

```bash
tests/SwAgent.Harness/bin/Debug/net48/SwAgent.Harness.exe

# Diagnostics
SwAgent.Harness.exe --probe        # plane orientation, bounding box order
SwAgent.Harness.exe --cutprobe     # cut end conditions and direction
SwAgent.Harness.exe --versionprobe a.sldprt b.sldprt   # save history, upgrade check
SwAgent.Harness.exe --attach-only  # fail rather than start a second session
```

Current state: **201 assertions passing** against SOLIDWORKS 2025 SP5.

> **SOLIDWORKS locks the add-in DLL while it has it loaded**, so close it before
> rebuilding `SwAgent.AddIn`. Core and the harness build fine while it runs -
> which is the point of keeping the COM layer free of UI dependencies.

> Without `--attach-only`, the harness will start its own SOLIDWORKS if it
> cannot attach to a running one — and `Marshal.GetActiveObject` does not
> reliably see an existing session. Check for strays with
> `Get-Process SLDWORKS`.

## Building the installer

```powershell
powershell -ExecutionPolicy Bypass -File installeruild-installer.ps1
```

Produces `artifacts\SwAgent-<version>-x64.msi`. Needs the WiX .NET tool:

```
dotnet tool install --global wix --version 5.*
dotnet tool run wix extension add --global WixToolset.Util.wixext/5.0.2
```

> **WiX v5 deliberately, not v7.** v6 and later require accepting the Open
> Source Maintenance Fee EULA, which carries a payment obligation for
> commercial use above a revenue threshold. v5 is the last version under the
> plain open-source licence. Revisit this when the product has revenue - it is
> a licensing decision, not a technical one.

The installer authors the COM registration **as MSI registry components**
rather than shelling out to `regasm`. That is what makes uninstall reliable:
Windows Installer owns every key it wrote, so it removes them on uninstall,
repairs them if damaged, and rolls them back if install fails halfway. A
`regasm` custom action is invisible to MSI, and when its uninstall counterpart
does not run you get an add-in entry pointing at a deleted DLL - which throws
an error on every SOLIDWORKS launch, forever, for someone who no longer uses
the product.

It refuses to install if SOLIDWORKS or the WebView2 runtime is missing, and
asks the user to close SOLIDWORKS first, since the host locks the add-in DLL.

**The MSI is not signed.** Unsigned installers hit SmartScreen warnings that
cost trial conversions, and signing reputation accrues over time - get an OV
certificate before the first external tester.

## The rules this code is built around

These are load-bearing. Breaking one does not produce a compile error; it
produces a part that is silently wrong, or a CAD session that dies with a
customer's unsaved work in it.

1. **Millimetres and degrees at the tool boundary; metres and radians in the
   API.** Converted at exactly one chokepoint, `Infrastructure/Units`. If you
   write `/ 1000.0` anywhere else, that is the bug — every call returns success
   for a part built 1000x too large.

2. **Nothing throws into the SOLIDWORKS message loop.** Every tool handler runs
   inside `SwGuard`. An unhandled exception can take down the host and destroy
   hours of the user's unsaved work.

3. **COM on the SOLIDWORKS thread; HTTP off it.** Enforced by `ISwDispatcher`.
   Network calls on the CAD thread freeze the entire UI mid-conversation.

4. **Clear the selection at the start of every operation.** Selection is global
   mutable state; a stale selection is why the fillet landed on the wrong edge.

5. **A returned feature object does not mean the feature is correct.** Force a
   rebuild, read `GetWhatsWrong`, then look at it.

6. **Assert on numbers, not appearance.** Every reference part checks mass and
   bounding box against hand calculation within 1%. "Looks about right" cannot
   tell 24 cm3 from 24,000 cm3.

7. **No code-execution tool. Ever.** Every capability is a named tool with typed,
   range-checked parameters.

8. **The model proposes a batch; only the user applies one.** `sw_batch_preview`
   is a dry run that writes nothing, and no tool applies a plan - the panel's
   Apply button does. A batch is the one place a mistake is multiplied across
   files that have already been saved and closed, so the confirmation is
   enforced by what the registry contains, not requested in the prompt. The
   model sees counts and row numbers, never file names.

Before trusting any API signature, read the real one — `docs/api-signatures.md`
is generated by reflection over the interop, and
[`docs/findings.md`](docs/findings.md) records where reality differed from the
documentation.

## Where this actually is

Built and verified against a real seat:

- [x] Unit conversion chokepoint, with range checks that reject absurd values
- [x] Exception boundary, with COM session-loss classification
- [x] Dispatcher interface + inline implementation for headless testing
- [x] Redacting log (API keys scrubbed on the way out)
- [x] Session ownership, selection discipline, rebuild with named failures
- [x] Language-independent plane selection (falls back to tree position)
- [x] Sketches: rectangle, centred rectangle, circle, line
- [x] `FeatureExtrusion3` / `FeatureCut4`, wrapped exactly once
- [x] Mass, volume, bounding box
- [x] Headless harness + 2 reference parts + malformed-argument boundary tests
- [x] `ISwAddIn` shell, COM registration, WebView2 task pane, `UiThreadDispatcher`
- [x] Tool registry with typed, range-validated parameters and JSON schema
- [x] 29 tools: modelling, shell, fillet, chamfer, patterns, inspection,
      properties, materials, files, drawings, design-intent contract
- [x] Verification round-trip: rebuild state + measurements + tree + screenshot
- [x] DPAPI key storage, Anthropic transport, agent loop, prompt caching
- [x] Chat UI in the task pane, with first-run key setup
- [x] Deliverable half: material, properties, save, drawing, STEP/PDF export
- [x] MSI installer with authored COM registration and clean uninstall
- [x] Design-intent contract: the agent predicts before it builds, and the
      loop refuses a premature "done"
- [x] The wrong-way transcript - see docs/wrong-way-transcript.md
- [x] Batch operations: folder index, dry-run preview, apply from the panel only.
      Set a property, set a material, or export, across a folder. Open,
      read-only and locked files, and files changed since the preview, are
      skipped with a reason; files last saved by an older release are not
      upgraded without explicit consent

Not built yet, in dependency order:

- [ ] More geometry: hole wizard, revolves
- [ ] Code signing (OV certificate)
- [ ] WebView2 bootstrapping - currently detected and refused, not installed
- [ ] Incremental batch previews. A preview runs in one call on the SOLIDWORKS
      thread, which is why property and material previews stop at 50 files;
      apply already dispatches one file at a time

**Known deviation from the spec:** the interops in `refs/` are SOLIDWORKS 2025
(33.5). The stated policy is to compile against the oldest supported version so
that newer seats can load the add-in, which means obtaining 2022 interops before
any external release. Until then the supported range is 2025 only, and the
version gate must say so rather than implying a range we have not tested.
