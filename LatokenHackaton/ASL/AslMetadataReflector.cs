using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;

namespace LatokenHackaton.ASL
{
    internal static class AslMetadataReflector
    {
        private static readonly ConcurrentDictionary<Type, object> aslMetadataCache = new();
        private static readonly ConcurrentDictionary<(Type Type, string MethodName, Type[] Params), MethodInfo?> methodCache = new();
        private static readonly ConcurrentDictionary<(Type Type, string PropertyName), PropertyInfo?> propertyCache = new();
        private static readonly ConcurrentDictionary<(Type GenericType, Type[] GenericArgs), ConstructorInfo?> constructorCache = new();
        private static readonly ConcurrentDictionary<Type, Type[]?> interfaceCache = new();
        private static readonly ConcurrentDictionary<Type, string> aslTypeCache = new();

        public static object GenerateAslMetadata(Type type)
        {
            return aslMetadataCache.GetOrAdd(type, _ =>
            {
                var visitedTypes = new Dictionary<Type, string>();
                var objectDefinitions = new Dictionary<string, object>();
                var enumDefinitions = new Dictionary<string, List<string>>();
                var methodsDict = new Dictionary<string, object>();

                foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                                      .Where(x => x.DeclaringType != typeof(object)
                                                  && !x.IsDefined(typeof(AslIgnoreAttribute), false)
                                                  && x.Name != "ToString"))
                {
                    var (methodDesc, methodManualFormat) = getAslData(m);
                    var paramDict = new Dictionary<string, object>();
                    foreach (var p in m.GetParameters())
                    {
                        var (pDesc, pManualFormat) = getAslData(p);
                        var paramType = unwrapForAsl(p.ParameterType);
                        var typeLabel = getTypeLabel(paramType);
                        paramDict[p.Name] = buildMetadata(pm =>
                        {
                            pm["Type"] = typeLabel.Type;
                            if (!string.IsNullOrEmpty(pDesc)) pm["Description"] = pDesc;
                            if (!string.IsNullOrEmpty(pManualFormat)) pm["Format"] = pManualFormat;
                            else if (!string.IsNullOrEmpty(typeLabel.Format)) pm["Format"] = typeLabel.Format;
                        });
                    }
                    var retType = unwrapForAsl(m.ReturnType);
                    var retLabel = getTypeLabel(retType);
                    methodsDict[m.Name] = buildMetadata(md =>
                    {
                        if (!string.IsNullOrEmpty(methodDesc)) md["Description"] = methodDesc;
                        md["Parameters"] = paramDict;
                        if (retType != null)
                        {
                            if (!string.IsNullOrEmpty(methodManualFormat))
                            {
                                md["Return"] = buildMetadata(r =>
                                {
                                    r["Type"] = retLabel.Type;
                                    r["Format"] = methodManualFormat;
                                });
                            }
                            else if (string.IsNullOrEmpty(retLabel.Format))
                            {
                                md["Return"] = retLabel.Type;
                            }
                            else
                            {
                                md["Return"] = buildMetadata(r =>
                                {
                                    r["Type"] = retLabel.Type;
                                    r["Format"] = retLabel.Format;
                                });
                            }
                        }
                    });
                }

                var result = buildMetadata(root =>
                {
                    root["Methods"] = methodsDict;
                    if (objectDefinitions.Count > 0) root["Types"] = objectDefinitions;
                    if (enumDefinitions.Count > 0) root["Enums"] = enumDefinitions;
                });
                return result;

                (string Type, string? Format) getTypeLabel(Type t)
                {
                    if (t == null) return ("null", null);
                    var isNullable = false;
                    var underlying = Nullable.GetUnderlyingType(t);
                    if (underlying != null)
                    {
                        isNullable = true;
                        t = underlying;
                    }
                    var aslKind = ConvertToAslType(t);
                    if (t.IsEnum)
                    {
                        var name = getOrCreateEnumDefinition(t);
                        return isNullable ? ("string or null", "enum:" + name) : ("string", "enum:" + name);
                    }
                    if (t == typeof(DateTime) || t == typeof(DateTimeOffset))
                    {
                        return isNullable ? ("string or null", "yyyy-MM-ddTHH:mm:ssZ") : ("string", "yyyy-MM-ddTHH:mm:ssZ");
                    }
                    if (aslKind is "string")
                    {
                        return isNullable ? ("string or null", null) : ("string", null);
                    }
                    if (aslKind is "number")
                    {
                        return isNullable ? ("number or null", null) : ("number", null);
                    }
                    if (aslKind is "boolean")
                    {
                        return isNullable ? ("boolean or null", null) : ("boolean", null);
                    }
                    if (aslKind is "null")
                    {
                        return ("null", null);
                    }
                    if (aslKind == "array")
                    {
                        var elemType = unwrapForAsl(getElementType(t) ?? typeof(object));
                        var elemLabel = getTypeLabel(elemType);
                        var finalElemType = elemLabel.Type;
                        if (finalElemType.StartsWith("object as ")) finalElemType = finalElemType.Substring("object as ".Length);
                        return isNullable
                            ? ($"array of {finalElemType} or null", elemLabel.Format)
                            : ($"array of {finalElemType}", elemLabel.Format);
                    }
                    if (t == typeof(object))
                    {
                        return isNullable ? ("object or null", null) : ("object", null);
                    }
                    var objKey = getOrCreateObjectDefinition(t);
                    return isNullable ? ($"object as {objKey} or null", null) : ($"object as {objKey}", null);
                }

                string getOrCreateObjectDefinition(Type t)
                {
                    if (visitedTypes.TryGetValue(t, out var existingName)) return existingName;
                    var name = t.Name;
                    visitedTypes[t] = name;
                    objectDefinitions[name] = buildObjectDefinition(t);
                    return name;
                }

                string getOrCreateEnumDefinition(Type e)
                {
                    if (visitedTypes.TryGetValue(e, out var existingName)) return existingName;
                    var name = e.Name;
                    visitedTypes[e] = name;
                    enumDefinitions[name] = e.GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => f.Name).ToList();
                    return name;
                }

                object buildObjectDefinition(Type t)
                {
                    return buildMetadata(def =>
                    {
                        var (classDesc, classManualFormat) = getAslData(t);
                        if (!string.IsNullOrEmpty(classDesc)) def["Description"] = classDesc;
                        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                     .Where(p => p.GetIndexParameters().Length == 0 && !p.IsDefined(typeof(AslIgnoreAttribute), false))
                                     .ToDictionary(p => p.Name, p => (object)buildPropertyDefinition(p));
                        if (props.Count > 0) def["Properties"] = props;
                    });
                }

                Dictionary<string, object> buildPropertyDefinition(PropertyInfo p)
                {
                    var (propDesc, propManualFormat) = getAslData(p);
                    var propertyType = unwrapForAsl(p.PropertyType);
                    var label = getTypeLabel(propertyType);
                    var pd = buildMetadata(x =>
                    {
                        x["Type"] = label.Type;
                        if (!string.IsNullOrEmpty(propDesc)) x["Description"] = propDesc;
                        if (!string.IsNullOrEmpty(propManualFormat)) x["Format"] = propManualFormat;
                        else if (!string.IsNullOrEmpty(label.Format)) x["Format"] = label.Format;
                    });
                    return pd;
                }

                (string? Description, string? ManualFormat) getAslData(ICustomAttributeProvider member)
                {
                    var attr = member.GetCustomAttributes(typeof(AslDescriptionAttribute), false)
                                     .OfType<AslDescriptionAttribute>()
                                     .FirstOrDefault();
                    if (attr == null) return (null, null);
                    return (attr.Description, attr.Format);
                }

                Type? unwrapForAsl(Type? t)
                {
                    if (t == null) return t;
                    if (IsTaskType(t))
                    {
                        if (!t.IsGenericType) return null;
                        return t.GetGenericArguments().FirstOrDefault() ?? typeof(object);
                    }
                    if (IsAsyncEnumerableType(t))
                    {
                        var g = t.GetGenericArguments().FirstOrDefault();
                        return g != null ? g.MakeArrayType() : typeof(object[]);
                    }
                    return t;
                }

                Type getElementType(Type t)
                {
                    if (t.IsArray) return t.GetElementType();
                    var g = t.GetGenericArguments().FirstOrDefault();
                    return g ?? typeof(object);
                }

                Dictionary<string, object> buildMetadata(Action<Dictionary<string, object>> builder)
                {
                    var dict = new Dictionary<string, object>();
                    builder(dict);
                    return dict;
                }
            });
        }

        public static string ConvertToAslType(Type type)
        {
            if (type == null) return "object";
            if (type == typeof(void)) return "null";

            return aslTypeCache.GetOrAdd(type, type =>
            {
                if (IsNumericType(type)) return "number";
                if (type == typeof(bool) || type == typeof(bool?)) return "boolean";
                if (type == typeof(string) || type == typeof(char)
                    || type == typeof(DateTime) || type == typeof(DateTime?)
                    || type == typeof(Guid) || type == typeof(Guid?)
                    || type == typeof(TimeSpan) || type == typeof(TimeSpan?))
                    return "string";
                if (type.IsEnum) return "string";
                if (IsTaskType(type))
                {
                    if (type.IsGenericType)
                    {
                        var genArg = type.GetGenericArguments().Single();
                        if (genArg == null) return "null";
                        return ConvertToAslType(genArg);
                    }

                    return "null";
                }
                if (IsAsyncEnumerable(type) || IsAsyncEnumerableType(type)) return "array";
                if (IsIDictionaryType(type)) return "array";
                if (type.IsArray || IsCollectionType(type)) return "array";
                return "object";
            });
        }

        public static async Task<object?> ConvertFromAslTypeValueAsync(Type type, object? value)
        {
            if (value == null) return null;
            if (type == typeof(void)) return null;

            if (value is Task task)
            {
                await task.ConfigureAwait(false);
                var taskType = task.GetType();
                if (taskType.IsGenericType && taskType.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    var prop = GetPropertyCached(taskType, "Result");
                    return prop?.GetValue(task);
                }
                return null;
            }

            var asyncEnumInterface = GetCachedInterfaces(value.GetType())
                .FirstOrDefault(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>));
            if (asyncEnumInterface != null)
            {
                var list = await ConvertIAsyncEnumerableToList(value, asyncEnumInterface);
                return await ConvertFromAslTypeValueAsync(type, list);
            }

            if (type.IsAssignableFrom(value.GetType())) return value;
            if (type == typeof(string)) return value.ToString();

            if ((type == typeof(bool) || type == typeof(bool?)) && bool.TryParse(value.ToString(), out var b))
                return b;

            if (IsNumericType(type) && double.TryParse(value.ToString(), out var d))
                return ConvertToNumeric(type, d);

            if ((type == typeof(DateTime) || type == typeof(DateTime?)) && DateTime.TryParse(value.ToString(), out var dt))
                return dt;

            if ((type == typeof(Guid) || type == typeof(Guid?)) && Guid.TryParse(value.ToString(), out var g))
                return g;

            if (type.IsEnum)
            {
                try { return Enum.Parse(type, value.ToString()!, true); } catch { }
            }

            if (IsIEnumerableType(type) && value is IEnumerable en && value is not string)
                return BuildList(type, en);

            if (IsIDictionaryType(type) && value is IDictionary dic)
                return BuildDictionary(type, dic);

            return value;
        }

        private static bool IsNumericType(Type t)
        {
            if (t.IsPrimitive && t != typeof(bool) && t != typeof(char)) return true;
            if (t == typeof(decimal) || t == typeof(decimal?)) return true;
            return false;
        }

        private static object? ConvertToNumeric(Type t, double d)
        {
            if (t == typeof(int) || t == typeof(int?)) return (int)d;
            if (t == typeof(long) || t == typeof(long?)) return (long)d;
            if (t == typeof(float) || t == typeof(float?)) return (float)d;
            if (t == typeof(double) || t == typeof(double?)) return d;
            if (t == typeof(decimal) || t == typeof(decimal?)) return (decimal)d;
            if (t == typeof(short) || t == typeof(short?)) return (short)d;
            if (t == typeof(byte) || t == typeof(byte?)) return (byte)d;
            if (t == typeof(uint) || t == typeof(uint?)) return (uint)d;
            if (t == typeof(ulong) || t == typeof(ulong?)) return (ulong)d;
            if (t == typeof(ushort) || t == typeof(ushort?)) return (ushort)d;
            return null;
        }

        private static bool IsIEnumerableType(Type t)
        {
            if (t == typeof(string)) return false;
            return typeof(IEnumerable).IsAssignableFrom(t);
        }

        private static bool IsIDictionaryType(Type t)
        {
            return typeof(IDictionary).IsAssignableFrom(t);
        }

        private static bool IsCollectionType(Type t)
        {
            if (t == typeof(string)) return false;
            if (typeof(IEnumerable).IsAssignableFrom(t) && !typeof(IDictionary).IsAssignableFrom(t)) return true;
            return false;
        }

        private static bool IsAsyncEnumerable(Type t)
        {
            return t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>);
        }

        private static bool IsAsyncEnumerableType(Type t)
        {
            return GetCachedInterfaces(t).Any(IsAsyncEnumerable);
        }

        private static bool IsTaskType(Type t)
        {
            if (t == typeof(Task) || t == typeof(ValueTask)) return true;
            if (t.IsGenericType)
            {
                var genDef = t.GetGenericTypeDefinition();
                if (genDef == typeof(Task<>) || genDef == typeof(ValueTask<>)) return true;
            }
            return false;
        }

        private static object BuildList(Type listType, IEnumerable source)
        {
            var elementType = listType.IsArray ? listType.GetElementType() : listType.GetGenericArguments().FirstOrDefault() ?? typeof(object);
            var items = new List<object?>();
            foreach (var s in source)
                items.Add(s);
            if (listType.IsArray)
            {
                var arr = Array.CreateInstance(elementType!, items.Count);
                for (int i = 0; i < items.Count; i++)
                    arr.SetValue(items[i], i);
                return arr;
            }

            var constructedList = GetCachedConstructor(typeof(List<>), new[] { elementType! });
            var list = constructedList?.Invoke(null) as IList;
            if (list != null) foreach (var it in items) list.Add(it);
            return list!;
        }

        static object BuildDictionary(Type dictType, IDictionary source)
        {
            var args = dictType.GetGenericArguments();
            var keyType = args.Length > 0 ? args[0] : typeof(object);
            var valueType = args.Length > 1 ? args[1] : typeof(object);
            var concreteDict = GetCachedConstructor(typeof(Dictionary<,>), new[] { keyType, valueType });
            var dict = concreteDict?.Invoke(null) as IDictionary;
            if (dict == null) return source;
            foreach (DictionaryEntry kv in source)
                dict.Add(kv.Key, kv.Value);
            return dict;
        }

        private static async Task<IList> ConvertIAsyncEnumerableToList(object asyncEnumerable, Type iasyncInterface)
        {
            var elementType = iasyncInterface.GetGenericArguments()[0];
            var getEnumNoParam = GetMethodCached(iasyncInterface, "GetAsyncEnumerator", Type.EmptyTypes);
            var getEnumWithToken = GetMethodCached(iasyncInterface, "GetAsyncEnumerator", new[] { typeof(CancellationToken) });
            var enumerator = getEnumNoParam != null
                ? getEnumNoParam.Invoke(asyncEnumerable, null)
                : getEnumWithToken?.Invoke(asyncEnumerable, new object?[] { CancellationToken.None });
            if (enumerator == null) return new List<object?>();
            var targetInterface = enumerator.GetType().GetInterface(typeof(IAsyncEnumerator<>).Name);
            var moveNextAsync = GetMethodCached(targetInterface, "MoveNextAsync", Type.EmptyTypes);
            var currentProp = GetPropertyCached(targetInterface, "Current");
            var listType = GetCachedConstructor(typeof(List<>), new[] { elementType });
            var list = listType?.Invoke(null) as IList;
            if (list == null || moveNextAsync == null || currentProp == null) return new List<object?>();
            while (true)
            {
                var moveNextResult = moveNextAsync.Invoke(enumerator, null);
                bool hasNext = false;
                if (moveNextResult is Task<bool> tb) hasNext = await tb.ConfigureAwait(false);
                else if (moveNextResult is ValueTask<bool> vtb) hasNext = await vtb.ConfigureAwait(false);
                if (!hasNext) break;
                list.Add(currentProp.GetValue(enumerator));
            }

            return list;
        }

        private static MethodInfo? GetMethodCached(Type type, string methodName, Type[] paramTypes)
        {
            return methodCache.GetOrAdd((type, methodName, paramTypes), tuple =>
            {
                var (t, m, p) = tuple;
                return t.GetMethod(m, BindingFlags.Public | BindingFlags.Instance, null, p, null);
            });
        }

        private static PropertyInfo? GetPropertyCached(Type type, string propertyName)
        {
            return propertyCache.GetOrAdd((type, propertyName), tuple =>
            {
                var (t, p) = tuple;
                return t.GetProperty(p, BindingFlags.Public | BindingFlags.Instance);
            });
        }

        private static ConstructorInfo? GetCachedConstructor(Type genericType, Type[] genericArgs)
        {
            return constructorCache.GetOrAdd((genericType, genericArgs), tuple =>
            {
                var (gType, gArgs) = tuple;
                var constructedType = gType.MakeGenericType(gArgs);
                return constructedType.GetConstructor(Type.EmptyTypes);
            });
        }

        private static Type[]? GetCachedInterfaces(Type type)
        {
            return interfaceCache.GetOrAdd(type, t => t.GetInterfaces());
        }
    }
}