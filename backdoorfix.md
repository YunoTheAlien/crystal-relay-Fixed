# backdoorfix.md — disabling Crystal Relay's developer remote-control path

Applies to: `seluvia/crystal-relay-public` at commit `aff72be` (2026-09-06).
Status in this working tree: **applied** (3 files changed).

---

## 1. What the path actually was

Crystal Relay shipped a chat-command channel that let **one hardcoded Twitch account**
drive the avatar of *anyone* running the app.

| Property | Value |
| --- | --- |
| Command prefix | `!screm` |
| Authorized account | `Screminpal_`, matched case-insensitively against Twitch login **or** display name |
| Transport | The streamer's own Twitch chat, via EventSub (not the telemetry heartbeat) |
| Gate | The hardcoded name only |
| User-facing off switch | None |

Capabilities granted to that account, in any channel running the app:

1. `grow <m> <s> [transition]` / `shrink <m> <s> [transition]` — relative avatar height.
2. `scaleset <m> <transition>` — absolute avatar height.
3. `scalerandom <min-max> <s>` — randomized avatar height.
4. `move <direction> <s>` — player movement: forward, back, left, right, jump, spin left/right, snap turn left/right (plus short aliases such as `tl`, `spr`).
5. `moverandom <s>` — randomized movement sequence.
6. `firesale <percent> <s>` — discounts all managed rewards by 1–100%.

### Honest characterization

This was **not a concealed implant**. It is in plain source, is unit-tested, and the
README acknowledges "a few developer-focused live commands … intended for fun use only
and live troubleshooting." What the README does *not* state is the command prefix, the
capability list, or that the privileged account is a hardcoded string with no way to
opt out.

Two genuine defects regardless of intent:

1. **No off switch.** Nothing in `Models/`, `RuntimeConfig.cs`, or `SettingsStore.cs`
   gates it. It is live whenever OSC is running.
2. **Asymmetric self-protection.** `BridgeCoordinator.cs:78-81` defines
   `ProtectedDevFireSaleBroadcasterIds = { "817261183" }` and `:2682` blocks `firesale`
   against that one broadcaster ID. Avatar scaling and movement are protected for
   nobody, including that ID.

Impersonation by a third party is hard but not the point: Twitch display names are
derived from the login, so an unrelated viewer cannot easily present as `Screminpal_`.
The defect is that the gate is a **name string** rather than an immutable numeric user
ID, and that it cannot be turned off.

---

## 2. The trap — do not "disable" it by blanking the constants

The obvious move is to blank the two constants in
`VrcTwitchOscBridge/Services/DevChatCommandParser.cs:35-36`. Half of that **fails open**.

```csharp
private static bool IsAuthorizedName(string? value)
{
    return string.Equals(value?.Trim(), AuthorizedChatterName, StringComparison.OrdinalIgnoreCase);
}
```

Set `AuthorizedChatterName = ""` and the comparison inverts into a grant:

| Input | `AuthorizedChatterName = "Screminpal_"` | `AuthorizedChatterName = ""` |
| --- | --- | --- |
| `null` | false | false |
| `""` | false | **true — authorized** |
| `"   "` | false | **true — authorized** |
| `"Screminpal_"` | true | false |

Any event arriving with an empty or whitespace-only login or display name would be
authorized. Blanking that constant makes the exposure wider, not narrower.

Blanking `CommandPrefix = ""` does happen to fail closed, because `SplitTokens` uses
`StringSplitOptions.RemoveEmptyEntries`, so no token can ever equal `""`. But that is
incidental behavior of the splitter, not an explicit deny — a future refactor of
`SplitTokens` would silently re-enable the path.

**Conclusion:** deny explicitly at the authorization function and cut the dispatch.
Do not rely on editing string literals.

---

## 3. The fix as applied

Three files changed. Layer 1 alone is sufficient; layer 2 is defense in depth so that a
future upstream merge restoring the dispatch does not silently reopen the path.

### Layer 1 — cut the dispatch

`VrcTwitchOscBridge/Services/BridgeCoordinator.cs`, in `HandleNotificationAsync`
(was lines 4471-4475). The original call is commented out and replaced with a swallow:

```csharp
// if (bridgeEvent is { IsChatCommandTrigger: true }
//     && await TryHandleDevChatCommandAsync(bridgeEvent, cancellationToken))
// {
//     return;
// }
//
// Replacement: swallow dev command messages so they neither execute nor fall
// through to normal chat-command rule matching below.
if (bridgeEvent is { IsChatCommandTrigger: true }
    && DevChatCommandParser.IsDevCommandMessage(bridgeEvent.ChatCommandText))
{
    WriteLog("Dev chat command ignored: developer remote control is disabled in this build.");
    return;
}
```

The replacement guard matters. A bare deletion would let a `!screm …` message fall
through to the world-command handler and the normal chat-command rule matcher below it,
where it could match a user rule whose trigger text happens to begin with `!screm`.
Swallowing it preserves the original "this message is consumed" behavior while
executing nothing.

### Layer 2 — unconditional deny in the parser

`VrcTwitchOscBridge/Services/DevChatCommandParser.cs:45`:

```csharp
public static bool IsAuthorizedUser(string? userLogin, string? userDisplayName)
{
    // BACKDOORFIX: unconditional deny.
    // Original:
    // return IsAuthorizedName(userLogin) || IsAuthorizedName(userDisplayName);
    return false;
}
```

`IsAuthorizedName` and `AuthorizedChatterName` are left in place, unused, as evidence of
what the gate used to be.

### Layer 3 — test updated to assert the deny

`VrcTwitchOscBridge.Tests/DevChatCommandParserTests.cs`. The existing test asserted that
the developer account *was* authorized, so it would fail against layer 2. It now asserts
the deny, plus a fail-open regression guard covering the empty and whitespace cases from
section 2.

---

## 4. Verifying

```powershell
cd <path-to>\crystal-relay-public
dotnet build VrcTwitchOscBridge.slnx
dotnet test VrcTwitchOscBridge.Tests
```

Requires the .NET 10 SDK on Windows, per the project README.

Runtime check: have any account, including `Screminpal_`, type `!screm move forward 5`
in chat. Expected result — the avatar does not move, and the debug log records
`Dev chat command ignored: developer remote control is disabled in this build.`

Confirm nothing else references the path:

```powershell
git -C <path-to>\crystal-relay-public grep -n "DevChatCommandParser"
```

At the audited commit the only non-test call site was the one in `BridgeCoordinator.cs`.
`TryHandleDevChatCommandAsync` and its `ExecuteDev*` helpers are now unreachable. They
are left in the file rather than deleted, to keep the diff against upstream small and
reviewable; delete them if a smaller attack surface is preferred over merge convenience.

---

## 5. Separate from this fix — the other remote endpoints

Disabling `!screm` does not touch these. All are Cloudflare Workers under
`*.screminpal-animation.workers.dev`:

1. **Live feedback heartbeat** — `Services/RuntimeConfig.cs:8`. Opt-in, send-only, never
   reads the response body, so it cannot carry inbound commands. Transmits display name,
   Twitch URL, live boolean, relay version, build channel.
2. **Ko-fi relay** — `Models/CashPaymentRule.cs:28`. Inbound payment events.
3. **World guard** — `Models/WorldCommandBlacklistSettings.cs:8-11`. Remote blacklist
   consulted for world commands; its responses influence local behavior.

Item 3 is the one worth reviewing next if the goal is to remove all remote influence
over app behavior.

## 6. Prompt-injection scan result

Clean at the audited commit. No agent-instruction files (`CLAUDE.md`, `AGENTS.md`,
`copilot-instructions`, `.cursorrules`, `*.mdc`). No instruction-override phrasing in any
of the 745 tracked files. No HTML comments in any markdown. The only hidden character
found was a UTF-8 BOM in VRChat's vendored `oscquery-lib/vrc-oscquery-lib/NuGetReadme.md`.

The repo in fact runs the opposite control: `.github/workflows/public-safety.yml` fails CI
if the public tree contains `AGENTS.md`, `.codex/`, the literals `system prompt`,
`prompt transcript`, `developer message`, `Codex`, `ChatGPT`, `OpenAI`, Windows absolute
paths, or any of a long list of credential patterns. Those literals are assembled
character-by-character via a `join()` helper so the scanner does not match itself.

---

# Addendum A — the Live Feedback Heartbeat cannot be turned off (corrected finding)

This supersedes section 5 item 1, which called the heartbeat "opt-in". It is not.
`LiveFeedbackHeartbeatService` has an internal default of `Enabled = false`, but the
value handed to it is always `true`, by three independent mechanisms.

## A.1 The README promises a switch that does not exist

README line 275:

> You can disable this anytime in Crystal Relay settings by turning off **Live Feedback Heartbeat**.

There is no such control. `MainWindow.xaml:2090-2105` renders a `Border` containing
exactly two `TextBlock` elements — the heading "Live Feedback Heartbeat" and a
descriptive paragraph. No `CheckBox`, no `ToggleButton`, no binding to the setting.
A grep for `Live Feedback` across every `.xaml` in the project returns only those two
text nodes.

## A.2 The property is hardcoded on

`VrcTwitchOscBridge/Models/AppSettings.cs:568`, as shipped:

```csharp
public bool LiveFeedbackHeartbeatEnabled
{
    get => true;
    set
    {
        if (value)
        {
            SetProperty(ref liveFeedbackHeartbeatEnabled, true);
        }
    }
}
```

1. The getter ignores the backing field and returns the literal `true`.
2. The setter silently discards `false`. Only `true` is accepted.
3. The backing field at line 84 defaulted to `= true` anyway.

## A.3 The persisted value is overwritten on load

`VrcTwitchOscBridge/Services/SettingsStore.cs:430`, as shipped:

```csharp
settings.LiveFeedbackHeartbeatEnabled = true;
```

Every adjacent line in that method uses the pattern `profile.X ?? settings.X`. This one
line discards the saved value. The JSON field is declared and deserialized
(`SettingsStore.cs:2915-2916`, `bool? LiveFeedbackHeartbeatEnabled`) and then thrown
away, so hand-editing the settings file has no effect either.

Net effect: the heartbeat is on for every user, on every launch, with no supported way
to stop it. `MainWindowViewModel.cs:18130` passes
`Settings.LiveFeedbackHeartbeatEnabled` — always `true` — into `UpdateState`.

## A.4 What it actually transmits

The payload is modest and matches the README's disclosure. `LiveFeedbackHeartbeatPayload`
(`LiveFeedbackHeartbeatService.cs:309-314`) carries exactly five fields: `DisplayName`
(capped 80 chars), `TwitchUrl`, `IsLive`, `RelayVersion`, `BuildChannel` (both capped 40).

Two mitigating facts verified in source:

1. It is **send-only**. `LiveFeedbackHeartbeatService.cs:192-198` checks
   `response.IsSuccessStatusCode` and never reads the response body. It cannot receive
   commands. This is not the `!screm` transport.
2. It fires only while the broadcaster is live, refreshing roughly hourly.

So this is a presence beacon for the developer's private "who is live" list, not
exfiltration. The defect is the absence of consent, plus a README that documents a
control which was never built.

## A.5 The one-liner

Hard kill, single line — `AppSettings.cs:570`:

```csharp
get => true;     // change to:
get => false;
```

That is enough. `UpdateState(false, …)` sends a final offline ping and then stays quiet.

## A.6 The proper fix, as applied in this tree

Three edits restore a real, persistable, default-off setting:

1. `AppSettings.cs:84` — `private bool liveFeedbackHeartbeatEnabled = true;` → defaults off.
2. `AppSettings.cs:568` — getter returns the backing field; setter accepts `false`.
3. `SettingsStore.cs:430` — honors `profile.LiveFeedbackHeartbeatEnabled ?? settings.…`
   like every neighboring line.

With these applied the heartbeat is off by default and the `liveFeedbackHeartbeatEnabled`
field in the settings JSON works as documented. Adding an actual checkbox to
`MainWindow.xaml` is left undone — it is UI work, not a security fix, and editing the
JSON now functions.

---

# Addendum B — a stray `.lnk` leaks the developer's private paths

`Crystal Relay Live Feedback.lnk`, committed at the repository root, is a Windows
shortcut from the developer's own machine. Parsed without executing it:

| Field | Value |
| --- | --- |
| Target | an absolute path on a lettered drive to `CrystalRelayLiveList.exe`, under a private `tools/crystal-relay-live-list` build output folder |
| Working dir | the same path, `bin\Release\net10.0-windows` |
| Icon | an absolute path to the project's icon asset |
| Description | `Open the private Crystal Relay Live Feedback dev tool` |
| Volume label | *[redacted - a named drive label]* |
| Host name | *[redacted - a machine name]* |

This is an accidental commit, not an attack. It discloses the developer's directory
layout, machine name, drive label, and the existence of an unpublished companion tool
(`CrystalRelayLiveList.exe`) that consumes the heartbeat data from Addendum A.

It is inert on any other machine: the target path will not exist. It is worth deleting
regardless, as it is pure information leakage with no function in a public repo.

## B.1 Why the project's own CI gate missed it

`.github/workflows/public-safety.yml` is specifically designed to catch this. It has a
`"Windows absolute path"` regex, and `blocked_dir_names` includes `tools`. Both failed
to fire:

1. The path check inspects only the *tracked path* of each file. This file sits at the
   repository root, so none of its path segments match `tools` or `private` — the
   offending path is in the file's *contents*.
2. The content scan runs `if b"\0" in content: continue` **before** applying the regexes.
   A `.lnk` is a binary structure full of null bytes, so it is skipped and the
   absolute-path regex never executes against it. `.lnk` is also absent from the
   `binary_extensions` list, so it was never intentionally exempted — it falls through
   the null-byte shortcut instead.

Suggested hardening for that workflow: add `.lnk` (and other binary shortcut/metadata
formats) to an explicit deny list of file types that must not be committed at all,
rather than relying on the text scan which structurally cannot read them.

---

# Addendum C — removals: bug report, update check, self-update

Requested removals, on the reasoning that an auto-update path lets the developer
reintroduce anything that was patched out. All three are cut at a single choke point so
the original implementation stays visible below the cut for review.

## C.1 In-app bug report — removed

`Services/BugReportService.cs`, top of `SubmitAsync`. Returns a failure that points at
`GitHubIssuesUrl` and performs no network call. The endpoint it used was assembled from
string fragments, reconstructing to
`https://crystal-relay-bug-report.screminpal-animation.workers.dev/report`
(see `network-inventory.md` item D4).

Reporting manually through GitHub means you see exactly what you disclose before you
send it. The snapshot builders (`BugReportSnapshotService`, `SensitiveTextSanitizer`,
`BugReportPreviewBuilder`) are untouched, so the preview window still works for
assembling a report by hand.

## C.2 Update check — removed

`Services/ApplicationUpdateService.cs`, top of `CheckForUpdateCoreAsync`. Returns
`ApplicationUpdateCheckStatus.NoUpdate` immediately. `api.github.com` is no longer
contacted. The UI reports "up to date" and never offers a download.

## C.3 Self-update — removed

`Services/ApplicationSelfUpdateService.cs`, top of `PrepareAndLaunchUpdateAsync`. Throws
`InvalidOperationException` before touching the filesystem or network.

This was the highest-risk component in the app. It downloaded a release asset and
launched `CrystalRelayUpdater` to overwrite installed binaries — arbitrary code
execution gated only on the developer's release feed. The original did validate the
download host as exactly `github.com` (`:411`) and check a SHA-256 digest, which is
correct practice, but both checks only prove the package came from that repo's releases.
They prove nothing about its contents.

With C.2 and C.3 applied, updating means pulling source, reading the diff, and
rebuilding.

`CrystalRelayUpdater/` and the static helpers `ApplyUpdateAsync`,
`CleanupCompletedUpdateAsync` and `AcknowledgeUpdateStartupAsync` are deliberately left
intact, so that an update already staged on disk by a previous install can still be
cleaned up on next launch.

---

# Addendum D — scope notes

## D.1 Is `HandleDevFireSaleRequest` dev-only?

Yes. Its only reachable caller chain is the dev chat command:

```
"!screm firesale"
  -> BridgeCoordinator.RequestDevFireSale        (BridgeCoordinator.cs:2689)
  -> event DevFireSaleRequested                  (:2699, declared :384)
  -> MainWindowViewModel.cs:1039 subscription
  -> HandleDevFireSaleRequest                    (MainWindowViewModel.cs:5587)
```

The broadcaster's own Fire Sale runs through entirely separate code: tier progression at
`MainWindowViewModel.cs:5729-5738`, `StopRewardFireSale`, `ExpireRewardFireSaleIfNeeded`
and `ApplyRewardFireSaleDiscount` (`:5881`). Neutralizing the dev path does not affect
the streamer-configured feature.

What made the dev version worse than the normal one, beyond skipping the goal:

1. `fireSale.IsEnabled = true` — force-enables the feature even if the streamer had
   deliberately turned it off.
2. `QueueSave(0)` — persists that `IsEnabled = true` into the user's settings file, so
   the feature stays switched on after the temporary sale expires.
3. Up to 100% off, which floors every managed reward at 1 channel point
   (`ApplyRewardFireSaleDiscount`, min clamp at `:5895`).
4. `QueueManagedRewardSync(..., FireSaleChanged)` pushes the new prices to the real
   Twitch channel via Helix `UpdateCustomRewardAsync`.

## D.2 Should all `Dev`-named members be deleted?

**No — they are entangled with legitimate avatar-change code.**

`RetargetPausedDevAvatarScaleTimerForAvatarChange` (`BridgeCoordinator.cs:2076`) is
called from the normal avatar-change handler at `BridgeCoordinator.cs:19646`, not from a
dev path. A dev scale command pauses the streamer's own active scale timer and stores
`pausedDevAvatarScaleTimerSnapshot` (`:238`), which the ordinary avatar-change and resume
logic then has to retarget and restore. Deleting the `Dev`-named state machine wholesale
would break ordinary avatar scaling.

Recommended approach: keep the two-layer deny from section 3. It makes every one of
these members unreachable while leaving a small, reviewable diff against upstream.

If physical deletion is still wanted, this subset is safe to remove:

1. `Services/DevChatCommandParser.cs` and
   `VrcTwitchOscBridge.Tests/DevChatCommandParserTests.cs`
2. `TryHandleDevChatCommandAsync` and its call site
3. `ExecuteDevRelativeAvatarScaleAsync`, `ExecuteDevRandomAvatarScaleAsync`,
   `ExecuteDevSetAvatarScaleAsync`, `ExecuteDevMovementAsync`,
   `ExecuteDevRandomMovementSequenceAsync`, `RequestDevFireSale`,
   `IsDevFireSaleProtectedBroadcaster`, `ProtectedDevFireSaleBroadcasterIds`
4. `DevFireSaleRequest`, the `DevFireSaleRequested` event, its subscription at
   `MainWindowViewModel.cs:1039`, and `HandleDevFireSaleRequest`

These must **stay** (or be untangled first): `pausedDevAvatarScaleTimerSnapshot`,
`PauseActiveAvatarScaleTimerForDev`, `RetargetPausedDevAvatarScaleTimerForAvatarChange`,
`ResumePausedAvatarScaleTimerAfterDevAsync`, `ResumePausedMovementTimerAfterDevAsync`,
`RestoreDevAvatarScaleHeightAsync`. With the dispatch removed the snapshot is always
null, so they become harmless no-ops.

Note that `DebugLogService`, `DebugLogWindow` and `DesktopInputLockService` are **not**
part of this — despite the names, they are ordinary user-facing features.

## D.3 Does anything report who is live to the developer?

Yes — one thing, and it is the heartbeat in Addendum A.

`LiveFeedbackHeartbeatPayload` sends `DisplayName` and `TwitchUrl`
(`https://www.twitch.tv/<login>`) together with `IsLive`. That is a named, clickable list
of which Crystal Relay users are streaming right now. It is not anonymous and not
aggregated.

The consumer is confirmed by the stray shortcut in Addendum B, whose target is
`CrystalRelayLiveList.exe` and whose description reads "Open the private Crystal Relay
Live Feedback dev tool".

Operationally the two features in this document pair up: the heartbeat answers "who is
running my app and streaming right now", and `!screm` is typed into that channel's
public Twitch chat. Discovery, then control. Both are now disabled in this tree.

One secondary case: if Ko-fi is configured in `HostedRelay` mode, the developer's relay
also learns that a given `KoFiRelayChannelId` is connected
(`CashPaymentProviderService.cs:248-263`). That is opt-in, and
`KoFiConnectionMode.LocalWebhook` is a local-only alternative that listens on
`http://127.0.0.1:<port>/` and never touches the developer's infrastructure.

## D.4 Is World Guard a real guard?

Yes, on the evidence. It is a genuine blocklist lookup, not a disguised channel.

Points in its favor, all verifiable in `Services/WorldCommandBlacklistService.cs`:

1. **It carries no identity.** The POST body is exactly `{worldId, authorId}`
   (`:315-317`). No Twitch login, display name, broadcaster ID, machine ID or auth
   header. The only header set is `User-Agent: CrystalRelay-DesktopApp` (`:86-90`). The
   server cannot tell *who* asked, beyond the client IP any HTTP request exposes.
2. **It rejects unexpected response fields.** `:132-135` blocks if `schemaVersion != 2`,
   if `blocked` is not a bool, **or if `AdditionalProperties` is non-empty**. A covert
   channel needs spare fields to carry a payload; this refuses them outright. That is the
   signature of defensive parsing, not a smuggling path.
3. **Its only power is to suppress.** The response sets one boolean that decides whether
   a world gets shared. It cannot start OSC, movement, scaling or any other action.
4. **It fails closed.** Every error path — bad status, bad JSON, schema mismatch, invalid
   world/author ID format — returns `Block(isFailClosed: true)`.
5. **`authorId` is the tell.** Sending the world's creator ID lets the list block by
   creator, which is what a creator opt-out registry needs. That is a considerate design,
   not a tracking one.
6. **The status endpoint returns only counters.** `ok`, `worldEntryCount`,
   `creatorEntryCount`, `updatedAt`, `schemaVersion`, `revision`, `storageState`,
   `totalEntries` (`:331-339`). List health, no instructions.
7. **It is not continuous.** The sole caller is `BuildWorldCommandMessageAsync`
   (`BridgeCoordinator.cs:4165`), reached only when a viewer uses the world command. It
   does not poll and does not fire on world changes.

Two real drawbacks remain:

1. **No caching.** `InvalidateCurrentSessionList` is a documented no-op — "checked
   server-side for each world command". So every world command is a live request, giving
   the developer's worker a stream of `(client IP, worldId, timestamp)`. That is a
   passive usage side channel even if nobody intended it.
2. **Hard dependency, no opt-out.** `IsEnabled` is hardcoded `get => true` with a setter
   that ignores disable attempts, and the behavior is fail-closed. If that domain lapses,
   is taken over, or simply goes down, the world command stops working for everyone. The
   always-on part deserves criticism; the lookup itself does not.

Verdict: leave it alone unless you want the world command to work offline. It is the
least concerning of the developer endpoints.

---

# Addendum E — heartbeat hard deny, and the Ko-fi hosted relay

## E.1 The Addendum A fix had a gap

Addendum A restored `LiveFeedbackHeartbeatEnabled` as a real, field-backed, default-off
setting that honors the persisted value. On a clean install that is correct. On an
**existing** install it was not sufficient, and this is worth stating plainly because it
would have silently kept the beacon running:

the original build force-saved `liveFeedbackHeartbeatEnabled = true` into the settings
file (`SettingsStore.cs:430`). So a user who has ever run the shipped app already has
`true` on disk. Honoring the stored value then reads that `true` back and switches the
heartbeat straight on again. The "fix" would have changed nothing for exactly the people
who most needed it.

**Resolution:** a hard deny at the service boundary, mirroring the two-layer pattern used
for `!screm`. `LiveFeedbackHeartbeatService.UpdateState` now forces `enabled = false`
before storing state:

```csharp
// BACKDOORFIX: hard deny.
enabled = false;
```

Nothing is sent regardless of the settings file, an imported profile, or a future
upstream change to the settings plumbing. The Addendum A edits stay in place, so if the
heartbeat is ever wanted again the setting is honest and the single line here is the only
thing to remove.

## E.2 Ko-fi: who gets paid

The money goes to the **streamer**. Ko-fi pays the owner of the Ko-fi page. The streamer
configures their own Ko-fi webhook with their own verification token. The relay carries
notifications only; no funds pass through it, and nothing in this app can redirect a
payment.

No evidence of financial tapping. Checked and found absent across all shipped source:
any payee, commission, split, merchant, PayPal or Stripe logic. The only developer Ko-fi
reference is `KoFiSupportUri = "https://ko-fi.com/screminpal"`
(`MainWindowViewModel.cs:83`), opened by `OpenUri` at `:18355` — an ordinary
"support the developer" button the user clicks.

## E.3 What hosted relay mode does disclose

Two things, neither of which is money:

1. **The streamer's Ko-fi verification token.** Sent to the relay operator on connect
   (`CashPaymentProviderService.cs:259-266`). This is the shared secret Ko-fi puts in each
   webhook body so a receiver can prove the webhook is genuine — validated locally at
   `:514-528`. It is **not** an API key: it cannot withdraw funds, read donation history,
   change payout details, or sign in. Its misuse value is forging fake tip events to that
   streamer's app, which would trigger redeems and advance Fire Sale progress.
2. **The full raw Ko-fi webhook.** In hosted mode Ko-fi posts to the relay, not to the
   app. The relay receives everything Ko-fi sends — which for Ko-fi webhooks can include
   the donor's email address — and forwards a trimmed six-field subset to the app:
   `eventId`, `amount`, `currencyCode`, `userDisplayName`, `message`, `receivedAt`
   (`:570-618`).

The trimming happens **server-side**. The worker's source is not in this repository, so
the README's claim that the relay stores no payloads or emails **cannot be verified
here**. The client is auditable; the server is not. That asymmetry, not any theft of
funds, is the actual issue with hosted mode.

## E.4 What was changed

The developer's hosted relay is now refused, while a self-hosted one still works.

`CashPaymentProviderService.cs`, in the provider startup gate: hosted relay mode now
checks `IsDeveloperHostedKoFiRelay(connections.KoFiRelayBaseUrl)` and, if it matches,
logs a refusal instead of connecting. The new helper blocks any host ending in
`.screminpal-animation.workers.dev`, the legacy host `relay.crystalrelay.app`, and a
blank or unparseable URL — blank counts as developer-hosted because the URL resolution
path falls back to the default.

This was chosen over disabling Ko-fi outright because `KoFiRelayBaseUrl` is a real,
persisted, user-settable setting (`CashPaymentRule.cs:148`, `SettingsStore.cs:3063`) and
`NormalizeRelayBaseUrl` accepts **any** absolute http/https URL with no host allowlist.
Self-hosting is therefore fully supported upstream, and credit is due for leaving it
configurable. Pointing `KoFiRelayBaseUrl` at your own worker keeps tip integration
working with the developer out of the path entirely.

Unaffected by this change: StreamElements and Streamlabs, which connect directly to those
providers' own sockets with no developer involvement, and Ko-fi local webhook mode
(`KoFiConnectionMode.LocalWebhook`), which listens on `http://127.0.0.1:<port>/` and validates
the token locally so it never leaves the machine. The one caveat on local mode is that
Ko-fi must be able to reach that port from the internet, which needs a tunnel or port
forward — presumably why the hosted relay exists in the first place.

## E.5 Current status of every developer-controlled endpoint

| Endpoint | Status after patching |
| --- | --- |
| `crystal-relay-live-worker.…/api/ping` | **Disabled** — hard deny in the service, plus default-off setting |
| `crystal-relay-bug-report.…/report` | **Disabled** — no POST; points at GitHub Issues |
| `crystal-relay-kofi-relay.…` | **Refused** for the developer's host; self-hosted allowed |
| `relay.crystalrelay.app` (legacy) | **Refused** by the same check |
| `crystal-relay-world-guard.…/api/check` | **Still active** — deliberate, see Addendum D.4 |
| `crystal-relay-world-guard.…/api/status` | **Still active** — same |
| `api.github.com` release feed | **Disabled** — update check returns NoUpdate |
| `github.com` package download | **Disabled** — self-update throws before any I/O |

World Guard is the only developer endpoint still contacted. That is a deliberate
recommendation, not an oversight: it sends no identity, rejects unexpected response
fields, can only suppress a world-share, fails closed, and fires only when a viewer uses
the world command. Disabling it would break that command, since it is fail-closed by
design. To cut it anyway, make `WorldCommandBlacklistService.EvaluateAsync` return
`WorldCommandBlacklistDecision.Block(isFailClosed: true)` before its HTTP call — the
world command then stops working, which is the intended fail-closed behavior, not a bug.

Still contacted and **not** developer-controlled: `api.betterttv.net`,
`cdn.betterttv.net`, `api.frankerfacez.com`, `7tv.io`, `cdn.7tv.app` (emote catalogs,
each receiving the broadcaster's numeric Twitch ID), plus `astro.streamelements.com` and
`sockets.streamlabs.com` if those opt-in providers are configured.

---

# Addendum F — running Ko-fi locally, with the developer out of the path

## F.1 Local mode is untouched by the patch

The refusal in Addendum E only modified the `HostedRelay` branch. Local webhook mode sits
in the `else` branch of the same gate and was not changed:

```
if (KoFiEnabled && verification token present)
    if (mode == HostedRelay && channelId and clientSecret present)
        -> refused when the relay host is the developer's   <-- E.4 patch
    else
        -> RunKoFiWebhookAsync: local 127.0.0.1 listener    <-- untouched
```

**The refusal does not fall back to local mode.** It logs and connects nothing, on purpose
— silently opening a local listener would be a surprising thing for a patch to do. Local
mode has to be selected explicitly.

## F.2 Correction: the enum value is `LocalWebhook`

Earlier notes in this document said `KoFiConnectionMode.Webhook`. The actual value is
`KoFiConnectionMode.LocalWebhook` (`CashPaymentRule.cs:20-24`). The default is
`HostedRelay` (`:37`), so this is a change the user must make.

The `else` branch is also reached if the mode is still `HostedRelay` but
`KoFiRelayChannelId` or `KoFiRelayClientSecret` is blank. Setting the mode explicitly is
cleaner than relying on that.

## F.3 What local mode actually does

`RunKoFiWebhookAsync` (`CashPaymentProviderService.cs:306-316`):

1. Binds **one** prefix: `http://127.0.0.1:{KoFiLocalPort}/`. Default port **47891**
   (`CashPaymentRule.cs:41`). There is no `0.0.0.0` prefix, no `+` wildcard, and no LAN
   binding anywhere in the file.
2. Accepts only `POST` to the configured path. Default path **`/kofi`**
   (`CashPaymentRule.cs:42`). Anything else gets a 404 (`:354-360`).
3. Validates the Ko-fi `verification_token` from the request body against the locally
   stored token (`:366`, implemented at `:514-528`).

The token never leaves the machine in this mode. The developer has no involvement at all:
no relay, no worker, no connection to any `workers.dev` host.

## F.4 The catch: Ko-fi cannot reach 127.0.0.1 by itself

Ko-fi delivers webhooks from its own servers on the internet. A loopback-bound listener is
not reachable from the internet, so something has to bridge the two.

**Router port-forwarding will not work.** This is the non-obvious part: the listener is
bound specifically to `127.0.0.1`, not to the machine's LAN address. A forwarded packet
arrives on the LAN interface and is refused, because nothing is listening there.
Forwarding a port is both ineffective here and bad practice.

**A reverse tunnel does work**, because the tunnel client runs on the same machine and
connects outward, then relays inbound requests to `127.0.0.1` locally. Cloudflare Tunnel
(`cloudflared`), ngrok, and Tailscale Funnel all do this. The public HTTPS URL the tunnel
gives you, with the webhook path appended, is what goes into Ko-fi's webhook setting — for
example `https://<your-tunnel-host>/kofi`.

Honest caveat worth passing on: a hosted tunnel terminates TLS at the provider, so the
tunnel operator can see the webhook body in the same way the developer's relay could. It
substitutes one third party for another. The difference is that it is a third party of the
user's choosing, with no interest in their Twitch channel, and the Ko-fi verification
token is never deliberately handed over as a credential.

The only arrangement with no third party in the middle is running the relay yourself on
your own host and domain, which is what `KoFiRelayBaseUrl` already supports (Addendum E.4).

## F.5 Summary of the three working options

| Option | Developer involved? | Needs extra setup |
| --- | --- | --- |
| Self-hosted relay via `KoFiRelayBaseUrl` | No | Your own worker/VPS and domain |
| `LocalWebhook` + reverse tunnel | No | A tunnel client; tunnel provider sees payloads |
| StreamElements or Streamlabs instead | No | Those providers' own tokens |

All three keep tip-triggered redeems working. The developer's hosted relay is the only
path now refused.
