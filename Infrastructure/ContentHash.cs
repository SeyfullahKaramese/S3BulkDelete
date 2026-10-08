using System.Security.Cryptography;
using System.Text;

namespace S3BulkDelete.Infrastructure;

public static class ContentHash
{
    public static string FromText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static async Task<string> FromStreamAsync(Stream stream, CancellationToken cancellationToken) =>
        Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
}
