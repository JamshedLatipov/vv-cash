// Proffi POS — Mixed payment (v3).
// Left panel: method tiles · amount display · split row [ keypad | quick amounts + order discount ].
// Right panel: receipt (subtotal · discount · total due · tender breakdown · remaining/change · confirm).
const { Button, KeyHint } = window.ProffiPOSDesignSystem_12f41e;

const METHODS = [
  { id: "cash", icon: "cash", label: "Cash" },
  { id: "card", icon: "credit-card-outline", label: "Card" },
  { id: "gift", icon: "wallet-giftcard", label: "Gift" },
];
const DISCOUNTS = [0, 5, 10, 15];

function MethodTile({ m, active, onSelect }) {
  return (
    <button onClick={onSelect}
      style={{ flex: 1, cursor: "pointer", fontFamily: "var(--font-sans)", display: "flex", flexDirection: "column", alignItems: "center", gap: "6px", padding: "12px 8px",
        background: active ? "var(--primary)" : "#fff", color: active ? "#fff" : "var(--slate-600)",
        border: `2px solid ${active ? "var(--primary)" : "var(--slate-200)"}`, borderRadius: "var(--radius-xl)", transition: "all .12s" }}>
      <i className={`mdi mdi-${m.icon}`} style={{ fontSize: "24px" }} />
      <span style={{ fontSize: "14px", fontWeight: 700 }}>{m.label}</span>
    </button>
  );
}

function Key({ label, action, onPress }) {
  const [h, setH] = React.useState(false);
  const [p, setP] = React.useState(false);
  const bg = p ? "var(--primary)" : action ? (h ? "var(--slate-200)" : "var(--slate-100)") : (h ? "var(--secondary-bg)" : "#fff");
  const fg = p ? "#fff" : action ? "var(--slate-700)" : "var(--slate-900)";
  return (
    <button onClick={onPress} onMouseEnter={() => setH(true)} onMouseLeave={() => { setH(false); setP(false); }} onMouseDown={() => setP(true)} onMouseUp={() => setP(false)}
      style={{ border: action ? "none" : "1px solid var(--border)", background: bg, color: fg, borderRadius: "var(--radius-xl)", cursor: "pointer",
        fontFamily: "var(--font-sans)", fontSize: "24px", fontWeight: 700, display: "flex", alignItems: "center", justifyContent: "center", transition: "background .1s, color .1s" }}>
      {label}
    </button>
  );
}

function QuickBtn({ children, onClick, tall }) {
  const [h, setH] = React.useState(false);
  return (
    <button onClick={onClick} onMouseEnter={() => setH(true)} onMouseLeave={() => setH(false)}
      style={{ width: "100%", height: tall ? "auto" : "42px", flex: tall ? 1 : "none", cursor: "pointer", fontFamily: "var(--font-sans)", fontSize: "14px", fontWeight: 700,
        background: h ? "var(--primary-light)" : "#fff", color: h ? "var(--primary)" : "var(--slate-700)",
        border: `1px solid ${h ? "var(--primary)" : "var(--slate-200)"}`, borderRadius: "var(--radius-lg)", transition: "all .12s" }}>
      {children}
    </button>
  );
}

function PaymentScreen({ total = 0, onBack, onConfirm }) {
  const [amounts, setAmounts] = React.useState({ cash: 0, card: 0, gift: 0 });
  const [active, setActive] = React.useState("cash");
  const [discPct, setDiscPct] = React.useState(0);

  const due = +(total * (1 - discPct / 100)).toFixed(2);
  const paid = amounts.cash + amounts.card + amounts.gift;
  const remaining = Math.max(0, +(due - paid).toFixed(2));
  const change = Math.max(0, +(paid - due).toFixed(2));
  const done = remaining <= 0;
  const pct = due > 0 ? Math.min(100, (paid / due) * 100) : 100;

  const setActiveAmt = (v) => setAmounts((a) => ({ ...a, [active]: Math.max(0, +v.toFixed(2)) }));
  const key = (k) => setAmounts((a) => {
    const cur = String(Math.round(a[active] * 100));
    let next;
    if (k === "clear") next = "0";
    else if (k === "back") next = cur.slice(0, -1) || "0";
    else next = (cur === "0" ? "" : cur) + k;
    return { ...a, [active]: parseInt(next || "0", 10) / 100 };
  });

  const roundUps = React.useMemo(() => {
    const rem = +(due - (paid - amounts[active])).toFixed(2);
    const base = rem > 0 ? rem : due;
    const ups = [10, 50, 100].map((s) => Math.ceil(base / s) * s).filter((v, i, arr) => v > base && arr.indexOf(v) === i);
    return { exact: +base.toFixed(2), ups: ups.slice(0, 3) };
  }, [due, paid, amounts, active]);

  React.useEffect(() => {
    const h = (e) => {
      if (e.key === "Enter" && done) { e.preventDefault(); onConfirm && onConfirm(); }
      else if (e.key === "Escape") onBack && onBack();
      else if (/[0-9]/.test(e.key)) key(e.key);
      else if (e.key === "Backspace") key("back");
    };
    window.addEventListener("keydown", h);
    return () => window.removeEventListener("keydown", h);
  }, [done, onConfirm, onBack, active]);

  const activeLabel = METHODS.find((m) => m.id === active).label;
  const keys = [["1"], ["2"], ["3"], ["4"], ["5"], ["6"], ["7"], ["8"], ["9"], ["clear", "C", true], ["0"], ["back", "⌫", true]];
  const eyebrow = { fontSize: "11px", fontWeight: 700, letterSpacing: "1px", color: "var(--slate-400)" };

  return (
    <div style={{ height: "100%", background: "var(--background)", display: "flex", flexDirection: "column" }}>
      {/* header */}
      <div style={{ background: "#fff", borderBottom: "1px solid var(--slate-200)", padding: "12px 24px", display: "flex", alignItems: "center", gap: "12px", flexShrink: 0 }}>
        <button onClick={onBack} aria-label="Back" style={{ width: "40px", height: "40px", borderRadius: "var(--radius-lg)", border: "none", background: "var(--slate-100)", cursor: "pointer", display: "inline-flex", alignItems: "center", justifyContent: "center", color: "var(--slate-700)" }}>
          <i className="mdi mdi-arrow-left" style={{ fontSize: "22px" }} />
        </button>
        <img src="../../assets/logo.png" width="28" height="28" alt="Proffi" style={{ borderRadius: "7px" }} />
        <span style={{ fontSize: "18px", fontWeight: 800, color: "var(--slate-900)", whiteSpace: "nowrap" }}>Payment</span>
        <span style={{ marginLeft: "auto", fontSize: "13px", color: "var(--slate-400)", whiteSpace: "nowrap" }}>Order #8429 · Terminal 01</span>
      </div>

      <div style={{ flex: 1, minHeight: 0, display: "grid", gridTemplateColumns: "1fr 340px", gap: "20px", padding: "20px" }}>
        {/* LEFT — entry */}
        <div style={{ background: "#fff", border: "1px solid var(--slate-200)", borderRadius: "var(--radius-2xl)", padding: "20px", display: "flex", flexDirection: "column", gap: "16px", minHeight: 0 }}>
          <div style={{ display: "flex", gap: "10px" }}>
            {METHODS.map((m) => <MethodTile key={m.id} m={m} active={active === m.id} onSelect={() => setActive(m.id)} />)}
          </div>

          <div style={{ background: "var(--slate-50)", borderRadius: "var(--radius-lg)", padding: "14px 18px", display: "flex", alignItems: "baseline", justifyContent: "space-between" }}>
            <span style={{ fontSize: "13px", fontWeight: 700, color: "var(--slate-400)" }}>{activeLabel} amount</span>
            <span style={{ fontSize: "38px", fontWeight: 900, color: "var(--slate-900)", letterSpacing: "-1px", lineHeight: 1 }}>{amounts[active].toFixed(2)}</span>
          </div>

          {/* split: keypad | quick amounts + discount */}
          <div style={{ flex: 1, minHeight: 0, display: "grid", gridTemplateColumns: "1fr 1fr", gap: "16px" }}>
            <div style={{ display: "grid", gridTemplateColumns: "repeat(3, 1fr)", gridAutoRows: "1fr", gap: "10px", minHeight: 0 }}>
              {keys.map(([k, lbl, action]) => <Key key={k} label={lbl || k} action={action} onPress={() => key(k)} />)}
            </div>

            <div style={{ display: "flex", flexDirection: "column", gap: "10px", minHeight: 0 }}>
              <span style={eyebrow}>QUICK AMOUNT</span>
              <QuickBtn tall onClick={() => setActiveAmt(roundUps.exact)}>Exact · {roundUps.exact.toFixed(2)}</QuickBtn>
              {roundUps.ups.map((v) => <QuickBtn key={v} tall onClick={() => setActiveAmt(v)}>{v.toFixed(2)}</QuickBtn>)}
              <span style={{ ...eyebrow, marginTop: "6px" }}>ORDER DISCOUNT</span>
              <div style={{ display: "grid", gridTemplateColumns: "repeat(4, 1fr)", gap: "8px" }}>
                {DISCOUNTS.map((d) => {
                  const on = discPct === d;
                  return (
                    <button key={d} onClick={() => setDiscPct(d)}
                      style={{ height: "42px", cursor: "pointer", fontFamily: "var(--font-sans)", fontSize: "14px", fontWeight: 700,
                        background: on ? "var(--primary)" : "#fff", color: on ? "#fff" : "var(--slate-700)",
                        border: `1px solid ${on ? "var(--primary)" : "var(--slate-200)"}`, borderRadius: "var(--radius-lg)", transition: "all .12s" }}>
                      {d === 0 ? "No" : `${d}%`}
                    </button>
                  );
                })}
              </div>
            </div>
          </div>
        </div>

        {/* RIGHT — receipt / confirm */}
        <div style={{ background: "#fff", border: "1px solid var(--slate-200)", borderRadius: "var(--radius-2xl)", padding: "22px", display: "flex", flexDirection: "column", minHeight: 0 }}>
          <div style={{ display: "flex", flexDirection: "column", gap: "8px" }}>
            <div style={{ display: "flex", justifyContent: "space-between", fontSize: "14px", color: "var(--slate-500)", fontWeight: 500 }}>
              <span>Subtotal</span><span>{total.toFixed(2)}</span>
            </div>
            {discPct > 0 && (
              <div style={{ display: "flex", justifyContent: "space-between", fontSize: "14px", color: "var(--danger)", fontWeight: 600 }}>
                <span>Order discount · {discPct}%</span><span>-{(total - due).toFixed(2)}</span>
              </div>
            )}
          </div>
          <div style={{ height: "1px", background: "var(--border)", margin: "14px 0" }} />
          <div style={{ ...eyebrow }}>TOTAL DUE</div>
          <div style={{ fontSize: "40px", fontWeight: 900, letterSpacing: "-1px", color: "var(--slate-900)", lineHeight: 1.1, marginTop: "2px" }}>{due.toFixed(2)}</div>

          <div style={{ height: "1px", background: "var(--border)", margin: "18px 0" }} />

          <div style={{ display: "flex", flexDirection: "column", gap: "12px" }}>
            {METHODS.map((m) => {
              const v = amounts[m.id];
              return (
                <div key={m.id} onClick={() => setActive(m.id)} style={{ display: "flex", alignItems: "center", gap: "10px", cursor: "pointer" }}>
                  <i className={`mdi mdi-${m.icon}`} style={{ fontSize: "18px", color: v > 0 ? "var(--primary)" : "var(--slate-300)" }} />
                  <span style={{ flex: 1, fontSize: "14px", fontWeight: 600, color: v > 0 ? "var(--slate-700)" : "var(--slate-400)" }}>{m.label}</span>
                  <span style={{ fontSize: "15px", fontWeight: 700, color: v > 0 ? "var(--slate-900)" : "var(--slate-300)" }}>{v.toFixed(2)}</span>
                </div>
              );
            })}
          </div>

          <div style={{ marginTop: "18px", height: "8px", borderRadius: "var(--radius-pill)", background: "var(--slate-100)", overflow: "hidden" }}>
            <div style={{ height: "100%", width: `${pct}%`, background: done ? "var(--success)" : "var(--primary)", borderRadius: "var(--radius-pill)", transition: "width .2s ease" }} />
          </div>

          <div style={{ marginTop: "16px", background: done ? "var(--emerald-100)" : "var(--red-50)", borderRadius: "var(--radius-xl)", padding: "16px 18px", display: "flex", alignItems: "center", justifyContent: "space-between" }}>
            <span style={{ fontSize: "13px", fontWeight: 700, color: done ? "var(--emerald-600)" : "var(--red-600)" }}>{done ? (change > 0 ? "CHANGE DUE" : "FULLY PAID") : "REMAINING"}</span>
            <span style={{ fontSize: "30px", fontWeight: 900, letterSpacing: "-0.5px", color: done ? "var(--emerald-600)" : "var(--red-600)" }}>{(done ? change : remaining).toFixed(2)}</span>
          </div>

          <div style={{ flex: 1 }} />

          <Button size="lg" fullWidth disabled={!done} onClick={onConfirm}
            style={{ height: "60px", marginTop: "16px", justifyContent: "space-between", padding: "0 22px" }}
            icon={<span style={{ display: "inline-flex", alignItems: "center", gap: "10px", fontSize: "18px", fontWeight: 800 }}><i className="mdi mdi-check-circle" style={{ fontSize: "24px" }} /> Confirm</span>}
            iconRight={<KeyHint tone="dark">Enter</KeyHint>} />
        </div>
      </div>
    </div>
  );
}

window.PaymentScreen = PaymentScreen;
