using System;
using System.Collections.Generic;
using System.IO.Ports;

namespace VvCash.Models;

/// <summary>Как устроен байт на проводе: сколько бит данных, есть ли чётность,
/// сколько стоп-бит.
///
/// Отдельная настройка, потому что касса открывала порт голым конструктором
/// SerialPort, то есть всегда 8N1, а часть табло покупателя работает на 7E1 и на 8N1
/// не отвечает вовсе.</summary>
public sealed class SerialFraming
{
    public SerialFraming(string id, string displayName, int dataBits, Parity parity, StopBits stopBits)
    {
        Id = id;
        DisplayName = displayName;
        DataBits = dataBits;
        Parity = parity;
        StopBits = stopBits;
    }

    /// <summary>То, что ложится в настройки.</summary>
    public string Id { get; }

    /// <summary>Не переводится: «8N1» опознаётся независимо от письменности.</summary>
    public string DisplayName { get; }

    public int DataBits { get; }
    public Parity Parity { get; }
    public StopBits StopBits { get; }
}

/// <summary>Каталог, по образцу EscPosCodePages. Две записи — те, что встречаются на
/// табло покупателя; остальные комбинации в природе на этих железках не попадались, и
/// короткий список выбора лучше, чем длинный список неверных ответов.</summary>
public static class SerialFramings
{
    public static readonly SerialFraming EightN1 = new("8N1", "8N1", 8, Parity.None, StopBits.One);
    public static readonly SerialFraming SevenE1 = new("7E1", "7E1", 7, Parity.Even, StopBits.One);

    public static IReadOnlyList<SerialFraming> All { get; } =
        Array.AsReadOnly(new[] { EightN1, SevenE1 });

    /// <summary>Что даёт голый конструктор SerialPort — то есть нынешнее поведение.</summary>
    public static SerialFraming Default => EightN1;

    public static SerialFraming Resolve(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            foreach (var framing in All)
            {
                if (string.Equals(framing.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return framing;
                }
            }
        }

        return Default;
    }
}
