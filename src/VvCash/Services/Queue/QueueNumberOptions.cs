using System;

namespace VvCash.Services.Queue;

/// <summary>Снимок настроек формы номера очереди — то, от чего NumberPool
/// считает срез и ключ пересборки, а QueueClient берёт индекс кассы и
/// префикс. Один record, а не шесть свойств IQueueSettings, читаемых
/// по очереди: пулу нужны согласованные значения на одну выдачу, а
/// настройки могут поменяться между двумя чтениями.
///
/// Prefix и Secret в PoolKey не входят (см. NumberPool.PoolKey): префикс
/// прибавляется к уже выданному числу, секрет влияет только на порядок при
/// следующей пересборке.</summary>
public sealed record QueueNumberOptions(
    int TillIndex, int TillCount, int Min, int Max, bool Shuffle,
    string Prefix, string Secret)
{
    // Константы, с которыми пул уехал на точки до этой настройки. Значения
    // по умолчанию SettingsData — они же, и это не совпадение: settings.json
    // без новых полей обязан читаться как сегодняшнее поведение.
    public const int DefaultTillCount = 5;
    public const int DefaultMin = 100;
    public const int DefaultMax = 999;
    public const bool DefaultShuffle = true;

    public const int MinTillCount = 1;
    public const int MaxTillCount = 9;
    public const int MinNumber = 1;

    /// <summary>Четыре цифры — верх, за которым номер перестаёт читаться с
    /// талона на расстоянии. Не предел ленты: при 2× (см.
    /// EscPosPrinterService.BuildTicket) в строку помещается 16 символов, и
    /// 3 символа префикса плюс 4 цифры оставляют запас.</summary>
    public const int MaxNumber = 9999;

    public const int MaxPrefixLength = 3;

    public static QueueNumberOptions From(IQueueSettings settings) => new(
        settings.TillIndex, settings.TillCount, settings.QueueNumberMin, settings.QueueNumberMax,
        settings.QueueNumberShuffle, settings.QueueNumberPrefix, settings.QueueSecret);

    /// <summary>Сегодняшние константы — для тестов и для места, где
    /// IQueueSettings нет.</summary>
    public static QueueNumberOptions Default(int tillIndex, string secret) =>
        new(tillIndex, DefaultTillCount, DefaultMin, DefaultMax, DefaultShuffle, string.Empty, secret);

    /// <summary>Истинно, когда пул на диске, собранный кодом до этой
    /// настройки (он знал только константы), совпал бы с пулом от этих
    /// настроек. NumberPool по этому признаку усыновляет старый пул вместо
    /// пересборки посреди дня — см. его EnsurePoolAsync.
    ///
    /// TillIndex в проверке нарочно нет: старый пул никогда не хранил индекс
    /// кассы — его срез был неявным, определённым тем, какие номера лежали
    /// в файле, а не отдельным полем для сравнения. Касса, сменившая индекс
    /// посреди дня, получает то же поведение «применится при следующей
    /// пересборке», что было и раньше.</summary>
    public bool ShapesTheLegacyPool =>
        TillCount == DefaultTillCount && Min == DefaultMin && Max == DefaultMax && Shuffle == DefaultShuffle;

    // Клэмпы — здесь, а не в SettingsService: предпросмотр в настройках
    // обязан показывать то, что реально сохранится, и делить формулу на два
    // места значило бы, что экран однажды покажет одно, а талон напечатает
    // другое.

    public static int ClampTillCount(int raw) => Math.Clamp(raw, MinTillCount, MaxTillCount);

    public static int ClampMin(int raw) => Math.Clamp(raw, MinNumber, MaxNumber);

    // Второй аргумент клэмпится внутри, а не принимается как уже клэмпленный:
    // иначе Math.Clamp падает с ArgumentException, когда min > MaxNumber или
    // tillCount < 1, и функция незаметно требует вызова после ClampMin /
    // ClampTillCount. Сырое значение из settings.json не должно превращать
    // геттер в исключение — порядок вызовов не должен быть важен вовсе.
    public static int ClampMax(int raw, int min) => Math.Clamp(raw, ClampMin(min), MaxNumber);

    public static int ClampTillIndex(int raw, int tillCount) => Math.Clamp(raw, 0, ClampTillCount(tillCount) - 1);

    public static string NormalizePrefix(string? raw)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        return trimmed.Length <= MaxPrefixLength ? trimmed : trimmed[..MaxPrefixLength];
    }
}
