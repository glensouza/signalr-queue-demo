using Net.Codecrete.QrCodeGenerator;

namespace SignalRQueueDemo.Shared;

/// <summary>
/// Server-side QR encoding shared by both frontends' "check in from your phone" call-to-action. It lives here in
/// Shared (not in ApiService) so Blazor's self-encapsulated CheckInQr component can encode in-process while the
/// ApiService /checkin/qr endpoint encodes for the Angular board — one encoder, no client-side QR dependency, and
/// nothing is ever fetched from an external QR web service (the court's no-external-calls constraint).
/// </summary>
public static class QrCodeHelper
{
    /// <summary>
    /// Generates an inline SVG string for a QR code from the given URL. Medium error correction tolerates a
    /// scuffed/curled printout while keeping the code compact; border 0 lets the caller's CSS own the quiet zone.
    /// </summary>
    public static string GenerateSvg(string url)
    {
        QrCode qr = QrCode.EncodeText(url, QrCode.Ecc.Medium);
        return qr.ToSvgString(0);
    }
}
