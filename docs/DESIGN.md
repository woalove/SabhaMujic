# SabhaMujic design guide (v2)

One visual language for the **desktop app** (WPF) and the **iPhone remote** (web). It's based on a reference the user supplied: a dark, flat "crew hub" mobile app. Don't use the standard Windows look, i.e. no default title bar, grey chrome or 3D buttons.

## Principles
- **Dark by default**, with a light theme selectable from a sun/moon toggle in the header. The choice is remembered per device.
- **Flat:** no shadows, gradients or glow. Separate content with spacing and 1 px hairlines.
- **Calm and readable from a distance:** generous spacing, large hit targets (≥ 44 px on the phone, ≥ 36 px on the desktop), one accent colour.
- **Same names and order** for screens on both clients.

## Colour tokens
| Token | Dark | Light | Use |
|---|---|---|---|
| `bg` | `#11151A` | `#F6F7F9` | App background |
| `surface` | `#171C22` | `#FFFFFF` | Cards, bars, sheets |
| `surface-2` | `#1E242C` | `#EEF1F4` | Hover, pressed, inputs |
| `line` | `#2A313A` | `#DCE1E7` | Hairline dividers, outlines, the timeline line |
| `text` | `#F2F4F6` | `#12161B` | Primary text, titles |
| `text-2` | `#9AA4AE` | `#5B6670` | Secondary text: times, locations, counts |
| `text-3` | `#6B7580` | `#8A949E` | Disabled text, placeholders |
| `accent` | `#4FB3A9` (teal) | `#2E8F86` | Active tab underline, selected nav, progress, focus, "live" dot |
| `accent-soft` | `#173430` | `#DDF1EE` | Fill of the accent pill; border = `accent` at ~45 % |
| `blue` | `#4C7BD9` | `#3563C4` | Secondary pill (e.g. a tag), soft fill `#16233A` / `#E1E9F8` |
| `violet` | `#9B7BE0` | `#7556C4` | Tag dot colour |
| `amber` | `#E0A54C` | `#B9802A` | Tag dot colour, warnings |
| `danger` | `#E06A6A` | `#C24848` | Errors, destructive actions |

Tag colours cycle through accent → violet → amber → blue for dots and pills. Contrast is ≥ 4.5:1 for `text`/`text-2` on `bg` and `surface`.

## Type
- **Font:** system UI. On Windows that's "Segoe UI Variable", falling back to "Segoe UI". On iOS it's `-apple-system`/SF.
- **Header title:** 26–28 px, regular weight, **two-tone**: name in `text`, qualifier in `text-2` (e.g. **Sabha**<span>Mujic</span>, or the current category).
- **Header subtitle:** 11–12 px, **uppercase, letter-spacing 0.12em**, `text-2` (e.g. `SABHA MUSIC · PLAYBACK`).
- **Row title:** 17–18 px, semibold, `text`. Meta line: 14–15 px, `text-2`, with a 16 px outline icon.
- **Tabs:** label in `text-2` regular; the number or emphasis in `text` bold.

## Components
- **App header:**
  - Left: a rounded-square (12 px radius) **outline icon button** holding the app mark, then the two-tone title above the uppercase subtitle.
  - Right: the **theme toggle** (outline sun/moon icon) and the **account chip**, a rounded-square avatar with an initial, the name, and a chevron.
  - The chip shows "Admin sign-in" when signed out (desktop), and the admin's name when signed in.
- **Tab row:** horizontally scrollable. Each tab has an optional 8 px coloured dot, a label, and a bold value. The active tab gets a 3 px `accent` underline with rounded ends. A right-aligned status item uses a dot + label (e.g. `● Live` for connection status).
- **List / timeline row:**
  - A left gutter with a 1 px `line` vertical rule and 10 px hollow circle nodes.
  - Content: a muted top line (time, duration or artist), then the bold title, then a meta line with an icon, then the **pills**.
  - Right side: a muted count/duration and a chevron.
  - Rows are separated by space, not borders.
  - The current or playing row gets a filled `accent` node and its title in `accent`.
- **Pill / tag:** 6–8 px radius, 12–14 px text, 1 px border in the tag colour at ~45 %, fill = the soft variant, text in `text`. Use pills for categories/tags (several per track), analysis state and "Smart Loop".
- **Bottom navigation (phone)** and **left nav rail or top tab row (desktop):** 24 px outline icons with labels below. Inactive items are `text-2`; the active one is `accent`.
- **Buttons:**
  - Primary: `accent` fill, `bg`-coloured text, 10 px radius.
  - Secondary: `surface-2` fill with `text`.
  - Ghost/icon: transparent, with a `surface-2` hover.
  - Destructive: `danger` text, filled only on the confirm step.
- **Inputs:** `surface-2` fill, 1 px `line` border, 10 px radius, `accent` focus ring.
- **Now-playing bar:** a `surface` bar with a hairline top border. It shows the title and meta, transport controls (the primary play/pause is a filled `accent` circle), a thin progress track in `line` with an `accent` fill, the time in `text-2`, and volume. A Crossfading/Smart Loop state shows as a pill.
- **Status pill:** "Connected" uses the `accent` dot; "Reconnecting…" uses an `amber` dot with a subtle pulse; "Disconnected" uses `danger`.

## Desktop specifics (WPF)
- **Custom window chrome** (`WindowChrome`): no system caption. The app header *is* the title bar and draggable area. Minimise/maximise/close are flat 40×32 icon buttons; close hovers `danger`. The window stays resizable (min 760×480), with snap support.
- **Layout:** the header across the top; a left **nav rail** (Library, Queue, Playlists, Admin), with Admin visible only when an admin is signed in; the content; an optional right queue panel; and the now-playing bar at the bottom.
- **Category tags:** shown as the tab row at the top of the Library ("All" plus each tag with its coloured dot and track count).
- **Mini-player:** the same tokens, compact, with rounded corners.
- **Theme switching:** swap a `Themes/Dark.xaml` / `Themes/Light.xaml` token dictionary at runtime. Controls use `DynamicResource` brushes only, never hard-coded colours.

## Phone specifics (web remote)
- The header is the same as the desktop's. Under it sits the **tab row of tags** on Library.
- **Bottom nav:** Now Playing, Queue, Library, Playlists, Upload.
- **Sign-in screen:** the app mark, then the two-tone title, then username, password and "Remember this phone", with a primary button.
- **Theme:** follows the header toggle, falling back to `prefers-color-scheme`. Honour safe-area insets.
