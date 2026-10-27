# network-inventory.md — every outbound connection Crystal Relay attempts

Derived entirely from source at commit `aff72be` (2026-09-06). The README was not used
as a source; where code and README disagree, that is called out.

Method: extracted every `http`/`https`/`ws`/`wss` literal from all tracked `.cs` files,
then traced each to its call site to separate *automatic connections* from *links the
app only opens if the user clicks them* and from *test fixtures*. One endpoint is
assembled from string fragments at runtime and does not appear in a plain grep; it is
listed as D4.

---

## 1. Verdict on telemetry

**No third-party telemetry or analytics SDK is present.**

`VrcTwitchOscBridge.csproj` and `CrystalRelayUpdater.csproj` contain **zero**
`PackageReference` entries. The app's only dependency is a `ProjectReference` to the
vendored `oscquery-lib` (VRChat's own OSCQuery library). There is no AppCenter, Sentry,
Application Insights, Google Analytics, Segment, Mixpanel, Firebase, or similar.

Crash logs are **local only** — `CrashLogService.cs` contains no `HttpClient`, no
`PostAsync`, and no URL of any kind.

What *does* leave the machine to the developer is first-party and is listed in section 2.
There are two always-on channels, D1 and D2.

---

## 2. Developer-controlled endpoints

All are Cloudflare Workers under `screminpal-animation.workers.dev`.

| # | Endpoint | Trigger | Opt-out? |
| --- | --- | --- | --- |
| D1 | `crystal-relay-live-worker.…/api/ping` | While Twitch channel is live | **None (as shipped)** |
| D2 | `crystal-relay-world-guard.…/api/check` | Each use of the world command | **None, by design** |
| D3 | `crystal-relay-world-guard.…/api/status` | Status/readiness fetch | Same as D2 |
| D4 | `crystal-relay-bug-report.…/report` | User submits a bug report | User-initiated |
| D5 | `crystal-relay-kofi-relay.…` (WebSocket) | Only if Ko-fi is configured | Opt-in |

### D1 — Live Feedback Heartbeat (always on)

Source: `Services/RuntimeConfig.cs:8`, `Services/LiveFeedbackHeartbeatService.cs`.

Sends five fields: `DisplayName` (≤80 chars), `TwitchUrl`, `IsLive`, `RelayVersion`,
`BuildChannel`. Evaluated on a 1-minute timer; pings on live transition and refreshes
roughly hourly.

**Send-only.** `LiveFeedbackHeartbeatService.cs:192-198` checks
`response.IsSuccessStatusCode` and never reads the response body, so it cannot receive
commands.

Could not be disabled as shipped. The README claims otherwise. Full detail in
`backdoorfix.md` Addendum A; summary: `AppSettings.cs:568` had `get => true;` with a
setter that discarded `false`, `SettingsStore.cs:430` forced it true on every load, and
no toggle control exists anywhere in the XAML.

**This is the app's one piece of genuine external telemetry.**

### D2 — World Guard (always on, by explicit design)

Source: `Models/WorldCommandBlacklistSettings.cs`, `Services/WorldCommandBlacklistService.cs`.

The settings class says so in its own doc comment:

```csharp
/// <summary>
/// World Guard is always-on and cannot be disabled by the user.
/// This property always returns true.
/// </summary>
public bool IsEnabled
{
    get => true;
    set { /* Always-on: ignore attempts to disable. */ }
}
```

POSTs `{ "worldId": "...", "authorId": "..." }` — both lowercased — being the VRChat
world the streamer is currently in and that world's author
(`WorldCommandBlacklistService.cs:116-123`, request record at `:315-317`).

Two things distinguish it from D1:

1. It **reads the response and acts on it.** The server returns `blocked: true|false`,
   which gates whether the world is shared (`:131-145`). This is an inbound control
   channel, albeit one that can only *suppress* an action, never trigger one. It
   **fails closed** — any error, non-200, or schema mismatch blocks.
2. It is **not continuous.** The only caller is `BuildWorldCommandMessageAsync`
   (`BridgeCoordinator.cs:4165`), reached when a viewer uses the world command. It does
   not poll, and it does not fire on world changes.

Assessment: the purpose is legitimate — a server-side list preventing the bot from
publicly sharing private or protected worlds, which is why it fails closed. But it does
disclose world IDs to the developer on each use, with no opt-out, and the developer's
server decides the outcome. Worth knowing about; not covert.

### D4 — Bug report endpoint (URL deliberately split)

`Services/BugReportService.cs:26-44`. The URL is not a literal; it is assembled from an
array at runtime:

```csharp
private static readonly string[] BugReportEndpointSegments =
[
    "https://crystal", "-relay", "-bug", "-report",
    ".screminpal", "-animation", ".workers", ".dev", "/report"
];
public static Uri BugReportEndpoint => new(string.Concat(BugReportEndpointSegments));
```

Reconstructed: `https://crystal-relay-bug-report.screminpal-animation.workers.dev/report`

This is the same character-splitting technique `.github/workflows/public-safety.yml`
uses on its own blocklist literals, so the likely intent is keeping the endpoint out of
naive scrapes rather than hiding it from readers. Flagging it because a string-split URL
will not show up in a grep-based audit, and anyone auditing this app should know it is
there. Submission is user-initiated from the bug report window
(`MainWindowViewModel.cs:18441`); logs are opt-in and pass through
`SensitiveTextSanitizer` first.

### D5 — Ko-fi relay

`Models/CashPaymentRule.cs:28`. Current:
`https://crystal-relay-kofi-relay.screminpal-animation.workers.dev`.
Legacy constant at `:29`: `https://relay.crystalrelay.app` — the only
non-`workers.dev` developer domain in the codebase. Outbound WebSocket, connected only
when the user configures Ko-fi payments.

---

## 3. Third-party services (not Twitch, not VRChat)

| # | Host | Purpose | Trigger |
| --- | --- | --- | --- |
| T1 | `api.github.com` | Release list for update checks | Automatic |
| T2 | `github.com` | Update package download | On update |
| T3 | `api.betterttv.net`, `cdn.betterttv.net` | BetterTTV emotes | Chatbox/emotes active |
| T4 | `api.frankerfacez.com` | FrankerFaceZ emotes | Chatbox/emotes active |
| T5 | `7tv.io`, `cdn.7tv.app` | 7TV emotes | Chatbox/emotes active |
| T6 | `astro.streamelements.com` (wss) | StreamElements tips | Opt-in, needs token |
| T7 | `sockets.streamlabs.com` (wss) | Streamlabs tips | Opt-in, needs token |

Notes:

1. T1 is `https://api.github.com/repos/seluvia/crystal-relay-public/releases?per_page=100`
   (`ApplicationUpdateService.cs:41`).
2. T2 is host-validated: `ApplicationSelfUpdateService.cs:411` rejects any download URI
   whose host is not exactly `github.com`. Good practice — the download URL comes from
   API response data, and this prevents redirection to an arbitrary host.
3. T3–T5 send the broadcaster's numeric Twitch user ID to each emote provider in order
   to fetch channel-specific emote sets (`BridgeCoordinator.cs:21577-21695`). Normal for
   chat tooling, but it is three more third parties learning the channel ID.
4. T7 places the Streamlabs socket token in the URL query string
   (`CashPaymentProviderService.cs:182`). That is Streamlabs' own documented socket API
   design, not this app's choice, but query-string tokens are more prone to ending up in
   logs than header credentials.
5. User-Agent sent to third parties is
   `CrystalRelay/2.8.9 (+https://github.com/seluvia/crystal-relay-public)`
   (`BridgeCoordinator.cs:128`, `TwitchChatboxWindow.xaml.cs:1010`), which identifies the
   app and version to every host it contacts.

---

## 4. Twitch and VRChat (excluded from the request, listed for completeness)

`api.twitch.tv`, `id.twitch.tv`, `eventsub.wss.twitch.tv`, `static-cdn.jtvnw.net`,
`*.twitchcdn.net`, `api.vrchat.cloud`, `vrchat.com`.

Profile images are restricted to an allowlist of `.jtvnw.net` and `.twitchcdn.net`
(`MainWindowViewModel.cs:21953-21957`). Avatar thumbnails in `AvatarImageService.cs`
have **no host allowlist** — the service fetches whatever thumbnail URL the VRChat API
returned. Low risk, since the URL originates from VRChat's own authenticated response,
but it is an unvalidated fetch of a server-supplied URL.

---

## 5. Local-only connections (never leave the machine)

1. `ws://localhost:42070/?client=CrystalRelay` — XSOverlay notifications
   (`XSOverlayNotificationService.cs:14`).
2. `127.0.0.1` / `localhost` on dynamic UDP and TCP ports — OSC and OSCQuery traffic to
   VRChat.
3. mDNS / Zeroconf service advertisement on the local network, via `MeaMod.DNS` inside
   the vendored `oscquery-lib`. This is link-local, not internet-bound, but it does
   broadcast an `_oscjson._tcp` service record to the LAN.

---

## 6. Not connections

1. **Browser links.** Opened only on an explicit user click, via
   `Process.Start` with `UseShellExecute = true` (e.g. `MainWindow.xaml.cs:1326`). The
   app never fetches these itself: `ko-fi.com/screminpal`,
   `discord.gg/6DvWJXN6A2`, `foomaring.gumroad.com/l/lmrjbl` (third-party companion
   product), `github.com/seluvia/crystal-relay-public/issues`, `dev.twitch.tv`,
   `twitch.tv/screminpal_`.
2. **Test fixtures.** `example.invalid`, `example.test` — in `VrcTwitchOscBridge.Tests`
   only, never compiled into the shipped app.
3. **Comments in vendored code.** `docs.unity3d.com`, `issuetracker.unity3d.com` — URLs
   in comments inside `oscquery-lib/Examples/` Unity sample projects, which are not part
   of the app build.
4. **README claim with no matching code.** The README's network table mentions a
   Cloudflare Worker for "About-page beta live-status cards". No such endpoint exists in
   source; the only `/api/live` reference is in `LiveFeedbackHeartbeatService.cs:266-268`,
   which *rewrites* a configured `/api/live` path to `/api/ping`. Either that feature was
   removed or it reuses D1. Nothing extra is contacted.

---

## 7. Summary for a user who just wants the count

Excluding Twitch and VRChat, the app contacts **12 distinct external hosts**:

1. **5 developer-controlled** Cloudflare Worker endpoints (D1–D5), of which **2 are
   always-on with no opt-out** (D1 heartbeat, D2 world guard).
2. **7 third-party** (GitHub ×2, BetterTTV, FrankerFaceZ, 7TV, StreamElements,
   Streamlabs), of which 2 are opt-in payment providers and 3 are emote CDNs.

Genuine external telemetry: **one channel**, D1. It is send-only and carries five
non-sensitive fields, but as shipped it could not be switched off and the README
incorrectly stated that it could. D2 is a functional moderation lookup that also
discloses VRChat world IDs with no opt-out.

Nothing in this inventory can execute code on the user's machine. The one inbound
control channel, D2, can only suppress a world-sharing message. The `!screm` remote
control documented in `backdoorfix.md` arrives over the streamer's **own Twitch chat**,
not over any of these endpoints.
