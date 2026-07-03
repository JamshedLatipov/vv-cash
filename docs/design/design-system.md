# Proffi POS — Design System

A design system reverse-engineered from **Proffi POS** (`proffi.io`), the
point-of-sale / cash-register application in the repository
[`JamshedLatipov/vv-cash`](https://github.com/JamshedLatipov/vv-cash). Proffi POS
is a cross-platform desktop/tablet register built with **Avalonia (.NET)**, aimed
at retail counters in Central Asia — the app ships fully localized in English,
Russian, Kazakh, Tajik and Uzbek. This system captures its blue-on-slate visual
language, its heavy Plus-Jakarta type, its Material-icon set, and its screen
patterns so new interfaces and mockups can be built on-brand.

> **Source:** <https://github.com/JamshedLatipov/vv-cash> — explore the repo (the
> Avalonia `.axaml` views under `src/VvCash/`, especially `Assets/Styles/Colors.axaml`
> and `Controls.axaml`, and `Views/PosView.axaml`) to build higher-fidelity designs.
> Values here are lifted verbatim from those files.

---

## What Proffi POS is

A touch-first sales register. The signature screen is the **Current Order** view: a
thin category rail, a slide-over product catalog, a cart of line items with big
quantity steppers, and a totals panel anchored by an oversized **Pay** button
showing the live total. Supporting flows: login/shift start, mixed payment (split
tender + numpad), returns, parked ("held") sales, customer registration & search,
and system settings. The interface is dense with information but calm — white
cards on a soft `#f6f8f8` canvas, separated by hairline slate borders rather than
shadows, with a single confident blue for every primary action.

---

## Content fundamentals

- **Voice:** plain, instructional, transactional. Short imperative labels —
  *Clear Cart*, *Confirm Payment*, *Start Shift*, *Hold*, *Register Client*. No
  marketing tone; this is an operator tool.
- **Casing:** section eyebrows and status meta are **UPPERCASE and letter-spaced**
  (*PROMOTIONS & COUPONS*, *QUICK INPUT*, *PAYMENT DISTRIBUTION*,
  *V 2.4.0 • TERMINAL ID: LXP-09921*). Field labels are Title/Sentence case and
  **bold**. Button labels are Title Case.
- **Person:** addresses the operator as **you** on welcoming/empty states
  (*"Welcome back! Please enter your credentials to continue."*,
  *"Please start your shift to begin accepting orders."*). Elsewhere it is neutral
  and object-labelled (*Total Amount*, *Remaining to pay*).
- **Numbers:** money is `0.00`, two decimals, no hard-coded currency symbol
  (the app is multi-currency); discounts read `-16%`; balances group thousands
  (`5,000`).
- **Emoji:** none. Meaning is carried by Material icons + color, never emoji.
- **Localization:** every string is a translation key — English is one of five
  languages. Keep copy short and avoid idiom so it survives translation.

---

## Visual foundations

- **Color:** one brand blue — `--primary #0075e2` (hover `#005fc4`, pressed
  `#0063c7`, tint `#e6f1fc`). Neutrals are the full **Tailwind slate** ramp
  (50–900); text is slate-900 / slate-500 / slate-400. Semantics: success
  `#22c55e`, danger `#ef4444` (text `#dc2626`, fill `#fee2e2`). A set of
  tint+ink accent pairs (blue / emerald / purple / red) colors category & status
  chips. The logo tile uses near-black ink `#0a0d12`.
- **Type:** the app font is a custom cut named **"Proffi Jakarta"**; this system
  substitutes **Plus Jakarta Sans** (see *Font substitution* below). The UI leans
  **heavy** — headings and totals at Black/ExtraBold (800–900) with tight negative
  tracking; labels at Bold (700); body at Medium/SemiBold (500–600). Scale runs
  10px meta → 48px login hero.
- **Spacing:** 4px base; **8 / 12 / 16 / 24** do almost all the work. Panels pad 24,
  cards pad 20.
- **Radius:** consistent and generous — **8** inputs, **10** icon/qty buttons,
  **12** primary buttons / product cards / numpad, **16** totals & category panels,
  **24** modals, pill for counters & chips.
- **Elevation:** the system is **flat**. Surfaces are white cards separated by
  **1px slate borders** (slate-100 hairline, slate-200 divider) — not drop shadows.
  The only real elevation is the modal dialog (`0 20 40 / 25%`) over a slate scrim
  (`rgba(15,23,42,.6)`).
- **Backgrounds:** solid `#f6f8f8` app canvas and white surfaces. **No** gradients,
  photos, textures, or illustration. Product/category imagery sits in slate-50
  wells with a Material placeholder glyph when absent.
- **Hover / press:** buttons darken (primary → `#005fc4`), slate buttons step one
  shade darker; interactive tiles (category, numpad, qty) invert to **blue fill /
  white text** on press. Transitions are short (~120ms) color/background fades —
  no bounce, scale, or motion flourish.
- **Layout:** fixed top nav bar and bottom status bar; content between. Touch
  targets stay large (48 icon/qty, 52 cart-delete, 64 numpad, 96 pay bar).
- **Transparency/blur:** essentially none beyond the modal scrim. No glassmorphism.

---

## Iconography

- **System:** [Material Design Icons](https://pictogrammers.com/library/mdi/)
  (`Material.Icons.Avalonia` in the app). This design system loads
  **`@mdi/font`** from CDN and references glyphs by name
  (`<i class="mdi mdi-magnify">`), e.g. `magnify`, `sync`, `delete`, `tag-outline`,
  `pause-circle-outline`, `cash-multiple`, `printer`, `account-circle`,
  `close-box-outline`, `ticket-confirmation`, `credit-card-outline`.
- Icons are **line/outline** weight at slate-600/700, sized 16–40px to context.
  Active/pressed states flip them to white on blue.
- **No emoji, no bespoke SVG.** Always reach for an MDI glyph. If a needed glyph is
  missing, pick the closest MDI name rather than drawing one.
- **Substitution flagged:** the CDN `@mdi/font` is the public Material Design Icons
  set — the same family the app uses, so this is a faithful match, not an approximation.

---

## Font substitution ⚠️

The product uses a licensed custom cut called **"Proffi Jakarta"** (seen in
`logo.svg` as *Proffi Jakarta XB / B*). We do not have those files, so
`fonts.css` loads **Plus Jakarta Sans** from Google Fonts as the closest public
match. Plus Jakarta Sans tops out at weight **800**, so `--fw-black (900)` falls
back to 800 in rendering. **Please supply the licensed Proffi Jakarta font files**
and we'll swap them into `fonts.css`.

---

## Logo

`assets/logo.png` (+ `assets/logo.svg`) — the `proffi.io` app mark: a near-black
(`#0a0d12`) rounded-square tile with **"proffi"** in white and **".io"** in brand
blue below. Use the tile as-is; pair with the wordmark "Proffi POS" in slate-900
(light backgrounds) or white (dark/blue backgrounds). See the *Brand › Logo* card.

---

## Index / manifest

**Root**
- `styles.css` — global entry point (`@import` manifest only). Consumers link this.
- `fonts.css` — webfont (`@import` of Plus Jakarta Sans).
- `tokens/` — `colors.css`, `typography.css`, `spacing.css`, `radius.css`, `shadows.css`.
- `assets/` — `logo.png`, `logo.svg`.
- `guidelines/` — foundation specimen cards (Colors, Type, Spacing, Brand).
- `SKILL.md` — Agent-Skills wrapper.

**Components** — `window.ProffiPOSDesignSystem_12f41e.*`
- `components/forms/` — **Button**, **IconButton**, **TextField**, **SearchBox**,
  **SegmentedControl**, **Switch**, **Checkbox**.
- `components/catalog/` — **CategoryTile**, **ProductCard**, **QtyStepper**, **Numpad**.
- `components/feedback/` — **Badge**, **CouponChip**, **StatusDot**, **Modal**, **KeyHint**.

**UI kits**
- `ui_kits/proffi-pos/` — interactive register recreation (Login → Register →
  Mixed Payment → Confirmation). Entry: `index.html`.

### Intentional additions
None. The component inventory is drawn directly from the app's
`Assets/Styles/Controls.axaml` button/input/toggle styles and the recurring
card/badge/chip/modal patterns in `PosView.axaml`. `StatusDot`, `CouponChip`,
`Badge` and `Modal` formalize markup that the app inlines rather than exposing as
named controls — no primitive was invented that has no counterpart in the source.

### Not built
The app has no slide-deck template, so no `slides/` were created.
