using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;
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

            using var form = new MultipartFormDataContent { HeaderEncodingSelector = (_, _) => Encoding.UTF8 };
            foreach (var (name, value) in fields)
            {
                switch (value)
                {
                    case null:
                        break;
                    case FileUpload file:
                        var part = new ByteArrayContent(file.Content);
                        part.Headers.ContentType = MediaTypeHeaderValue.Parse(file.ContentType);
                        // Written by hand: the framework's own writer turns a non-ASCII name into an
                        // encoded word no server reads, and refuses a name with a quote in it.
                        part.Headers.TryAddWithoutValidation(
                            "Content-Disposition",
                            $"form-data; name=\"{Quote(name)}\"; filename=\"{Quote(file.FileName)}\"");
                        form.Add(part);
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

        /// <summary>A name inside a quoted header parameter, as browsers write it: <c>"</c> and line breaks percent-encoded.</summary>
        private static string Quote(string name) =>
            name.Replace("\"", "%22").Replace("\r", "%0D").Replace("\n", "%0A");

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
