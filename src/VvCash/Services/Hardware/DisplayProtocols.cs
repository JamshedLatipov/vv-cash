using System;
using System.Collections.Generic;
using VvCash.Services.Hardware.Protocols;

namespace VvCash.Services.Hardware;

/// <summary>Каталог диалектов табло, по образцу EscPosCodePages.
///
/// Не редактируется из интерфейса: кассир не должен иметь возможности задать диалект,
/// которого не существует. Новая запись — правка этого файла и релиз.
///
/// AEDEX сюда сознательно не попал: точных байтов протокола нет, проверить негде, а
/// реализация по памяти удлинила бы автоподбор с 28 шагов до 35 и при этом
/// называлась бы поддержкой. Понадобится — добавляется одной строкой.</summary>
public static class DisplayProtocols
{
    public static readonly IDisplayProtocol EscPos = new EscPosDisplayProtocol();
    public static readonly IDisplayProtocol Cd5220 = new Cd5220DisplayProtocol();
    public static readonly IDisplayProtocol Numeric = new NumericDisplayProtocol();
    public static readonly IDisplayProtocol Raw = new RawDisplayProtocol();

    public static IReadOnlyList<IDisplayProtocol> All { get; } =
        Array.AsReadOnly(new[] { EscPos, Cd5220, Numeric, Raw });

    /// <summary>Чем становится касса, у которой настройку не трогали. ESC/POS —
    /// то, что она слала до появления этого каталога.</summary>
    public static IDisplayProtocol Default => EscPos;

    /// <summary>Единственное место, где Id превращается в запись. Функцией, а не
    /// веткой по месту: правило «пусто или незнакомо — значит ESC/POS» должно быть
    /// одно и проверяться тестом.</summary>
    public static IDisplayProtocol Resolve(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            foreach (var protocol in All)
            {
                if (string.Equals(protocol.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return protocol;
                }
            }
        }

        return Default;
    }
}
