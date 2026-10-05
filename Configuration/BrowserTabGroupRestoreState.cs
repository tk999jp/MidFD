using System.Text.Json;
using System.Text.Json.Serialization;

namespace MidFD.Configuration;

/// <summary>Optional persisted data for one user-created Browser tab group.</summary>
public sealed class BrowserTabGroupRestoreState
{
    public string GroupId { get; set; } = string.Empty;
    public string CategoryId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool Expanded { get; set; } = true;

    // This is a membership set. Its order is never used to order tabs or groups.
    public List<string> MemberTabIds { get; set; } = new();

    public BrowserTabGroupRestoreState Clone() => new()
    {
        GroupId = GroupId,
        CategoryId = CategoryId,
        DisplayName = DisplayName,
        Expanded = Expanded,
        MemberTabIds = (MemberTabIds ?? new List<string>()).ToList()
    };
}

/// <summary>
/// Reads optional group records independently so a malformed group does not invalidate
/// the containing Browser workspace snapshot.
/// </summary>
public sealed class BrowserTabGroupRestoreStateListConverter : JsonConverter<List<BrowserTabGroupRestoreState>>
{
    public override bool HandleNull => true;

    public override List<BrowserTabGroupRestoreState> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return new List<BrowserTabGroupRestoreState>();

        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return new List<BrowserTabGroupRestoreState>();
        }

        var groups = new List<BrowserTabGroupRestoreState>();
        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object
                || !TryGetString(element, "groupId", out string groupId)
                || !Guid.TryParse(groupId, out Guid parsedGroupId)
                || parsedGroupId == Guid.Empty
                || !TryGetString(element, "categoryId", out string categoryId)
                || string.IsNullOrWhiteSpace(categoryId)
                || !TryGetString(element, "displayName", out string displayName)
                || string.IsNullOrWhiteSpace(displayName))
            {
                continue;
            }

            bool expanded = true;
            if (TryGetProperty(element, "expanded", out JsonElement expandedElement))
            {
                if (expandedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) continue;
                expanded = expandedElement.GetBoolean();
            }

            var memberTabIds = new List<string>();
            if (TryGetProperty(element, "memberTabIds", out JsonElement membersElement))
            {
                if (membersElement.ValueKind != JsonValueKind.Array) continue;
                foreach (JsonElement memberElement in membersElement.EnumerateArray())
                {
                    if (memberElement.ValueKind != JsonValueKind.String
                        || !Guid.TryParse(memberElement.GetString(), out Guid memberTabId)
                        || memberTabId == Guid.Empty)
                    {
                        continue;
                    }

                    memberTabIds.Add(memberTabId.ToString("D"));
                }
            }

            groups.Add(new BrowserTabGroupRestoreState
            {
                GroupId = parsedGroupId.ToString("D"),
                CategoryId = categoryId,
                DisplayName = displayName,
                Expanded = expanded,
                MemberTabIds = memberTabIds
            });
        }

        return groups;
    }

    public override void Write(
        Utf8JsonWriter writer,
        List<BrowserTabGroupRestoreState> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (BrowserTabGroupRestoreState group in (value ?? new List<BrowserTabGroupRestoreState>())
                     .Where(IsValid)
                     .OrderBy(static group => Guid.Parse(group.GroupId).ToString("N"), StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("groupId", Guid.Parse(group.GroupId).ToString("D"));
            writer.WriteString("categoryId", group.CategoryId);
            writer.WriteString("displayName", group.DisplayName);
            writer.WriteBoolean("expanded", group.Expanded);
            writer.WriteStartArray("memberTabIds");
            foreach (Guid memberTabId in (group.MemberTabIds ?? new List<string>())
                         .Select(static id => Guid.TryParse(id, out Guid parsed) ? parsed : Guid.Empty)
                         .Where(static id => id != Guid.Empty)
                         .Distinct()
                         .OrderBy(static id => id))
            {
                writer.WriteStringValue(memberTabId.ToString("D"));
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static bool IsValid(BrowserTabGroupRestoreState? group) =>
        group != null
        && Guid.TryParse(group.GroupId, out Guid groupId)
        && groupId != Guid.Empty
        && !string.IsNullOrWhiteSpace(group.CategoryId)
        && !string.IsNullOrWhiteSpace(group.DisplayName);

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!TryGetProperty(element, propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
