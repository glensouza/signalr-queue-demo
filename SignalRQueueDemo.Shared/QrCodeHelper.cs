using Net.Codecrete.QrCodeGenerator;

namespace SignalRQueueDemo.Shared;

public static class QrCodeHelper
{
    /// <summary>
    /// Generates an inline SVG string for a QR code from the given URL.
    /// </summary>
    public static string GenerateSvg(string url)
    {
        var qr = QrCode.EncodeText(url, QrCode.Ecc.Medium);
        return qr.ToSvgString(0);
    }
}
