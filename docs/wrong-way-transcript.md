# The wrong-way transcript

The definition of done asks for one specific piece of evidence:

> A deliberately wrong feature — extruded the wrong direction — is caught by
> the model from the screenshot, undone, and rebuilt correctly. Keep that
> transcript.

This is that transcript. Reproduce with:

```
SwAgent.Harness.exe --wrongway
```

## Why this planting, specifically

The error is **volume-neutral and bounding-box-neutral**. A 20 × 20 × 15 mm
boss is placed on the wrong end of a symmetric 100 × 60 × 10 mm plate, while
the agent is told it belongs at the other end.

| | Correct part | Planted part |
|---|---|---|
| Volume | 66 cm3 | 66 cm3 |
| Mass | 66 g | 66 g |
| Bounding box | 25 × 100 × 60 mm | 25 × 100 × 60 mm |

Every number the agent routinely sees after a feature is **identical**. If it
catches the error, it cannot have caught it from the numbers.

This design matters because the alternative reading was live. Every bug found
during development had been caught by a measurement and none by a picture,
which looked like an argument for dropping the image and saving the tokens.
That argument was survivorship-biased: it counted only failures that survived
long enough to be debugged, never ones the image quietly prevented. This
experiment was built to settle it with evidence instead of instinct.

## Result: the image earns its place

The decisive line, after it had called both `sw_mass_properties` and
`sw_screenshot`:

> **"Sizes check out; the boss placement does not."**

The numbers passed. It found the error anyway.

## What it did, in order

1. `sw_feature_tree` and `sw_mass_properties` together — inspected before judging.
2. `sw_declare_intent` — committed to the corrected part *before* touching it,
   unprompted. "Numbers first. Let me commit to the corrected part before
   touching anything."
3. `sw_screenshot` — right view.
4. Identified the mismatch: size right, placement wrong.
5. `sw_undo` — removed the boss rather than cutting it away or adding a second
   one. This is the behaviour the system prompt asks for and it followed it.
6. Rebuilt the boss on the correct face via `sw_sketch_open_on_face`.
7. Re-measured, and **caught its own mistake**: "Z is off by 10 — the reported
   sketch origin mapping wasn't what I assumed." Undid again and redid it
   centred.
8. `sw_check_intent` — passed.

Step 7 is worth noting separately. The sketch-origin reporting built into
`sw_sketch_open_on_face` exists precisely so geometry is not placed blind, and
here the agent used it to catch a placement error in its own repair. The
verification loop caught a fault introduced by the verification loop.

Cost: **$0.49**, 18 tool calls across 17 turns.

## Consequences

- **Keep the screenshot after feature-creating operations.** The proposal to
  make it opt-in was wrong, and this is why.
- Reducing the capture to 640px stands - that is a resolution change, not a
  behavioural one, and 640px was plainly enough here.
- The ordering in the system prompt is vindicated as written: numbers are
  ground truth, the image is a sanity check. The sanity check caught something
  ground truth could not see.

## Known wart

`sw_undo` did not remove a stray sketch: *"Undo of 1 step(s) ran, but the
feature count did not change."* `EditUndo2` left `Sketch2` behind. The agent
noticed, said so, and worked around it. Worth fixing, but it reported the
problem rather than hiding it.
