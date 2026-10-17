using System.Collections.Frozen;
using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace Maple.Classic.Repacker.Engine.Models;

/// <summary>
/// Reads <see cref="DisplayAttribute"/> metadata (<see cref="DisplayAttribute.Name"/> and
/// <see cref="DisplayAttribute.ShortName"/>) from enum members. Lookups are reflected once per enum type and cached.
/// Members without a <see cref="DisplayAttribute"/> (or undefined numeric values) fall back to <see cref="Enum.ToString()"/>.
/// </summary>
public static class EnumDisplayExtensions {
    public static string GetDisplayName<TEnum>(this TEnum value) where TEnum : struct, Enum =>
        DisplayCache<TEnum>.Names.TryGetValue(value, out string? name) ? name : value.ToString();

    public static string GetShortName<TEnum>(this TEnum value) where TEnum : struct, Enum =>
        DisplayCache<TEnum>.ShortNames.TryGetValue(value, out string? name) ? name : value.ToString();

    private static class DisplayCache<TEnum> where TEnum : struct, Enum {
        public static readonly FrozenDictionary<TEnum, string> Names = Build(static d => d.GetName());
        public static readonly FrozenDictionary<TEnum, string> ShortNames = Build(static d => d.GetShortName());

        private static FrozenDictionary<TEnum, string> Build(Func<DisplayAttribute, string?> selector) {
            var map = new Dictionary<TEnum, string>();
            foreach (FieldInfo field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static)) {
                string? text = field.GetCustomAttribute<DisplayAttribute>() is { } display ? selector(display) : null;
                if (!string.IsNullOrEmpty(text)) {
                    map.TryAdd((TEnum) field.GetValue(null)!, text);
                }
            }
            return map.ToFrozenDictionary();
        }
    }
}
