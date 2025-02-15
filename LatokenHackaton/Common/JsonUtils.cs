using System;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LatokenHackaton.Common
{
    internal static class JsonUtils
    {
        public static string SerializeDefault(object value)
        {
            var result = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            return result;
        }

        public static T DeserializeDefault<T>(string value)
        {
            try
            {
                value = RemoveTrailingCommas(value);
                var options = new JsonSerializerOptions();
                options.Converters.Add(new ObjectAsDynamicConverter());
                var result = JsonSerializer.Deserialize<T>(value, options);
                return result;
            }
            catch (Exception ex)
            {
                throw;
            }
        }

        public static T DeserializeFirstJson<T>(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                throw new ArgumentException("Input text cannot be null or empty.", nameof(text));
            }

            var startIndex = -1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '{' || text[i] == '[')
                {
                    startIndex = i;
                    break;
                }
            }

            if (startIndex == -1)
            {
                throw new InvalidOperationException("No JSON object or array found in the text.");
            }

            var endIndex = -1;
            var stack = new Stack<char>();
            var inString = false;
            var escapeChar = false;

            for (int i = startIndex; i < text.Length; i++)
            {
                char c = text[i];

                if (inString)
                {
                    if (escapeChar)
                    {
                        escapeChar = false;
                    }
                    else if (c == '\\')
                    {
                        escapeChar = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }
                }
                else
                {
                    if (c == '"')
                    {
                        inString = true;
                    }
                    else if (c == '{' || c == '[')
                    {
                        stack.Push(c);
                    }
                    else if (c == '}' || c == ']')
                    {
                        if (stack.Count == 0)
                        {
                            throw new InvalidOperationException("Unmatched closing brace/bracket in JSON.");
                        }
                        char opening = stack.Pop();
                        if ((opening == '{' && c != '}') || (opening == '[' && c != ']'))
                        {
                            throw new InvalidOperationException("Mismatched braces/brackets in JSON.");
                        }

                        if (stack.Count == 0)
                        {
                            endIndex = i;
                            break;
                        }
                    }
                }
            }

            if (endIndex == -1)
            {
                throw new InvalidOperationException("Incomplete JSON object or array in text.");
            }

            var jsonString = text.Substring(startIndex, endIndex - startIndex + 1);

            try
            {
                jsonString = RemoveTrailingCommas(jsonString);
                var result = JsonSerializer.Deserialize<T>(jsonString);
                return result;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to deserialize JSON.", ex);
            }
        }

        public static string RemoveEmptyValues(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return json;

            var rootNode = JsonNode.Parse(json);

            CleanNode(rootNode);

            return rootNode.ToJsonString(new JsonSerializerOptions { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }

        public static string RemoveCodeBlocks(string markdownText)
        {
            var markdownBlockPattern = @"(^\s*```[^\n]*\n)|(^\s*~~~[^\n]*\n)|(\n\s*```\s*$)|(\n\s*~~~\s*$)";
            var cleanedText = Regex.Replace(markdownText, markdownBlockPattern, string.Empty, RegexOptions.Multiline);
            return cleanedText;
        }

        private static void CleanNode(JsonNode node)
        {
            if (node is JsonObject jsonObject)
            {
                var keysToRemove = new List<string>();

                foreach (var property in jsonObject)
                {
                    if (property.Value is JsonArray jsonArray && jsonArray.Count == 0)
                    {
                        keysToRemove.Add(property.Key);
                    }
                    else if (property.Value is JsonObject || property.Value is JsonArray)
                    {
                        CleanNode(property.Value);
                        if (property.Value is JsonObject nestedObject && nestedObject.Count == 0)
                        {
                            keysToRemove.Add(property.Key);
                        }
                        else if (property.Value is JsonArray nestedArray && nestedArray.Count == 0)
                        {
                            keysToRemove.Add(property.Key);
                        }
                    }
                    else if (property.Value == null || (property.Value is JsonValue jsonValue && jsonValue.ToString() == string.Empty))
                    {
                        keysToRemove.Add(property.Key);
                    }
                }

                foreach (var key in keysToRemove)
                {
                    jsonObject.Remove(key);
                }
            }
            else if (node is JsonArray jsonArray)
            {
                foreach (var item in jsonArray)
                {
                    CleanNode(item);
                }
            }
        }

        public static string[] SplitJsonArrayByMaxLength(string jsonArray, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(jsonArray) || maxLength <= 0)
                return null;

            var jsonDocument = JsonDocument.Parse(jsonArray);
            var result = new List<string>();
            var currentBatch = new List<JsonElement>();
            var currentLength = 0;

            foreach (var element in jsonDocument.RootElement.EnumerateArray())
            {
                var jsonString = SerializeDefault(element);
                int elementLength = jsonString.Length;

                // Check if adding this element would exceed the maxLength
                if (currentLength + elementLength > maxLength)
                {
                    // Serialize the current batch and add it to the result
                    result.Add(SerializeDefault(currentBatch));
                    currentBatch.Clear();
                    currentLength = 0;
                }

                // Add the element to the current batch
                currentBatch.Add(element);
                currentLength += elementLength;
            }

            // Add the last batch if it's not empty
            if (currentBatch.Count > 0)
            {
                result.Add(SerializeDefault(currentBatch));
            }

            return result.ToArray();
        }

        public static string RemoveTrailingCommas(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return json;

            int depth = 0;
            bool inString = false;
            bool escape = false;
            var sb = new StringBuilder();

            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];

                if (inString)
                {
                    sb.Append(c);
                    if (escape)
                    {
                        escape = false;
                    }
                    else if (c == '\\')
                    {
                        escape = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }
                }
                else
                {
                    if (c == '"')
                    {
                        inString = true;
                        sb.Append(c);
                    }
                    else if (c == '{' || c == '[')
                    {
                        depth++;
                        sb.Append(c);
                    }
                    else if (c == '}' || c == ']')
                    {
                        depth--;
                        if (sb.Length > 0 && sb[sb.Length - 1] == ',')
                        {
                            // Remove the trailing comma before a closing brace/bracket
                            sb.Length--;
                        }
                        sb.Append(c);
                    }
                    else if (c == ',')
                    {
                        // Check if the next non-whitespace character is a closing brace/bracket
                        int j = i + 1;
                        while (j < json.Length && char.IsWhiteSpace(json[j]))
                        {
                            j++;
                        }
                        if (j < json.Length && (json[j] == '}' || json[j] == ']'))
                        {
                            // Skip the comma
                            continue;
                        }
                        else
                        {
                            sb.Append(c);
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
            }

            return sb.ToString();
        }

        private class ObjectAsDynamicConverter : JsonConverter<object>
        {
            public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.String:
                        return reader.GetString();

                    case JsonTokenType.Number:
                        if (reader.TryGetInt64(out var l))
                            return l;
                        if (reader.TryGetDouble(out var d))
                            return d;
                        return reader.GetDecimal();

                    case JsonTokenType.True:
                        return true;

                    case JsonTokenType.False:
                        return false;

                    case JsonTokenType.Null:
                        return null;

                    case JsonTokenType.StartObject:
                        var dict = new Dictionary<string, object>();
                        while (reader.Read())
                        {
                            if (reader.TokenType == JsonTokenType.EndObject)
                                break;

                            if (reader.TokenType != JsonTokenType.PropertyName)
                                throw new JsonException("Expected PropertyName token");

                            var propertyName = reader.GetString();
                            reader.Read();
                            dict[propertyName] = Read(ref reader, typeof(object), options);
                        }
                        return dict;

                    case JsonTokenType.StartArray:
                        var list = new List<object>();
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        {
                            list.Add(Read(ref reader, typeof(object), options));
                        }
                        return list;

                    default:
                        throw new JsonException($"Unhandled token type: {reader.TokenType}");
                }
            }

            public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options) => throw new NotImplementedException();
        }
    }
}