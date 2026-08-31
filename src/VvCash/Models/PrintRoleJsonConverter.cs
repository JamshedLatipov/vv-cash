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
            // A number, null, bool, array, object — this field was only ever meant
            // to hold names someone typed by hand, so anything else falls back.
            // Skip() is what makes that safe for [ ] and { } too: it consumes the
            // whole subtree (a no-op for a scalar token, already fully "read").
            // Without it the reader stays parked on the opening token, and
            // Deserialize<SettingsData> throws "read too much or not enough" —
            // which Load()'s catch-all turns into the exact whole-file reset this
            // converter exists to prevent.
            reader.Skip();
            return PrintRole.Receipt;
        }

        var result = PrintRole.None;
        var sawAnyName = false;
        foreach (var token in (reader.GetString() ?? string.Empty).Split(','))
        {
            var name = token.Trim();
            if (name.Length == 0)
            {
                // A stray or trailing comma. JsonStringEnumConverter tolerated one
                // ("Ticket, KitchenOrder,"), so a hand-edited file keeps its meaning.
                continue;
            }

            if (!ByName.TryGetValue(name, out var role))
            {
                return PrintRole.Receipt;
            }

            result |= role;
            sawAnyName = true;
        }

        // No recognised name anywhere in the list (empty string, whitespace, a
        // lone comma) is unparseable input like any other, not an empty-but-valid
        // setting — that's None, and it has to be spelled out to mean it.
        return sawAnyName ? result : PrintRole.Receipt;
    }

    public override void Write(Utf8JsonWriter writer, PrintRole value, JsonSerializerOptions options)
    {
        // Only ever fed one of the three named flags or a combination of them
        // today. If that ever stops being true, Enum.ToString() falls back to a
        // raw number for a Flags value with no name ("8"), and Read() above has
        // no numeric entries in ByName — the next load silently degrades it to
        // Receipt, same as any other value it does not recognise.
        writer.WriteStringValue(value.ToString());
    }
}
