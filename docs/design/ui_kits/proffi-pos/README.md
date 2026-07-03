# Proffi POS — UI kit

Interactive recreation of the Proffi POS register (the Avalonia desktop/tablet
point-of-sale app in `JamshedLatipov/vv-cash`). Cosmetic reconstruction built on
the design-system component primitives — not production logic.

## Flow
`index.html` runs the demo end to end:
1. **Login** — split brand panel (with feature highlights) + sign-in form; press
   **Enter** to submit (`LoginScreen.jsx`).
2. **Register** — a modern three-pane register (`PosScreen.jsx`): category rail ·
   always-visible product catalog · always-visible order panel. A wide search/scan
   bar filters products live; tapping a product adds it; the order panel shows the
   cart, coupons, live totals and a big Pay button — so a sale is **two taps** and
   nothing is hidden behind a drawer. Hotkeys: **F2** focus search, **F4** pay,
   **Esc** clear search.
3. **Mixed payment** — big TOTAL / REMAINING hero, split tender across cash / card /
   gift, **quick-tender chips** (Exact + round-ups), on-screen numpad, and change-due
   readout; **Enter** confirms once the balance is cleared (`PaymentScreen.jsx`).
4. **Confirmation** — success state → start a new order.

## Files
- `index.html` — orchestrator (screen state machine) + script includes.
- `data.js` — sample **apparel** catalog (`window.PROFFI_DATA`): each product carries
  `size`, `color {name,hex}`, `season` and an MDI `icon` so line items can show the
  richer detail clothing retail needs.
- `LoginScreen.jsx`, `PosScreen.jsx`, `PaymentScreen.jsx` — screens, each exported to `window`.

The order panel (right column, ~468px) shows **rich but compact line items**: a
product thumbnail with a color-swatch dot, the name, a one-line color / size /
season / SKU meta, an inline quantity stepper and the line total.

**Kiosk-friendly (low-scroll):** touchscreen kiosks handle finger-drag scrolling
poorly, so the order list minimizes it — compact rows fit far more without
scrolling, large blue **▲ / ▼ pager buttons** appear only when the list overflows
(tap to page instead of dragging), and adding an item **auto-scrolls to the newest
line** so the cashier never hunts for it. Coupons live in a modal (the footer
**Coupon** button) rather than inside the scroll area, and totals + Pay stay pinned.

## Notes
- Components come from `window.ProffiPOSDesignSystem_12f41e` (the compiled bundle).
- Icons are Material Design Icons via the `@mdi/font` CDN — matching the app's
  `Material.Icons.Avalonia` set.
- Text is shown in English; the real app is multilingual (en/ru/kk/tg/uz).
