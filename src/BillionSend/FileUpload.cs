using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BillionSend
{
    /// <summary>
    /// A file sent as one part of a <c>multipart/form-data</c> request (for example the CSV of
    /// <c>Contacts.Imports.CreateAsync</c>). The bytes are kept in memory, so a retried call sends
    /// the same file again.
    /// </summary>
    public sealed class FileUpload
    {
        /// <summary>Creates a file part from its bytes.</summary>
        /// <param name="fileName">The file name the API sees.</param>
        /// <param name="content">The file's bytes.</param>
        /// <param name="contentType">The part's media type (default <c>text/csv</c>).</param>
        public FileUpload(string fileName, byte[] content, string contentType = "text/csv")
        {
            FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
            Content = content ?? throw new ArgumentNullException(nameof(content));
            ContentType = contentType ?? throw new ArgumentNullException(nameof(contentType));
        }

        /// <summary>The file name the API sees.</summary>
        public string FileName { get; }

        /// <summary>The file's bytes.</summary>
        public byte[] Content { get; }

        /// <summary>The part's media type.</summary>
        public string ContentType { get; }

        /// <summary>Reads a file from disk; its name is the file's own name.</summary>
        /// <param name="path">The file's path.</param>
        /// <param name="contentType">The part's media type (default <c>text/csv</c>).</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        public static async Task<FileUpload> FromFileAsync(
            string path,
            string contentType = "text/csv",
            CancellationToken cancellationToken = default) =>
            new(Path.GetFileName(path), await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), contentType);
    }
}
