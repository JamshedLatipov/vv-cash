using System;
using System.Collections.Generic;
using VvCash.Models;

namespace VvCash.Services;

public interface ISettingsService
{
    string BackendUrl { get; set; }
    string CashRegisterToken { get; set; }
    string AuthToken { get; set; }
    DateTime? AuthTokenExpiresAt { get; set; }
    int SyncIntervalMinutes { get; set; }
    string Language { get; set; }
    List<PrinterConfig> Printers { get; set; }

    /// <summary>The old local checkbox value — still loaded and saved by
    /// SettingsViewModel, deliberately, so that removing the server-driven flags
    /// later restores this rather than losing it. No longer read by anything that
    /// decides register behaviour: opening the cash drawer on a return is now
    /// decided by ICashFeatureService reading CashFeatureCodes.ReturnOpenDrawer
    /// (see ReturnsViewModel). Do not add a read of this field to gate behaviour.</summary>
    bool ReturnOpenCashDrawer { get; set; }

    /// <summary>Same story as <see cref="ReturnOpenCashDrawer"/>, for
    /// CashFeatureCodes.ReturnPrintReceipt instead.</summary>
    bool ReturnPrintReceipt { get; set; }

    /// <summary>Id of the payment category the exchange screen files its till payout
    /// under (POST /documents/money/expense/create/ requires one and the server has no
    /// default). Empty until an administrator picks one on the settings screen, and the
    /// exchange button refuses outright while it is — refusing costs nothing, whereas
    /// discovering it at the payout step leaves a return already booked.</summary>
    string ExchangePayoutCategoryId { get; set; }

    event EventHandler? SettingsChanged;

    void Save();
}
