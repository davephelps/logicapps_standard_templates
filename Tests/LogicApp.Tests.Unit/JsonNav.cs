using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace LogicApp.Tests.Unit
{
    internal static class JsonNav
    {
        public static bool TryProp(this JsonElement el, string name, out JsonElement value)
        {
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out value))
                return true;
            value = default;
            return false;
        }

        public static JsonElement? TryPath(this JsonElement el, params string[] segments)
        {
            var cur = el;
            foreach (var s in segments)
            {
                if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(s, out var next))
                    return null;
                cur = next;
            }
            return cur;
        }

        public static IEnumerable<JsonElement> DescendantsAndSelf(this JsonElement el)
        {
            yield return el;
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in el.EnumerateObject())
                        foreach (var d in p.Value.DescendantsAndSelf())
                            yield return d;
                    break;
                case JsonValueKind.Array:
                    foreach (var item in el.EnumerateArray())
                        foreach (var d in item.DescendantsAndSelf())
                            yield return d;
                    break;
            }
        }

        public static IEnumerable<string> AllStrings(this JsonElement el) =>
            el.DescendantsAndSelf()
              .Where(d => d.ValueKind == JsonValueKind.String)
              .Select(d => d.GetString()!)
              .Where(s => s != null);
    }
}
