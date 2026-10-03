namespace FH6LocalCryptoTool;

/// <summary>Authenticated FM CMS cache bookkeeping, not a CMS gzip entry or
/// the General asset profile. Same slotted framing with separate cipher contexts.</summary>
public static class MotorsportCmsCacheContainer
{
    private static readonly Lazy<MotorsportGameDb.Cipher> Data = new(() => MotorsportGameDb.Cipher.Load("fm2023-cms-cache-data.json", true));
    private static readonly Lazy<MotorsportGameDb.Cipher> Mac = new(() => MotorsportGameDb.Cipher.Load("fm2023-cms-cache-mac.json", false));

    public static bool HasHeaderAuthentication(byte[] encrypted) => MotorsportAssetContainer.HasHeaderAuthentication(encrypted, Mac.Value);
    public static byte[] Decrypt(byte[] encrypted) => MotorsportAssetContainer.Decrypt(encrypted, Data.Value, Mac.Value);
    public static byte[] Encrypt(byte[] plaintext, byte[] template) => MotorsportAssetContainer.Encrypt(plaintext, template, Data.Value, Mac.Value);
}
