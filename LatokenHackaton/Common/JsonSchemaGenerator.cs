using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;

namespace LatokenHackaton.Common
{
    public static class JsonSchemaGenerator
    {
        private static readonly Dictionary<Type, object> SchemaCache = new Dictionary<Type, object>();
        private static readonly object CacheLock = new object();

        public static object GenerateSchema(Type type)
        {
            return GenerateTypeSchema(type, new HashSet<Type>());
        }

        private static object GenerateTypeSchema(Type type, HashSet<Type> visited)
        {
            if (TryGetFromCache(type, out var cachedSchema))
                return cachedSchema;

            if (visited.Contains(type))
            {
                var loopSchema = new { type = "object" };
                StoreInCache(type, loopSchema);
                return loopSchema;
            }

            visited.Add(type);

            if (IsSimpleType(type))
            {
                var simple = GetSimpleSchema(type);
                visited.Remove(type);
                StoreInCache(type, simple);
                return simple;
            }

            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            var schemaObj = new
            {
                type = "object",
                properties = props.ToDictionary(
                    prop => prop.Name,
                    prop => GeneratePropertySchema(prop.PropertyType, visited)
                ),
                required = props
                    .Where(IsPropertyRequired)
                    .Select(prop => prop.Name)
                    .ToArray(),
                additionalProperties = false
            };

            visited.Remove(type);
            StoreInCache(type, schemaObj);
            return schemaObj;
        }

        private static object GeneratePropertySchema(Type propertyType, HashSet<Type> visited)
        {
            if (TryGetFromCache(propertyType, out var cached))
                return cached;

            if (IsSimpleType(propertyType))
            {
                var simple = GetSimpleSchema(propertyType);
                StoreInCache(propertyType, simple);
                return simple;
            }

            if (typeof(IDictionary).IsAssignableFrom(propertyType))
            {
                var genArgs = propertyType.GetGenericArguments();
                var valueType = genArgs.Length > 1 ? genArgs[1] : typeof(object);
                var dictSchema = new
                {
                    type = "object",
                    additionalProperties = GenerateTypeSchema(valueType, visited)
                };
                StoreInCache(propertyType, dictSchema);
                return dictSchema;
            }

            if (propertyType.IsArray ||
               (typeof(IEnumerable).IsAssignableFrom(propertyType) && propertyType != typeof(string)))
            {
                var elementType = propertyType.IsArray
                    ? propertyType.GetElementType()
                    : propertyType.GenericTypeArguments.FirstOrDefault() ?? typeof(object);

                var arrSchema = new
                {
                    type = "array",
                    items = GenerateTypeSchema(elementType, visited)
                };
                StoreInCache(propertyType, arrSchema);
                return arrSchema;
            }

            return GenerateTypeSchema(propertyType, visited);
        }

        private static bool IsSimpleType(Type type)
        {
            return type.IsPrimitive
                   || type == typeof(decimal)
                   || type == typeof(string)
                   || type == typeof(DateTime)
                   || type.IsEnum;
        }

        private static object GetSimpleSchema(Type type)
        {
            if (type == typeof(string) || type == typeof(DateTime))
                return new { type = "string" };
            if (type.IsEnum)
                return new { type = "string", @enum = Enum.GetNames(type) };
            if (type == typeof(bool))
                return new { type = "boolean" };
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
                return new { type = "number" };
            if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte))
                return new { type = "integer" };
            return new { type = "object" };
        }

        private static bool IsPropertyRequired(PropertyInfo property)
        {
            return property.GetCustomAttributes(typeof(RequiredAttribute), true).Any();
        }

        private static bool TryGetFromCache(Type type, out object schema)
        {
            lock (CacheLock)
            {
                return SchemaCache.TryGetValue(type, out schema);
            }
        }

        private static void StoreInCache(Type type, object schema)
        {
            lock (CacheLock)
            {
                if (!SchemaCache.ContainsKey(type))
                    SchemaCache[type] = schema;
            }
        }
    }
}