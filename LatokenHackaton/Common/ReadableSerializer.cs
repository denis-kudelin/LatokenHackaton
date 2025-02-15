using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;

namespace LatokenHackaton.Common
{
    internal static class ReadableSerializer
    {
        [AttributeUsage(AttributeTargets.Property)]
        public sealed class IgnoreAttribute : Attribute
        {
        }

        private static readonly ConcurrentDictionary<Type, PropertyInfo[]> cache = new();

        public static string Serialize(object? o)
        {
            var sb = new StringBuilder();
            Traverse(o, 0, new HashSet<object>(), sb);
            return sb.ToString();
        }

        private static void Traverse(object? x, int level, HashSet<object> seen, StringBuilder sb)
        {
            if (x == null)
            {
                sb.AppendLine(Indent(level) + "null");
                return;
            }
            var t = x.GetType();
            t = Nullable.GetUnderlyingType(t) ?? t;
            if (IsSimpleType(t))
            {
                sb.AppendLine(Indent(level) + FormatSimpleValue(x, t));
                return;
            }
            if (!t.IsValueType)
            {
                if (seen.Contains(x))
                {
                    sb.AppendLine(Indent(level) + "∞");
                    return;
                }
                seen.Add(x);
            }
            if (x is IDictionary dict)
            {
                if (IsDictionarySimple(dict))
                {
                    PrintDictionarySimple(dict, level, sb);
                }
                else
                {
                    foreach (var key in dict.Keys)
                    {
                        sb.AppendLine(Indent(level) + KeyAsString(key) + ":");
                        Traverse(dict[key], level + 1, seen, sb);
                    }
                }
                return;
            }
            if (x is IEnumerable en)
            {
                if (TryGetHomogeneousSimpleProps(en, out var props, out var items, out var itemType))
                {
                    PrintEnumerableAsTable(props, items, itemType!, level, sb);
                }
                else
                {
                    foreach (var el in en)
                    {
                        sb.AppendLine(Indent(level) + "-");
                        Traverse(el, level + 1, seen, sb);
                    }
                }
                return;
            }
            var objProps = cache.GetOrAdd(t, y => y.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0).ToArray());
            foreach (var p in objProps)
            {
                if (!p.CanRead) continue;
                if (p.IsDefined(typeof(IgnoreAttribute), true)) continue;
                object? val;
                try
                {
                    val = p.GetValue(x, null);
                }
                catch
                {
                    sb.AppendLine(Indent(level) + p.Name + ":");
                    sb.AppendLine(Indent(level + 1) + "error");
                    continue;
                }
                if (val == null) continue;
                if (val is string s && s == "") continue;
                sb.AppendLine(Indent(level) + p.Name + ":");
                Traverse(val, level + 1, seen, sb);
            }
        }

        private static bool IsSimpleType(Type t)
        {
            if (t.IsPrimitive || t.IsEnum) return true;
            if (t == typeof(string)) return true;
            if (t == typeof(decimal)) return true;
            if (t == typeof(DateTime)) return true;
            if (t == typeof(DateTimeOffset)) return true;
            if (t == typeof(TimeSpan)) return true;
            return false;
        }

        private static bool IsDictionarySimple(IDictionary dict)
        {
            foreach (var key in dict.Keys)
            {
                if (key == null) continue;
                var kt = Nullable.GetUnderlyingType(key.GetType()) ?? key.GetType();
                if (!IsSimpleType(kt)) return false;
            }
            foreach (var key in dict.Keys)
            {
                var val = dict[key];
                if (val == null) continue;
                var vt = Nullable.GetUnderlyingType(val.GetType()) ?? val.GetType();
                if (!IsSimpleType(vt)) return false;
            }
            return true;
        }

        private static void PrintDictionarySimple(IDictionary dict, int level, StringBuilder sb)
        {
            foreach (var key in dict.Keys)
            {
                var val = dict[key];
                var keyStr = KeyAsString(key);
                if (val == null)
                {
                    sb.AppendLine(Indent(level) + keyStr + ": null");
                    continue;
                }
                var vt = Nullable.GetUnderlyingType(val.GetType()) ?? val.GetType();
                if (IsSimpleType(vt))
                {
                    sb.AppendLine(Indent(level) + keyStr + ": " + FormatSimpleValue(val, vt));
                }
                else
                {
                    sb.AppendLine(Indent(level) + keyStr + ":");
                    Traverse(val, level + 1, new HashSet<object>(), sb);
                }
            }
        }

        private static bool TryGetHomogeneousSimpleProps(
            IEnumerable en,
            out List<PropertyInfo> props,
            out List<object> items,
            out Type? itemType
        )
        {
            props = new List<PropertyInfo>();
            items = new List<object>();
            itemType = null;
            foreach (var el in en)
            {
                if (el == null) continue;
                var currentT = Nullable.GetUnderlyingType(el.GetType()) ?? el.GetType();
                if (itemType == null) itemType = currentT;
                else if (currentT != itemType)
                {
                    props.Clear();
                    return false;
                }
                items.Add(el);
            }
            if (itemType == null) return false;
            if (IsSimpleType(itemType)) return false;
            var candidateProps = cache.GetOrAdd(itemType, y => y.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0).ToArray());
            candidateProps = candidateProps.Where(p => p.CanRead && !p.IsDefined(typeof(IgnoreAttribute), true)).ToArray();
            foreach (var p in candidateProps)
            {
                var pt = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                if (!IsSimpleType(pt))
                {
                    props.Clear();
                    return false;
                }
            }
            props.AddRange(candidateProps);
            return props.Count > 0;
        }

        private static void PrintEnumerableAsTable(
            List<PropertyInfo> props,
            List<object> items,
            Type itemType,
            int level,
            StringBuilder sb
        )
        {
            var colNames = props.Select(p => p.Name).ToArray();
            sb.AppendLine(Indent(level) + string.Join("  ", colNames));
            foreach (var item in items)
            {
                if (item == null) continue;
                if (item.GetType() != itemType) continue;
                var rowValues = new string[props.Count];
                for (int i = 0; i < props.Count; i++)
                {
                    var p = props[i];
                    object? val;
                    try
                    {
                        val = p.GetValue(item);
                    }
                    catch
                    {
                        val = "error";
                    }
                    if (val == null) rowValues[i] = "null";
                    else
                    {
                        var vt = Nullable.GetUnderlyingType(val.GetType()) ?? val.GetType();
                        rowValues[i] = FormatSimpleValue(val, vt);
                    }
                }
                sb.AppendLine(Indent(level) + string.Join("  ", rowValues));
            }
        }

        private static string FormatSimpleValue(object? x, Type t)
        {
            if (x == null) return "null";
            if (t == typeof(string))
            {
                var s = x.ToString()!;
                if (s.Contains('\n'))
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("---");
                    sb.AppendLine(s);
                    sb.Append("---");
                    return sb.ToString();
                }
                return s;
            }
            if (t == typeof(DateTime)) return ((DateTime)x).ToString("yyyy-MM-dd HH:mm:ssK");
            if (t == typeof(DateTimeOffset)) return ((DateTimeOffset)x).ToString("yyyy-MM-dd HH:mm:ssK");
            if (t == typeof(TimeSpan)) return x.ToString()!;
            return x.ToString()!;
        }

        private static string KeyAsString(object? k)
        {
            if (k == null) return "null";
            var t = Nullable.GetUnderlyingType(k.GetType()) ?? k.GetType();
            if (IsSimpleType(t)) return FormatSimpleValue(k, t);
            return t.Name;
        }

        private static string Indent(int level)
        {
            return new string('\t', level);
        }
    }
}

