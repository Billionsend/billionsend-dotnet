using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BillionSend.Internal
{
    /// <summary>Encodes a dictionary of fields and files as <c>multipart/form-data</c>.</summary>
    internal static class Multipart
    {
        /// <summary>
        /// A <see cref="FileUpload"/> is a file part; a string is sent as it is; a number or a boolean
        /// as its text; anything else (an object, a list, a <see cref="JsonNode"/>) as its JSON text,
        /// which is how the API takes structured fields in a form. A <see langword="null"/> is left out.
        /// </summary>
        public static (byte[] Body, string ContentType) Encode(object body, JsonSerializerOptions json)
        {
            if (body is not IEnumerable<KeyValuePair<string, object?>> fields)
            {
                throw new ArgumentException(
                    "A multipart body is a dictionary of form fields (string to value)",
                    nameof(body));
            }

            using var form = new MultipartFormDataContent();
            foreach (var (name, value) in fields)
            {
                switch (value)
                {
                    case null:
                        break;
                    case FileUpload file:
                        var part = new ByteArrayContent(file.Content);
                        part.Headers.ContentType = MediaTypeHeaderValue.Parse(file.ContentType);
                        form.Add(part, name, file.FileName);
                        break;
                    default:
                        form.Add(new StringContent(Text(value, json)), name);
                        break;
                }
            }

            // The boundary is in the content type: keep it with the bytes.
            var contentType = form.Headers.ContentType!.ToString();
            return (form.ReadAsByteArrayAsync().GetAwaiter().GetResult(), contentType);
        }

        private static string Text(object value, JsonSerializerOptions json) => value switch
        {
            string text => text,
            bool flag => flag ? "true" : "false",
            JsonValue { } node when node.TryGetValue<string>(out var text) => text,
            IFormattable number when value is not IEnumerable => number.ToString(null, CultureInfo.InvariantCulture),
            _ => JsonSerializer.Serialize(value, value.GetType(), json),
        };
    }
}
