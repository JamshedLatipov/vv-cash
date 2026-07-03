// Proffi POS — Register (refreshed). Three panes: category rail · live catalog ·
// always-visible order panel. Fewer clicks to pay, instant search/scan, hotkeys.
const {
  Button, IconButton, Badge, CategoryTile, ProductCard, QtyStepper,
  CouponChip, StatusDot, Modal, SegmentedControl, TextField, KeyHint, Numpad,
} = window.ProffiPOSDesignSystem_12f41e;

/* ---- top search / scan bar ---- */
function ScanBar({ value, onChange, inputRef }) {
  const [focus, setFocus] = React.useState(false);
  return (
    <div style={{
      flex: 1, display: "flex", alignItems: "center", gap: "10px",
      background: "#fff", borderRadius: "var(--radius-xl)", padding: "0 14px", height: "48px",
      border: `2px solid ${focus ? "var(--primary)" : "var(--slate-200)"}`, transition: "border-color .12s ease",
    }}>
      <i className="mdi mdi-magnify" style={{ fontSize: "22px", color: focus ? "var(--primary)" : "var(--slate-400)" }} />
      <input
        ref={inputRef} value={value} onChange={(e) => onChange(e.target.value)}
        onFocus={() => setFocus(true)} onBlur={() => setFocus(false)}
        placeholder="Search products or scan barcode…"
        style={{ flex: 1, minWidth: 0, border: "none", outline: "none", background: "transparent", fontFamily: "var(--font-sans)", fontSize: "15px", fontWeight: 500, color: "var(--slate-900)" }}
      />
      <i className="mdi mdi-barcode-scan" style={{ fontSize: "20px", color: "var(--slate-300)" }} />
      <KeyHint>F2</KeyHint>
    </div>
  );
}

/* ---- order-panel line item (compact, kiosk-friendly) ---- */
function LineItem({ item, onQty, onRemove }) {
  const line = item.price * item.qty;
  const meta = [item.color && item.color.name, item.size && `Size ${item.size}`, item.season, item.sku].filter(Boolean).join(" · ");
  return (
    <div style={{ display: "flex", gap: "10px", padding: "10px 0", borderBottom: "1px solid var(--border)", alignItems: "center" }}>
      <div style={{ position: "relative", width: "46px", height: "46px", flexShrink: 0, borderRadius: "var(--radius-md)", background: "var(--slate-50)", border: "1px solid var(--border)", display: "flex", alignItems: "center", justifyContent: "center" }}>
        <i className={`mdi mdi-${item.icon || "image-outline"}`} style={{ fontSize: "22px", color: "var(--slate-400)" }} />
        {item.color && <span title={item.color.name} style={{ position: "absolute", bottom: "-3px", right: "-3px", width: "14px", height: "14px", borderRadius: "50%", background: item.color.hex, border: "2px solid #fff", boxShadow: "0 1px 2px rgba(15,23,42,.2)" }} />}
      </div>
      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{ display: "flex", alignItems: "center", gap: "6px" }}>
          <span style={{ flex: 1, minWidth: 0, fontSize: "14px", fontWeight: 700, color: "var(--slate-900)", overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>{item.name}</span>
          {item.discountPercent != null && <Badge tone="danger">-{item.discountPercent}%</Badge>}
        </div>
        <div style={{ fontSize: "12px", color: "var(--slate-400)", overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap", marginTop: "2px" }}>{meta}</div>
      </div>
      <QtyStepper size="sm" value={item.qty} onChange={(v) => onQty(item.id, v)} />
      <span style={{ width: "62px", textAlign: "right", fontSize: "15px", fontWeight: 800, color: "var(--slate-900)" }}>{line.toFixed(2)}</span>
      <IconButton icon="close" variant="cart" size={30} iconSize={17} onClick={() => onRemove(item.id)} />
    </div>
  );
}

function TotalRow({ label, value, tone, icon, strong }) {
  const color = tone === "danger" ? "var(--danger)" : "var(--slate-600)";
  return (
    <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
      <span style={{ display: "inline-flex", alignItems: "center", gap: "5px", fontSize: strong ? "15px" : "14px", fontWeight: strong ? 700 : 500, color: strong ? "var(--slate-900)" : color }}>
        {icon && <i className={`mdi mdi-${icon}`} style={{ fontSize: "15px" }} />}{label}
      </span>
      <span style={{ fontSize: strong ? "15px" : "14px", fontWeight: strong ? 800 : 600, color: strong ? "var(--slate-900)" : color }}>{value}</span>
    </div>
  );
}

function PosScreen({ onPay }) {
  const D = window.PROFFI_DATA;
  const [cart, setCart] = React.useState([
    { ...D.products[0], qty: 1 },
    { ...D.products[1], qty: 2 },
    { ...D.products[5], qty: 3 },
  ]);
  const [activeCat, setActiveCat] = React.useState("all");
  const [query, setQuery] = React.useState("");
  const [coupons, setCoupons] = React.useState(["SUMMER20"]);
  const [couponInput, setCouponInput] = React.useState("");
  const [discountOpen, setDiscountOpen] = React.useState(false);
  const [parkOpen, setParkOpen] = React.useState(false);
  const [couponOpen, setCouponOpen] = React.useState(false);
  const [discMode, setDiscMode] = React.useState("pct");
  const [discVal, setDiscVal] = React.useState("0");
  const discKey = (k) => setDiscVal((v) => {
    if (k === "clear") return "0";
    if (k === "backspace") return v.length > 1 ? v.slice(0, -1) : "0";
    return v === "0" ? String(k) : v + k;
  });
  const searchRef = React.useRef(null);

  const add = (p) => setCart((c) => {
    const ex = c.find((i) => i.id === p.id);
    if (ex) return c.map((i) => i.id === p.id ? { ...i, qty: i.qty + 1 } : i);
    return [...c, { ...p, qty: 1 }];
  });
  const setQty = (id, v) => setCart((c) => c.map((i) => i.id === id ? { ...i, qty: v } : i));
  const remove = (id) => setCart((c) => c.filter((i) => i.id !== id));

  const subtotal = cart.reduce((s, i) => s + (i.originalPrice || i.price) * i.qty, 0);
  const itemDisc = cart.reduce((s, i) => s + ((i.originalPrice || i.price) - i.price) * i.qty, 0);
  const total = subtotal - itemDisc;
  const count = cart.reduce((s, i) => s + i.qty, 0);

  const q = query.trim().toLowerCase();
  const shown = D.products.filter((p) =>
    (activeCat === "all" || p.cat === activeCat) &&
    (!q || p.name.toLowerCase().includes(q) || p.sku.toLowerCase().includes(q))
  );
  const catName = D.categories.find((c) => c.id === activeCat)?.name || "ALL";
  const browsingCategories = activeCat === "all" && !q; // "ALL" shows categories, not every product
  const catCount = (id) => D.products.filter((p) => p.cat === id).length;

  // Kiosk-friendly scrolling: big pager buttons + auto-scroll to the newest item.
  const itemsRef = React.useRef(null);
  const prevLen = React.useRef(cart.length);
  const [pager, setPager] = React.useState({ up: false, down: false });
  const syncPager = React.useCallback(() => {
    const el = itemsRef.current; if (!el) return;
    setPager({ up: el.scrollTop > 4, down: el.scrollTop + el.clientHeight < el.scrollHeight - 4 });
  }, []);
  const page = (dir) => { const el = itemsRef.current; if (!el) return; el.scrollBy({ top: dir * el.clientHeight * 0.8, behavior: "smooth" }); setTimeout(syncPager, 320); };
  React.useEffect(() => {
    const el = itemsRef.current;
    if (el && cart.length > prevLen.current) el.scrollTop = el.scrollHeight; // reveal newest, no manual scroll
    prevLen.current = cart.length;
    syncPager();
  }, [cart, syncPager]);

  // Hotkeys: F2 focus search, F4 pay, Esc clear search
  React.useEffect(() => {
    const h = (e) => {
      if (e.key === "F2") { e.preventDefault(); searchRef.current && searchRef.current.focus(); }
      else if (e.key === "F4") { e.preventDefault(); if (cart.length) onPay && onPay(total); }
      else if (e.key === "Escape") { setQuery(""); }
    };
    window.addEventListener("keydown", h);
    return () => window.removeEventListener("keydown", h);
  }, [cart.length, total, onPay]);

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%", background: "var(--background)" }}>
      {/* Top bar */}
      <div style={{ background: "#fff", borderBottom: "1px solid var(--slate-200)", padding: "12px 20px", display: "flex", alignItems: "center", gap: "20px", zIndex: 10 }}>
        <div style={{ display: "flex", alignItems: "center", gap: "10px", flexShrink: 0 }}>
          <img src="../../assets/logo.png" width="30" height="30" alt="Proffi" style={{ borderRadius: "7px" }} />
          <span style={{ fontSize: "17px", fontWeight: 800, color: "var(--slate-900)", letterSpacing: "-0.3px" }}>Proffi POS</span>
        </div>
        <ScanBar value={query} onChange={setQuery} inputRef={searchRef} />
        <div style={{ display: "flex", alignItems: "center", gap: "8px", flexShrink: 0 }}>
          <IconButton icon="clipboard-text-clock-outline" size={44} title="Parked" />
          <Badge tone="solid" shape="pill" style={{ marginLeft: "-14px", marginTop: "-20px" }}>2</Badge>
          <IconButton icon="backup-restore" size={44} title="Returns" />
          <IconButton icon="sync" size={44} title="Sync" />
          <div style={{ width: "1px", height: "24px", background: "var(--slate-200)" }} />
          <div style={{ display: "flex", alignItems: "center", gap: "8px", background: "var(--slate-100)", borderRadius: "var(--radius-pill)", padding: "4px 4px 4px 12px" }}>
            <span style={{ fontSize: "13px", fontWeight: 700, color: "var(--slate-700)" }}>Aigerim K.</span>
            <span style={{ width: "30px", height: "30px", borderRadius: "50%", background: "var(--primary)", color: "#fff", display: "inline-flex", alignItems: "center", justifyContent: "center", fontSize: "13px", fontWeight: 800 }}>AK</span>
          </div>
        </div>
      </div>

      {/* Body */}
      <div style={{ flex: 1, minHeight: 0, display: "flex", gap: "16px", padding: "16px" }}>
        {/* Rail */}
        <div style={{ width: "92px", flexShrink: 0, display: "flex", flexDirection: "column", gap: "8px", overflowY: "auto" }}>
          <CategoryTile icon="view-grid" label="ALL" active={activeCat === "all"} width={92} height={80} onClick={() => setActiveCat("all")} />
          {D.categories.slice(1).map((c) => (
            <CategoryTile key={c.id} icon={c.icon} label={c.name} active={activeCat === c.id} width={92} height={80} onClick={() => setActiveCat(c.id)} />
          ))}
        </div>

        {/* Catalog */}
        <div style={{ flex: 1, minWidth: 0, display: "flex", flexDirection: "column" }}>
          <div style={{ display: "flex", alignItems: "baseline", gap: "10px", marginBottom: "14px" }}>
            <span style={{ fontSize: "22px", fontWeight: 800, color: "var(--slate-900)", letterSpacing: "-0.4px" }}>{browsingCategories ? "Categories" : catName}</span>
            <span style={{ fontSize: "14px", color: "var(--slate-400)", fontWeight: 500 }}>{browsingCategories ? `${D.categories.length - 1} categories` : `${shown.length} items`}</span>
          </div>
          <div style={{ flex: 1, minHeight: 0, overflowY: "auto", paddingRight: "4px" }}>
            {browsingCategories
              ? <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(178px, 1fr))", gap: "14px", alignContent: "flex-start" }}>
                  {D.categories.slice(1).map((c) => (
                    <button key={c.id} onClick={() => setActiveCat(c.id)}
                      style={{ display: "flex", flexDirection: "column", alignItems: "flex-start", gap: "12px", padding: "18px", background: "#fff", border: "1px solid var(--border)", borderRadius: "var(--radius-xl)", cursor: "pointer", fontFamily: "var(--font-sans)", textAlign: "left" }}>
                      <span style={{ width: "52px", height: "52px", borderRadius: "var(--radius-lg)", background: "var(--primary-light)", display: "inline-flex", alignItems: "center", justifyContent: "center" }}>
                        <i className={`mdi mdi-${c.icon}`} style={{ fontSize: "28px", color: "var(--primary)" }} />
                      </span>
                      <span>
                        <span style={{ display: "block", fontSize: "16px", fontWeight: 700, color: "var(--slate-900)" }}>{c.name}</span>
                        <span style={{ display: "block", fontSize: "13px", color: "var(--slate-400)", marginTop: "2px" }}>{catCount(c.id)} items</span>
                      </span>
                    </button>
                  ))}
                </div>
              : shown.length === 0
                ? <div style={{ height: "100%", display: "flex", flexDirection: "column", alignItems: "center", justifyContent: "center", gap: "10px", color: "var(--slate-300)" }}>
                    <i className="mdi mdi-package-variant" style={{ fontSize: "48px" }} />
                    <span style={{ fontSize: "15px", fontWeight: 600, color: "var(--slate-400)" }}>No products match “{query}”</span>
                  </div>
                : <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(178px, 1fr))", gap: "14px", alignContent: "flex-start" }}>
                    {shown.map((p) => <ProductCard key={p.id} {...p} width="100%" attributes={[p.size, p.season].filter(Boolean)} onClick={() => add(p)} />)}
                  </div>}
          </div>
        </div>

        {/* Order panel */}
        <div style={{ width: "540px", flexShrink: 0, background: "#fff", borderRadius: "var(--radius-2xl)", border: "1px solid var(--slate-200)", boxShadow: "0 12px 32px -16px rgba(15,23,42,.18)", display: "flex", flexDirection: "column", overflow: "hidden" }}>
          {/* header */}
          <div style={{ padding: "11px 20px", borderBottom: "1px solid var(--border)", display: "flex", alignItems: "center", justifyContent: "space-between", gap: "10px" }}>
            <div style={{ minWidth: 0 }}>
              <div style={{ fontSize: "16px", fontWeight: 800, color: "var(--slate-900)", lineHeight: 1.2 }}>Current Order</div>
              <div style={{ fontSize: "12px", color: "var(--slate-400)" }}>#8429 · Terminal 01</div>
            </div>
            <div style={{ display: "flex", alignItems: "center", gap: "8px", flexShrink: 0 }}>
              <button style={{ padding: "7px 11px", background: "var(--primary-light)", border: "none", borderRadius: "var(--radius-pill)", cursor: "pointer", display: "inline-flex", alignItems: "center", gap: "6px", color: "var(--primary)", fontFamily: "var(--font-sans)", fontSize: "13px", fontWeight: 700 }}>
                <i className="mdi mdi-account-plus-outline" style={{ fontSize: "17px" }} /> Add customer
              </button>
              <button onClick={() => setCart([])} style={{ background: "none", border: "none", cursor: "pointer", display: "inline-flex", alignItems: "center", gap: "4px", color: "var(--slate-400)", fontFamily: "var(--font-sans)", fontSize: "13px", fontWeight: 600 }}>
                <i className="mdi mdi-delete-outline" style={{ fontSize: "16px" }} /> Clear
              </button>
            </div>
          </div>

          {/* items only — big pager buttons so kiosks don't need finger-drag scrolling */}
          <div style={{ position: "relative", flex: 1, minHeight: 0 }}>
            <div ref={itemsRef} onScroll={syncPager} style={{ height: "100%", overflowY: "auto", padding: "0 20px" }}>
              {cart.length === 0
                ? <div style={{ height: "100%", minHeight: "140px", display: "flex", flexDirection: "column", alignItems: "center", justifyContent: "center", gap: "10px", color: "var(--slate-300)" }}>
                    <i className="mdi mdi-barcode-scan" style={{ fontSize: "40px" }} />
                    <span style={{ fontSize: "14px", fontWeight: 600, color: "var(--slate-400)" }}>Scan or tap items to begin</span>
                  </div>
                : cart.map((i) => <LineItem key={i.id} item={i} onQty={setQty} onRemove={remove} />)}
            </div>
            {pager.up && (
              <button onClick={() => page(-1)} aria-label="Scroll up" style={{ position: "absolute", top: "6px", right: "10px", width: "46px", height: "46px", borderRadius: "var(--radius-pill)", border: "none", background: "var(--primary)", color: "#fff", cursor: "pointer", display: "flex", alignItems: "center", justifyContent: "center", boxShadow: "0 6px 16px -4px rgba(0,117,226,.6)", zIndex: 4 }}>
                <i className="mdi mdi-chevron-up" style={{ fontSize: "28px" }} />
              </button>
            )}
            {pager.down && (
              <button onClick={() => page(1)} aria-label="Scroll down" style={{ position: "absolute", bottom: "6px", right: "10px", width: "46px", height: "46px", borderRadius: "var(--radius-pill)", border: "none", background: "var(--primary)", color: "#fff", cursor: "pointer", display: "flex", alignItems: "center", justifyContent: "center", boxShadow: "0 6px 16px -4px rgba(0,117,226,.6)", zIndex: 4 }}>
                <i className="mdi mdi-chevron-down" style={{ fontSize: "28px" }} />
              </button>
            )}
          </div>

          {/* pinned footer: coupons · totals · actions · pay */}
          <div style={{ borderTop: "1px solid var(--border)", padding: "14px 20px", display: "flex", flexDirection: "column", gap: "12px", background: "var(--slate-50)" }}>
            {coupons.length > 0 && (
              <div style={{ display: "flex", flexWrap: "wrap", gap: "6px" }}>
                {coupons.map((c) => <CouponChip key={c} code={c} onRemove={() => setCoupons((x) => x.filter((v) => v !== c))} />)}
              </div>
            )}
            <div style={{ display: "flex", flexDirection: "column", gap: "8px" }}>
              <TotalRow label="Subtotal" value={subtotal.toFixed(2)} />
              {itemDisc > 0 && <TotalRow label="Discount" value={`-${itemDisc.toFixed(2)}`} tone="danger" icon="tag" />}
              <TotalRow label="Total" value={total.toFixed(2)} strong />
            </div>

            {/* secondary actions */}
            <div style={{ display: "flex", gap: "8px" }}>
              <Button variant="secondary" size="sm" style={{ flex: 1 }} icon={<i className="mdi mdi-tag-outline" />} onClick={() => setDiscountOpen(true)}>Discount</Button>
              <Button variant="secondary" size="sm" style={{ flex: 1 }} icon={<i className="mdi mdi-ticket-percent-outline" />} onClick={() => setCouponOpen(true)}>Coupon</Button>
              <Button variant="secondary" size="sm" style={{ flex: 1 }} icon={<i className="mdi mdi-pause-circle-outline" />} onClick={() => setParkOpen(true)}>Hold</Button>
              <IconButton icon="printer" size={38} />
            </div>

            {/* pay */}
            <Button disabled={cart.length === 0} onClick={() => onPay && onPay(total)}
              style={{ height: "64px", borderRadius: "var(--radius-xl)", justifyContent: "space-between", padding: "0 20px" }}>
              <span style={{ display: "inline-flex", alignItems: "center", gap: "10px", fontSize: "18px", fontWeight: 800 }}>
                <i className="mdi mdi-cash-multiple" style={{ fontSize: "24px" }} /> Pay · {count} items
              </span>
              <span style={{ display: "inline-flex", alignItems: "center", gap: "10px" }}>
                <span style={{ fontSize: "24px", fontWeight: 900, letterSpacing: "-0.5px" }}>{total.toFixed(2)}</span>
                <KeyHint tone="dark">F4</KeyHint>
              </span>
            </Button>
          </div>
        </div>
      </div>

      {/* Status bar */}
      <div style={{ background: "var(--slate-50)", borderTop: "1px solid var(--slate-200)", padding: "7px 20px", display: "flex", alignItems: "center", justifyContent: "space-between" }}>
        <div style={{ display: "flex", gap: "16px" }}>
          <StatusDot tone="online" label="System Online" />
          <StatusDot tone="online" label="Printer Ready" />
        </div>
        <div style={{ display: "flex", alignItems: "center", gap: "12px" }}>
          <span style={{ display: "inline-flex", alignItems: "center", gap: "6px", fontSize: "11px", color: "var(--slate-400)", fontWeight: 600 }}>
            <KeyHint>F2</KeyHint> Search <KeyHint>F4</KeyHint> Pay
          </span>
          <span style={{ fontSize: "10px", fontWeight: 700, letterSpacing: "1px", color: "var(--slate-400)" }}>V 2.4.0 · LXP-09921</span>
        </div>
      </div>

      {discountOpen && (
        <Modal icon="tag-outline" title="Order Discount" onClose={() => setDiscountOpen(false)}
          actions={<><Button variant="secondary" fullWidth onClick={() => setDiscVal("0")}>Clear</Button><Button fullWidth onClick={() => setDiscountOpen(false)}>Apply</Button></>}>
          <div style={{ display: "flex", flexDirection: "column", gap: "16px" }}>
            <SegmentedControl value={discMode} onChange={setDiscMode} options={[{ label: "By Percent (%)", value: "pct" }, { label: "By Amount", value: "amt" }]} />
            <div style={{ background: "var(--slate-50)", borderRadius: "var(--radius-lg)", padding: "14px 18px", textAlign: "right", fontSize: "34px", fontWeight: 900, color: "var(--slate-900)", letterSpacing: "-0.5px" }}>
              {discMode === "pct" ? `${discVal}%` : discVal}
            </div>
            <Numpad onKey={discKey} />
          </div>
        </Modal>
      )}
      {parkOpen && (
        <Modal icon="pause-circle-outline" title="Hold Order" onClose={() => setParkOpen(false)}
          actions={<><Button variant="secondary" fullWidth onClick={() => setParkOpen(false)}>Back</Button><Button fullWidth onClick={() => setParkOpen(false)}>Hold</Button></>}>
          <TextField variant="filled" label="Note (optional)" placeholder="e.g. waiting for card" />
        </Modal>
      )}
      {couponOpen && (
        <Modal icon="ticket-percent-outline" title="Add Coupon" onClose={() => setCouponOpen(false)}
          actions={<><Button variant="secondary" fullWidth onClick={() => setCouponOpen(false)}>Cancel</Button><Button fullWidth onClick={() => { if (couponInput.trim()) { setCoupons((c) => [...c, couponInput.trim().toUpperCase()]); setCouponInput(""); } setCouponOpen(false); }}>Apply</Button></>}>
          <TextField variant="filled" label="Coupon code" value={couponInput} onChange={(e) => setCouponInput(e.target.value)} placeholder="e.g. SUMMER20" />
        </Modal>
      )}
    </div>
  );
}
window.PosScreen = PosScreen;
