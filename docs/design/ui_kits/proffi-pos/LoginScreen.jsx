// Proffi POS — Login screen. Split blue brand panel + sign-in form.
const { Button, TextField, Checkbox, IconButton } = window.ProffiPOSDesignSystem_12f41e;

function LoginScreen({ onLogin }) {
  const [email, setEmail] = React.useState("cashier@proffi.io");
  const [pw, setPw] = React.useState("••••••••");
  const [remember, setRemember] = React.useState(true);
  const [busy, setBusy] = React.useState(false);

  const submit = () => {
    setBusy(true);
    setTimeout(() => { setBusy(false); onLogin && onLogin(); }, 700);
  };

  const features = [
    { icon: "lightning-bolt", text: "Ring up a sale in two taps" },
    { icon: "barcode-scan", text: "Instant search & barcode scan" },
    { icon: "wifi-off", text: "Works offline, syncs later" },
  ];

  return (
    <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", height: "100%", background: "var(--slate-50)" }} onKeyDown={(e) => { if (e.key === "Enter" && !busy) submit(); }}>
      {/* Brand panel */}
      <div style={{ background: "var(--primary)", borderRadius: "0 24px 24px 0", padding: "56px", display: "flex", flexDirection: "column", alignItems: "center", justifyContent: "center", gap: "22px", textAlign: "center", position: "relative", overflow: "hidden" }}>
        <div style={{ position: "absolute", inset: 0, background: "radial-gradient(120% 80% at 50% -10%, rgba(255,255,255,.18), transparent 60%)", pointerEvents: "none" }}></div>
        <img src="../../assets/logo.png" width="104" height="104" alt="Proffi" style={{ borderRadius: "24px", position: "relative" }} />
        <div style={{ fontSize: "46px", fontWeight: 900, color: "#fff", letterSpacing: "-0.5px", position: "relative" }}>Proffi POS</div>
        <div style={{ fontSize: "18px", color: "#e6f1fc", maxWidth: "360px", lineHeight: 1.5, position: "relative" }}>
          Welcome back! Sign in to open your shift and start selling.
        </div>
        <div style={{ display: "flex", flexDirection: "column", gap: "12px", marginTop: "12px", position: "relative" }}>
          {features.map((f) => (
            <div key={f.text} style={{ display: "flex", alignItems: "center", gap: "12px", color: "#fff", fontSize: "15px", fontWeight: 600 }}>
              <span style={{ width: "34px", height: "34px", borderRadius: "50%", background: "rgba(255,255,255,.18)", display: "inline-flex", alignItems: "center", justifyContent: "center" }}>
                <i className={`mdi mdi-${f.icon}`} style={{ fontSize: "18px" }}></i>
              </span>
              {f.text}
            </div>
          ))}
        </div>
      </div>

      {/* Form panel */}
      <div style={{ position: "relative", padding: "48px", display: "flex", alignItems: "center", justifyContent: "center" }}>
        <div style={{ position: "absolute", top: "32px", right: "32px" }}>
          <IconButton icon="cog" variant="ghost" />
        </div>
        <div style={{ width: "100%", maxWidth: "380px" }}>
          <div style={{ fontSize: "34px", fontWeight: 900, color: "var(--slate-900)", textAlign: "center", marginBottom: "32px" }}>Sign In</div>
          <div style={{ marginBottom: "20px" }}>
            <TextField label="Email Address" icon="email-outline" value={email} onChange={(e) => setEmail(e.target.value)} placeholder="Enter your email" />
          </div>
          <div style={{ marginBottom: "16px" }}>
            <TextField label="Password" icon="lock-outline" type="password" value={pw} onChange={(e) => setPw(e.target.value)} placeholder="Enter your password" />
          </div>
          <Checkbox checked={remember} onChange={setRemember} label="Remember me" style={{ marginBottom: "24px" }} />
          <Button size="lg" fullWidth disabled={busy} onClick={submit} style={{ height: "56px", borderRadius: "var(--radius-md)", fontSize: "18px" }}>
            {busy ? "Authenticating…" : "Login"}
          </Button>
        </div>
      </div>
    </div>
  );
}
window.LoginScreen = LoginScreen;
