namespace VvCash.Constants;

/// <summary>Codes of the server-driven cash feature flags, mirrored from the
/// options seeded by backend migration 20260728000800. Kept in one place so that
/// no string literal for a flag appears anywhere else: a typo in a literal reads
/// as an unknown code, an unknown code silently means "enabled", and so the
/// mistake would never fail loudly — it would just leave a function switched on
/// that the store asked to have switched off.</summary>
public static class CashFeatureCodes
{
    public const string Returns = "cash_returns_enabled";
    public const string ParkedSales = "cash_parked_sales_enabled";
    public const string MixedPayment = "cash_mixed_payment_enabled";
    public const string CustomerDisplay = "cash_customer_display_enabled";
    public const string CustomerRegistration = "cash_customer_registration_enabled";
    public const string SellerSwitch = "cash_seller_switch_enabled";
    public const string ReturnPrintReceipt = "cash_return_print_receipt";
    public const string ReturnOpenDrawer = "cash_return_open_drawer";
}
