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

    /// <summary>Same thing for the returns screen's own till payout, kept as its own
    /// setting rather than shared with the exchange: a return and an exchange are
    /// different lines in the back office's expense report, and a store that wants
    /// them under one heading can simply pick the same category twice. Empty until an
    /// administrator picks one, and the return button refuses while it is — for the
    /// same reason the exchange does, since a return cannot be cancelled.</summary>
    string ReturnPayoutCategoryId { get; set; }

    /// <summary>Id записи из PhoneFormats — какой формат телефона у клиентов
    /// этой кассы. Пусто на кассе, где настройку не трогали; PhoneFormats.Resolve
    /// читает пустое и незнакомое как Россию, поэтому обновление существующей
    /// кассы ничего не меняет.</summary>
    string PhoneFormatId { get; set; }

    /// <summary>COM-порт дисплея покупателя. Пусто — VFD на этой кассе нет, и это
    /// нормальное состояние, а не незаконченная настройка.</summary>
    string CustomerDisplayPort { get; set; }

    /// <summary>Скорость порта. Своя настройка, потому что 9600 было зашито, а VFD
    /// встречаются на 2400 и 19200.</summary>
    int CustomerDisplayBaudRate { get; set; }

    /// <summary>Id записи из EscPosCodePages — своя, отдельная от принтерной:
    /// дисплей и принтер это разные железки с разными таблицами.</summary>
    string CustomerDisplayCodePageId { get; set; }

    /// <summary>Id записи из DisplayProtocols. Пусто на кассе, где настройку не
    /// трогали; Resolve читает пустое и незнакомое как ESC/POS, поэтому обновление
    /// существующей кассы ничего не меняет.</summary>
    string CustomerDisplayProtocolId { get; set; }

    /// <summary>Id записи из SerialFramings. Пусто — 8N1, то есть то, что давал голый
    /// конструктор SerialPort до появления этой настройки.</summary>
    string CustomerDisplayFramingId { get; set; }

    /// <summary>Поднимать ли DTR и RTS при открытии порта. Часть табло без этого
    /// данные не принимает, а некоторые от этих линий ещё и питаются. По умолчанию
    /// false — так вёл себя SerialPort раньше.</summary>
    bool CustomerDisplayDtrRts { get; set; }

    event EventHandler? SettingsChanged;

    void Save();
}
