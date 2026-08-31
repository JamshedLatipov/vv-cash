using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VvCash.Models;

/// <summary>Читает `Roles` так, чтобы опечатка в settings.json никогда не долетала
/// до исключения. `Roles` — первое строковое поле в этом файле, а Load() ловит
/// любой JsonException на весь SettingsData целиком и обнуляет его — не только
/// принтеры, но и BackendUrl, токены авторизации, всё. Одна кривая роль на одном
/// принтере не должна класть кассу без связи с бэкендом; она должна тихо
/// откатиться к Receipt — тому же значению, в которое мигрирует отсутствующее
/// поле — и оставить остальной файл как есть.
///
/// Список с одним плохим токеном — тоже опечатка: "Ticket, Bogus" откатывается
/// целиком к Receipt, а не режется до "Ticket". Наполовину применённая ошибка
/// хуже проигнорированной: касса напечатает то, что никто не выбирал.</summary>
public class PrintRoleJsonConverter : JsonConverter<PrintRole>
{
    private static readonly Dictionary<string, PrintRole> ByName = BuildNameMap();

    private static Dictionary<string, PrintRole> BuildNameMap()
    {
        var map = new Dictionary<string, PrintRole>(StringComparer.OrdinalIgnoreCase);
        foreach (PrintRole role in Enum.GetValues<PrintRole>())
        {
            map[role.ToString()] = role;
        }
        return map;
    }

    public override PrintRole Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            // A number, null, bool — this field was only ever meant to hold names
            // someone typed by hand. Fall back instead of throwing.
            return PrintRole.Receipt;
        }

        var text = reader.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return PrintRole.Receipt;
        }

        var result = PrintRole.None;
        foreach (var token in text.Split(','))
        {
            var name = token.Trim();
            if (name.Length == 0 || !ByName.TryGetValue(name, out var role))
            {
                return PrintRole.Receipt;
            }

            result |= role;
        }

        return result;
    }

    public override void Write(Utf8JsonWriter writer, PrintRole value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
