namespace StudioX.Engine;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using StudioX.Foundation;

/// <summary>将多映像的地址、用途和内容共同绑定到审批；单映像保留原有固件散列接口。</summary>
public static class DownloadImageLayout
{
    public static string ApprovalSha256(IReadOnlyList<DownloadImagePreview> images)
    {
        if (images.Count == 0) { throw new StudioXException("DOWNLOAD_IMAGES", "下载布局不能为空。"); }
        if (images.Any(image => image.Bytes <= 0 || image.Format is not ("bin" or "elf") ||
            image.Sha256.Length != 64 || !image.Sha256.All(Uri.IsHexDigit)))
        {
            throw new StudioXException("DOWNLOAD_IMAGES", "下载布局含无效映像。");
        }
        if (images.Count == 1) { return images[0].Sha256; }
        var text = new StringBuilder("StudioX-download-layout-1\n");
        foreach (var image in images.OrderBy(image => image.Address).ThenBy(image => image.Role, StringComparer.Ordinal))
        {
            // 字段分隔符不能来自工程文件，避免不同布局在串联时产生同一输入。
            if (image.RelativePath.Any(char.IsControl) || image.Role.Any(char.IsControl) ||
                image.Role is not ("application" or "pin-mapping"))
            {
                throw new StudioXException("DOWNLOAD_IMAGES", "下载布局含无效用途或路径。");
            }
            text.Append(image.Role).Append('\0').Append(image.RelativePath).Append('\0')
                .Append(image.Format).Append('\0').Append(image.Address.ToString("x16", CultureInfo.InvariantCulture)).Append('\0')
                .Append(image.Bytes.ToString(CultureInfo.InvariantCulture)).Append('\0')
                .Append(image.Sha256.ToUpperInvariant()).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
