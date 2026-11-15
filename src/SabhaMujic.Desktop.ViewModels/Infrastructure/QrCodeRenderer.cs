using QRCoder;

namespace SabhaMujic.Desktop.ViewModels.Infrastructure;

/// <summary>Renders QR codes as PNG bytes (cross-platform; WPF turns them into a BitmapImage).</summary>
public static class QrCodeRenderer
{
    public static byte[] RenderPng(string payload, int pixelsPerModule = 8)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data);
        return png.GetGraphic(pixelsPerModule);
    }
}
