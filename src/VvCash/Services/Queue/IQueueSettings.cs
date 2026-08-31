using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VvCash.Services.Queue;

[JsonConverter(typeof(QueueRoleJsonConverter))]
public enum QueueRole
{
    /// <summary>Очереди как сетевой системы на этой кассе нет. Печать талона и
    /// бегунка при этом работает — документы и сервер независимы.</summary>
    Off,
    Server,
    Client
}

/// <summary>Читает QueueRole так же осторожно, как PrintRoleJsonConverter читает
/// PrintRole (см. его докстринг): settings.json на точках правят руками, и
/// опечатка в одном поле не должна класть Deserialize&lt;SettingsData&gt; целиком —
/// Load() ловит любой JsonException на весь файл и обнуляет его: BackendUrl,
/// токены, все принтеры, не только роль очереди. JsonStringEnumConverter на
/// нераспознанной строке кидает исключение; этот конвертер вместо этого тихо
/// откатывается к Off — тому же безопасному значению, что и у поля, которого
/// в файле вовсе нет.</summary>
public class QueueRoleJsonConverter : JsonConverter<QueueRole>
{
    private static readonly Dictionary<string, QueueRole> ByName = BuildNameMap();

    private static Dictionary<string, QueueRole> BuildNameMap()
    {
        var map = new Dictionary<string, QueueRole>(StringComparer.OrdinalIgnoreCase);
        foreach (QueueRole role in Enum.GetValues<QueueRole>())
        {
            map[role.ToString()] = role;
        }
        return map;
    }

    public override QueueRole Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            // Число, null, bool, массив, объект — сюда всегда попадало только то,
            // что вписали руками. Skip() дочитывает поддерево (для скаляра — не
            // действие), иначе для [ ] и { } Deserialize<SettingsData> упадёт с
            // "read too much or not enough" — той самой ошибкой, от которой
            // этот конвертер должен уберечь.
            reader.Skip();
            return QueueRole.Off;
        }

        var name = reader.GetString();
        if (name != null && ByName.TryGetValue(name, out var role))
        {
            return role;
        }

        return QueueRole.Off;
    }

    public override void Write(Utf8JsonWriter writer, QueueRole value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}

/// <summary>Настройки очереди. Свой интерфейс, а не пять полей в
/// ISettingsService: тот реализуют полтора десятка тестовых заглушек, и каждое
/// новое свойство ломает их все, ничего не давая взамен.</summary>
public interface IQueueSettings
{
    QueueRole QueueRole { get; set; }

    /// <summary>Адрес кассы-сервера у клиента: «10.0.0.5:8770». Пусто у сервера
    /// и у выключенной очереди.</summary>
    string QueueServerAddress { get; set; }

    int QueuePort { get; set; }

    /// <summary>Общий секрет точки. Отсекает случайный планшет в гостевом
    /// Wi-Fi; криптографией не является и защитой от своих не считается.</summary>
    string QueueSecret { get; set; }

    /// <summary>Номер кассы 0..4 — он же её класс вычетов в пуле. Две кассы с
    /// одинаковым индексом начнут выдавать одинаковые номера, поэтому значение
    /// зажимается в диапазон, а не принимается как есть.</summary>
    int TillIndex { get; set; }
}
