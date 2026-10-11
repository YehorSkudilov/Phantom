# Phantom brand

![App icon](app-icon.svg) ![Wordmark](wordmark.svg)

## Mark

The **ghost-play** mark is a ghost silhouette with a play triangle cut out of its body: a
"phantom" that streams. It always uses the brand gradient on a dark background.

| File | Use |
| --- | --- |
| `mark.svg` | Standalone mark (splash screen, in-app logo) |
| `wordmark.svg` | Mark + `PHANTOM` lettering for headers and store listings |
| `app-icon.svg` | Rounded preview of the composed launcher icon |

The client's MAUI resources (`src/Phantom.Client/Resources/AppIcon`, `Splash`, `Images`) are
generated from the same SVG paths, so update both together.

## Color

| Token | Hex | Role |
| --- | --- | --- |
| Background | `#0B0B12` | App background, icon background |
| Surface | `#161622` | Cards, tab bar |
| Surface raised | `#20202F` | Secondary buttons, chips |
| Outline | `#2E2E42` | Dividers, slider track |
| Primary | `#8B5CF6` | Primary actions, progress |
| Primary light | `#A78BFA` | Hover, links, selected tab |
| Accent | `#22D3EE` | Highlights ("FEATURED", live rates) |
| Text primary | `#F4F4F8` | Body text |
| Text secondary | `#9A9AB0` | Metadata, captions |
| Success / Warning / Danger | `#34D399` / `#FBBF24` / `#F87171` | Status |

**Brand gradient:** `#A78BFA → #8B5CF6 (55%) → #22D3EE`, top-left to bottom-right.

## Type

- UI: Open Sans (Regular 400, Semibold 600), bundled with the app.
- Wordmark: geometric sans, heavy weight, wide tracking (`letter-spacing: 10`).

## Voice

Short, calm and direct, like a good projectionist: "Your movies, streamed from the swarm."
