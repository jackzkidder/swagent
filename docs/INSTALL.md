# Getting started with SwAgent

SwAgent adds a chat panel to SOLIDWORKS. You describe a part in plain English;
it models it, measures it, and can produce the drawing, STEP and PDF as well.

It runs on **your own Anthropic API key**, so you pay Anthropic directly for
what you use. There is no subscription, no SwAgent account and no server.

Start to finish, this takes about ten minutes.

---

## Before you start

| You need | Notes |
|---|---|
| Windows 10 or 11, 64-bit | SwAgent is a desktop add-in |
| SOLIDWORKS 2025 | Older releases are not supported yet - see [Known limits](#known-limits) |
| Microsoft Edge WebView2 | Already on nearly every Windows machine. The installer checks and tells you if it is missing |
| An Anthropic account | Free to create; you add credit to it in step 4 |

---

## 1. Download the installer

Get the latest `SwAgent-<version>-x64.msi` from the
[Releases page](https://github.com/jackzkidder/swagent/releases).

## 2. Install it

**Close SOLIDWORKS first.** It holds the add-in file open while it runs, and
the installer will ask you to close it anyway.

Double-click the `.msi`.

> **Windows will warn you that the publisher is unknown.** SwAgent is not code
> signed yet. Click **More info**, then **Run anyway**. If you would rather not,
> that is a reasonable choice - build from source instead (see the main README).

## 3. Open SOLIDWORKS and find the panel

Start SOLIDWORKS. On the right-hand edge is the Task Pane, the vertical strip of
tabs. Click the **SwAgent** tab (the cube icon).

The first time, it shows the setup screen.

> **No SwAgent tab?** See [If something goes wrong](#if-something-goes-wrong).

## 4. Create an API key and add credit

In the setup screen, click **Open API keys**. That takes you to
`console.anthropic.com`.

1. Sign in, or create an account.
2. Go to **API keys** and click **Create key**. Name it `SwAgent` so you can
   recognise it later.
3. Copy the key. It starts with `sk-ant-`. **You cannot see it again after you
   leave that page**, so paste it into SwAgent now, or somewhere safe.
4. Go to **Billing** and add credit. API usage is prepaid and separate from any
   Claude.ai subscription you may already pay for - a subscription does **not**
   cover API use.

**What it costs.** A simple part is usually $0.10-$0.50. A detailed one with a
drawing and exports is around $1. You can switch to a cheaper model in Settings,
and you can cap how much work one message may do.

## 5. Paste the key

Back in SOLIDWORKS, paste the key into the box and click **Check and save**.

SwAgent checks it with Anthropic immediately, so you find out now if the key is
wrong or the account has no credit - not twenty minutes into your first part.

The key is encrypted with your Windows account (DPAPI) and stored on your
machine. It is sent to `api.anthropic.com` and nowhere else.

## 6. Make your first part

Type something specific, with real dimensions:

```
A 60 x 40 x 10 mm plate with a 10 mm hole in the middle
```

Press **Send**, and watch the steps appear. SwAgent will:

1. Write down what it expects the finished part to measure, **before** building.
2. Sketch, extrude and cut.
3. Measure the result and check it against that prediction.

When it finishes you get the part in your SOLIDWORKS session, a summary, and the
cost of that request.

Good next requests:

- `Set the material to 6061 aluminium and fill in the part number as BRK-1001`
- `Make a drawing and export a STEP file to C:\Jobs\1234`
- `Add a 3 mm fillet to the four vertical edges`

---

## Settings

Open the **⋮** menu and choose **Settings**.

- **Model.** *Opus 5* (default) reasons best about geometry it cannot see.
  *Sonnet 5* costs roughly 2.5x less per token and is fine for simple prismatic
  parts and batch changes. Changing the model starts a new chat.
- **Step limit.** The most tool calls SwAgent will make for a single message,
  40 by default. This is a spend limit: if it misunderstands you, it stops here
  rather than working through your credit.
- **API key.** Replace the stored key at any time.
- **Open log folder.** Everything SwAgent did, with API keys scrubbed out. This
  is the first thing to look at when something misbehaves.

---

## Things worth knowing

**Select first, then ask.** If you click a face or an edge in SOLIDWORKS before
sending your message, SwAgent can use exactly that face - "sketch on this face"
works. Guessing at geometry is where these tools most often go wrong.

**It can undo its own work.** If a request goes badly, ask it to revert. It
deletes what it built for that message and leaves everything you built before it
untouched.

**Changes across many files are always previewed.** Ask for something like
"set Revision to B on every part in C:\Jobs\1234" and you get a list of every
affected file, with its current and new value, and nothing changes until you
press **Apply**. The model cannot apply a batch; only you can.

**What leaves your PC.** Your messages, measurements and viewport images of the
model, plus the file paths of anything you ask it to save or export. All of it
goes to Anthropic and nowhere else. File lists from batch changes stay local -
the model sees row numbers. There is no SwAgent server and no analytics.

---

## If something goes wrong

**No SwAgent tab in the Task Pane.**
Check `Tools > Add-Ins` in SOLIDWORKS and tick **SwAgent** in both columns
(the second column loads it at startup). If it is not listed at all, the
installer did not complete - reinstall with SOLIDWORKS closed.

**The panel is blank or white.**
The WebView2 runtime is missing or blocked. Install
*Microsoft Edge WebView2 Runtime* from Microsoft, then restart SOLIDWORKS.

**"Your Anthropic account is out of credit."**
Add credit at `console.anthropic.com` under Billing, then press **Try again** in
the panel. Nothing is lost.

**"Anthropic rejected your API key."**
The key was deleted or revoked. Create a new one and use **Settings > Change
key**.

**The installer says SOLIDWORKS is not installed, and it is.**
The installer looks for a registered SOLIDWORKS 2025 or newer. If you are on an
older release, SwAgent will not work yet - this is a real limit, not a check to
bypass.

**It built the wrong thing.**
Ask it to revert, then say what was wrong in one specific sentence: which face,
which dimension, which direction. Vague corrections cost as much as the original
request and usually miss again.

---

## Known limits

- **SOLIDWORKS 2025 only.** It is built against the 2025 interop assemblies.
  2022-2024 support needs those releases' files; it is on the roadmap.
- **Prismatic parts.** Plates, brackets, housings, spacers, flanges and simple
  enclosures: sketches, extrudes, cuts, shells, fillets, chamfers, patterns and
  mirrors. No assemblies, sheet metal, surfacing, revolves, sweeps or lofts yet.
  Ask for one of those and it will tell you it cannot, rather than approximating.
- **Drawings need tidying.** Inserted dimensions always land overlapping and
  need a few minutes of human work. SwAgent says so rather than calling the
  drawing finished.
- **Not code signed.** Hence the Windows warning on install.

---

## Uninstalling

**Settings > Apps > SwAgent > Uninstall**, with SOLIDWORKS closed. Everything
the installer wrote is removed, including the COM registration.

Your API key and logs live in `%LOCALAPPDATA%\SwAgent` and are left behind on
purpose; delete that folder if you want them gone.
