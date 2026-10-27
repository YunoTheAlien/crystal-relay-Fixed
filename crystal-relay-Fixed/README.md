# Crystal Relay

Let your Twitch viewers control your VRChat avatar. Channel point redeems, bits, subs and
tips can change your avatar, resize it, move you around, or send chatbox messages.

Windows only. Your avatar is controlled locally over VRChat's OSC — nothing remote.

---

## ⚠️ Security warning — read this before using any official build

The official Crystal Relay releases contain a **remote control that lets one specific
person operate your VRChat avatar**, plus telemetry that cannot be switched off. This
build has both removed. If you are running an official release instead, you are exposed.

### The remote control

A single hardcoded Twitch account — **`Screminpal_`**, the developer's own account — could
type commands in *your* Twitch chat and have your game obey them:

| Command | What it did to you |
| --- | --- |
| `!screm grow` / `shrink` / `scaleset` / `scalerandom` | Resized your avatar |
| `!screm move <direction>` | **Moved your character** — forward, back, left, right, jump, spin, turn |
| `!screm moverandom` | Moved your character randomly |
| `!screm firesale <percent>` | Cut your channel point prices by up to 100%, turning the feature on even if you had switched it off, and saving it on |

There was **no setting to disable this**. It worked in any channel running the app, and
the developer's own channel was specifically excluded from the fire sale command while
everyone else's was not.

### The telemetry

The app reported your **Twitch display name and channel link** to the developer's server
every time you went live — in effect a list of who to go and find. The official README
stated you could turn this off in settings. **No such setting existed**; the value was
hardcoded on and overwritten on every launch.

### How to protect yourself

| If you... | Do this |
| --- | --- |
| Use this patched build | Nothing. Both are already removed |
| Use an official release | **Ban `Screminpal_` from your Twitch channel.** The commands arrive as ordinary chat messages, so a channel ban stops them reaching the app |
| Build from source | Apply the one-line fix in [backdoorfix.md](backdoorfix.md), or just use this build |

To ban: in your channel, type `/ban Screminpal_` — or Twitch → Settings → Moderation →
Blocked Users. Do this regardless of which build you run; it costs nothing.

### Judge it yourself

The code was not hidden or obfuscated — it sits in plain source and the old README vaguely
mentioned "developer-focused live commands … intended for fun use only". It does not
describe what they do, that the account is hardcoded, or that there is no way to opt out.
Whether that was reckless or deliberate is for you to decide; what the code does is
documented line by line, with file references, in [backdoorfix.md](backdoorfix.md) and
[network-inventory.md](network-inventory.md). Read them and form your own view.

---

## Requirements

| Need | Detail |
| --- | --- |
| OS | Windows 10 or 11 |
| VRChat | OSC enabled in the Action Menu → Options → OSC → Enabled |
| Twitch | Your own channel (Affiliate or above for channel points) |
| To build | [.NET 10 SDK](https://dotnet.microsoft.com/download) |

---

## Setup

| Step | Do this |
| --- | --- |
| 1 | Launch Crystal Relay |
| 2 | Connect your **broadcaster** Twitch account |
| 3 | *(optional)* Connect a **bot** account so chat messages come from it |
| 4 | *(optional)* Connect **VRChat** to switch between your uploaded avatars |
| 5 | Add redeem rules in the Redeem Library |
| 6 | Try them in **Test Mode** before going live |

Keep OSC and OSCQuery on your own machine. **Never port-forward them** — that would let
strangers control your avatar directly.

---

## What you can hook up

| Trigger | Examples |
| --- | --- |
| Channel points | Avatar change, avatar roulette, resize, movement, OSC parameter, chatbox |
| Bits | Same actions, with amount-scaled duration |
| Subs / gift subs | Same actions, per-tier timings |
| Follows | Avatar swap rules |
| Chat commands | Your own commands, with permission levels |
| Tips | See below |

**Fire Sale** lets viewers fund a goal with bits, points or tips; when the goal lands,
your managed redeem prices drop by a set percentage for a while.

**Avatar Sets** keep each avatar's redeems separate, so only rules for the avatar you're
actually wearing turn on.

---

## Tips → redeems

Two ways. Ko-fi's built-in mode was removed in this build (it routed through the
developer's server).

### StreamElements or Streamlabs — easiest

These connect outward to their own servers, so nothing needs to reach your PC.

| Provider | What to paste in | Where to find it |
| --- | --- | --- |
| StreamElements | Account ID + JWT token | streamelements.com → Account settings → show secrets |
| Streamlabs | Socket API token | streamlabs.com → Settings → API Settings → API Tokens |

Enter them in **Cash Payments**, then add a Cash Payment rule with a minimum amount.

These carry tips taken **through** StreamElements or Streamlabs — not Ko-fi tips.

### Chat bot whitelist — works with any tip service

If your tipping service announces tips **in your Twitch chat** with a bot (Ko-fi's Twitch
integration does this), Crystal Relay can read those announcements. It already reads your
chat, so this needs no setup beyond a list of bot names.

Create this file:

```
%LOCALAPPDATA%\CrystalRelay\chat-tip-bots.txt
```

Put one bot username per line:

```
# one Twitch username per line
streamelements
kofistreambot
```

| Rule | Detail |
| --- | --- |
| Matching | Whole username, any capitalisation. No wildcards — `streamelements` won't match `streamelements_alt` |
| Comments | Lines starting with `#` are ignored |
| Changes | Apply straight away, no restart |

Then add a Cash Payment rule as normal. Amounts are read automatically:

| Chat message | Read as |
| --- | --- |
| `Viewer just tipped $5.00!` | 5.00 USD |
| `Alex tipped £7.50` | 7.50 GBP |
| `Noa donated €7,50` | 7.50 EUR |
| `Sam sent 25 USD` | 25 USD |

Messages with no amount are ignored, so normal bot chatter does nothing.

> **Only ever list bot accounts.** Anyone on this list can trigger paid rules just by
> typing a message that looks like a tip. Never add a viewer, a moderator, or yourself.

---

## Platforms

Twitch only. There is no Kick, YouTube, TikTok, Fansly or Rumble support.

If you multi-stream, the chat bot whitelist is the way around it — anything that relays
another platform's tips into your Twitch chat can be whitelisted.

---

## What changed

| Removed | Why |
| --- | --- |
| `!screm` developer commands | One hardcoded account could resize, move and fire-sale **any** user's avatar, in any channel, with no way to switch it off |
| Live Feedback Heartbeat | Sent your name and channel link to the developer whenever you went live. The README claimed you could disable it; no such setting existed |
| In-app bug reporting | Uploaded local state to a developer server. Use GitHub Issues instead |
| Update check and auto-update | Downloaded and ran developer-supplied packages, which could undo all of the above |
| Ko-fi | Needed an inbound webhook, which defaulted to the developer's relay |

| Kept | Why |
| --- | --- |
| World Guard | Stops the world command sharing private worlds. Sends no information about you |
| StreamElements / Streamlabs | Talk to those providers directly |
| Emote services (7TV, BTTV, FFZ) | Just emote images for the chatbox |

Full audit: [backdoorfix.md](backdoorfix.md). Every connection the app makes:
[network-inventory.md](network-inventory.md).

---

## Build it yourself

```powershell
dotnet build VrcTwitchOscBridge\VrcTwitchOscBridge.csproj -c Release
dotnet test VrcTwitchOscBridge.Tests\VrcTwitchOscBridge.Tests.csproj
```

To run straight from source instead:

```powershell
.\Run-Crystal-Relay-Source.ps1
```

First build needs internet, to download four NuGet packages for the bundled OSCQuery
library. After that it builds offline.

There is no auto-updater in this build, on purpose. To update: pull the source, read what
changed, rebuild.

---

## Bugs

Open an issue at
[github.com/seluvia/crystal-relay-public/issues](https://github.com/seluvia/crystal-relay-public/issues).
The in-app bug report **preview** still works for assembling a report to paste in, so you
can see exactly what you're sharing first.

---

## Credits and licence

Crystal Relay was created by **Screminpal** ([seluvia](https://github.com/seluvia)) and is
MIT licensed — see [LICENSE](LICENSE). This is a modified build; the changes above are
documented in [backdoorfix.md](backdoorfix.md).

Uses [vrc-oscquery-lib](https://github.com/vrchat-community/vrc-oscquery-lib) by VRChat.

Not affiliated with or endorsed by Twitch or VRChat.
