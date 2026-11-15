using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace SabhaMujic.Server.Remote.Certificates;

/// <summary>
/// Builds the unsigned Apple configuration profile (<c>.mobileconfig</c>) that installs the local CA on an iPhone through
/// a single <c>com.apple.security.root</c> payload. Identifiers and UUIDs derive from the CA fingerprint, so downloading
/// the profile again replaces the installed one instead of adding a duplicate.
/// </summary>
public static class AppleProfile
{
    public const string ContentType = "application/x-apple-aspen-config";

    public static byte[] Build(LocalCertificateAuthority ca)
    {
        ArgumentNullException.ThrowIfNull(ca);
        var hash = SHA256.HashData(ca.CertificateDer);
        var shortId = Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        var identifier = "com.sabhamujic.localca." + shortId;
        var commonName = ca.Certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false);

        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true };
        using var buffer = new MemoryStream();
        using (var w = XmlWriter.Create(buffer, settings))
        {
            w.WriteStartDocument();
            w.WriteDocType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null);
            w.WriteStartElement("plist");
            w.WriteAttributeString("version", "1.0");
            w.WriteStartElement("dict");

            Key(w, "PayloadContent");
            w.WriteStartElement("array");
            w.WriteStartElement("dict");
            String(w, "PayloadCertificateFileName", "sabhamujic-ca.cer");
            Key(w, "PayloadContent");
            w.WriteElementString("data", Convert.ToBase64String(ca.CertificateDer));
            String(w, "PayloadDescription", "Lets this iPhone trust the SabhaMujic remote on your local network.");
            String(w, "PayloadDisplayName", commonName);
            String(w, "PayloadIdentifier", identifier + ".root");
            String(w, "PayloadType", "com.apple.security.root");
            String(w, "PayloadUUID", UuidFrom(hash, 1));
            Integer(w, "PayloadVersion", 1);
            w.WriteEndElement(); // dict
            w.WriteEndElement(); // array

            String(w, "PayloadDescription",
                $"Installs the SabhaMujic local certificate authority. SHA-256 fingerprint: {ca.Sha256Fingerprint}");
            String(w, "PayloadDisplayName", "SabhaMujic Remote");
            String(w, "PayloadIdentifier", identifier);
            String(w, "PayloadOrganization", "SabhaMujic");
            Key(w, "PayloadRemovalDisallowed");
            w.WriteStartElement("false");
            w.WriteEndElement();
            String(w, "PayloadType", "Configuration");
            String(w, "PayloadUUID", UuidFrom(hash, 0));
            Integer(w, "PayloadVersion", 1);

            w.WriteEndElement(); // dict
            w.WriteEndElement(); // plist
            w.WriteEndDocument();
        }

        return buffer.ToArray();
    }

    private static void Key(XmlWriter w, string key) => w.WriteElementString("key", key);

    private static void String(XmlWriter w, string key, string value)
    {
        Key(w, key);
        w.WriteElementString("string", value);
    }

    private static void Integer(XmlWriter w, string key, int value)
    {
        Key(w, key);
        w.WriteElementString("integer", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>A stable RFC 4122-shaped UUID derived from the CA hash (index 0 = profile, 1 = payload).</summary>
    private static string UuidFrom(byte[] hash, int index)
    {
        var bytes = SHA256.HashData([.. hash, (byte)index])[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true).ToString("D").ToUpperInvariant();
    }
}
