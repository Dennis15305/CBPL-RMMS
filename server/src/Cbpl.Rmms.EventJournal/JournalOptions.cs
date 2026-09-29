using System.Net;
using Microsoft.Data.SqlClient;

namespace Cbpl.Rmms.EventJournal;

public sealed class JournalOptions
{
    public string ListenUrl { get; init; } = "http://127.0.0.1:5087";
    public string SqlConnectionString { get; init; } = "";
    public string[] AllowedClientIps { get; init; } = ["127.0.0.1", "::1"];

    public HashSet<IPAddress> Validate()
    {
        if (!Uri.TryCreate(ListenUrl, UriKind.Absolute, out Uri? url) ||
            url.Scheme is not ("http" or "https") ||
            url.Port is < 1 or > 65535 ||
            url.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(url.Query) ||
            !string.IsNullOrEmpty(url.Fragment))
        {
            throw new InvalidOperationException(
                "Journal.ListenUrl must be an absolute HTTP(S) origin with an explicit port.");
        }

        if (string.IsNullOrWhiteSpace(SqlConnectionString))
            throw new InvalidOperationException("Journal.SqlConnectionString is required.");

        var connection = new SqlConnectionStringBuilder(SqlConnectionString);
        if (!string.Equals(connection.InitialCatalog, "RMMS_Demo",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Journal.SqlConnectionString must target RMMS_Demo.");
        }

        if (AllowedClientIps.Length == 0)
            throw new InvalidOperationException("Journal.AllowedClientIps cannot be empty.");

        var allowed = new HashSet<IPAddress>();
        foreach (string value in AllowedClientIps)
        {
            if (!IPAddress.TryParse(value, out IPAddress? address))
                throw new InvalidOperationException($"Invalid allowed client IP: {value}");
            allowed.Add(Normalize(address));
        }

        return allowed;
    }

    public static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
