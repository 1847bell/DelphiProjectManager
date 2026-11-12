using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DelphiBuilder;

internal static class VersionResource
{
    internal sealed record Info(ushort[] FileVersion, ushort[] ProductVersion, ushort Language,
        uint FileType, IReadOnlyDictionary<string, string> Strings);
    private static readonly Regex NumericVersion = new(@"^[0-9]+(?:\.[0-9]+){0,3}$", RegexOptions.Compiled);

    public static Info? GetInfo(ProjectEvaluation evaluation)
    {
        string Property(string name, string fallback = "") => evaluation.Properties.TryGetValue(name, out var value)
            ? BuildPaths.Expand(value, evaluation.Properties).Trim() : fallback;
        var enabled = Property("VerInfo_IncludeVerInfo");
        if (enabled.Length == 0 || BuildPaths.Boolean(enabled) == false)
            return null;
        if (BuildPaths.Boolean(enabled) is null)
            throw new InvalidOperationException($"不支持的版本信息开关：VerInfo_IncludeVerInfo={enabled}");
        var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Property("VerInfo_Keys").Split(';'))
        {
            var separator = entry.IndexOf('=');
            if (separator > 0 && entry[..separator].Trim() is { Length: > 0 } name)
                strings[name] = entry[(separator + 1)..];
        }
        var components = string.Join('.', Property("VerInfo_MajorVer", "1"), Property("VerInfo_MinorVer", "0"),
            Property("VerInfo_Release", "0"), Property("VerInfo_Build", "0"));
        var fileString = strings.GetValueOrDefault("FileVersion", "");
        var productString = strings.GetValueOrDefault("ProductVersion", "");
        var fileVersion = ParseVersion(NumericVersion.IsMatch(fileString.Trim()) ? fileString : components, "FileVersion");
        var productVersion = NumericVersion.IsMatch(productString.Trim()) ? ParseVersion(productString, "ProductVersion") : fileVersion;
        strings.Remove("FileVersion");
        strings.Remove("ProductVersion");
        strings["FileVersion"] = fileString.Length == 0 ? string.Join(".", fileVersion) : fileString;
        strings["ProductVersion"] = productString.Length == 0 ? string.Join(".", productVersion) : productString;
        var language = ParseWord(Property("VerInfo_Locale", "1033"), "VerInfo_Locale", true);
        var library = Property("AppType").Equals("library", StringComparison.OrdinalIgnoreCase)
            || Property("DCC_OutputType").Equals("library", StringComparison.OrdinalIgnoreCase)
            || BuildPaths.Boolean(Property("GenDll")) == true
            || Path.GetExtension(evaluation.MainSource).Equals(".dpk", StringComparison.OrdinalIgnoreCase);
        return new Info(fileVersion, productVersion, language, library ? 2u : 1u, strings);
    }

    public static async Task UpdateFileAsync(string path, Info info, CancellationToken cancellationToken)
    {
        var original = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var updated = Update(original, info);
        if (original.AsSpan().SequenceEqual(updated))
            return;
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, updated, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static byte[] Update(ReadOnlySpan<byte> content, Info info)
    {
        var versionRecord = CreateVersionRecord(info);
        using var result = new MemoryStream(checked(content.Length + versionRecord.Length + 32));
        if (content.Length == 0)
            result.Write(ProjectResources.EmptyHeader);
        var offset = 0;
        while (offset < content.Length)
        {
            if (content.Length - offset < 28)
                throw InvalidResource();
            var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(content[offset..]);
            var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(content[(offset + 4)..]);
            var end = offset + (long)headerSize + dataSize;
            if (headerSize < 28 || headerSize % 4 != 0 || end > content.Length)
                throw InvalidResource();
            var headerEnd = offset + (int)headerSize;
            var cursor = SkipIdentifier(content, offset + 8, headerEnd);
            cursor = SkipIdentifier(content, cursor, headerEnd);
            if (Align4(cursor) + 16 > headerEnd)
                throw InvalidResource();
            var next = (int)Math.Min((end + 3) & ~3L, content.Length);
            var isVersion = BinaryPrimitives.ReadUInt16LittleEndian(content[(offset + 8)..]) == 0xffff
                && BinaryPrimitives.ReadUInt16LittleEndian(content[(offset + 10)..]) == 16;
            if (!isVersion)
            {
                result.Write(content[offset..next]);
                while (result.Length % 4 != 0)
                    result.WriteByte(0);
            }
            offset = next;
        }
        result.Write(versionRecord);
        return result.ToArray();
    }

    private static int SkipIdentifier(ReadOnlySpan<byte> content, int offset, int headerEnd)
    {
        if (offset + 2 > headerEnd)
            throw InvalidResource();
        if (BinaryPrimitives.ReadUInt16LittleEndian(content[offset..]) == 0xffff)
        {
            if (offset + 4 > headerEnd)
                throw InvalidResource();
            return offset + 4;
        }
        while (offset + 2 <= headerEnd)
        {
            var word = BinaryPrimitives.ReadUInt16LittleEndian(content[offset..]);
            offset += 2;
            if (word == 0)
                return offset;
        }
        throw InvalidResource();
    }

    private static byte[] CreateVersionRecord(Info info)
    {
        Span<uint> words = stackalloc uint[]
        {
            0xfeef04bd, 0x00010000, VersionDword(info.FileVersion[0], info.FileVersion[1]),
            VersionDword(info.FileVersion[2], info.FileVersion[3]), VersionDword(info.ProductVersion[0], info.ProductVersion[1]),
            VersionDword(info.ProductVersion[2], info.ProductVersion[3]), 0x3f, 0, 0x00040004, info.FileType, 0, 0, 0
        };
        var fixedInfo = new byte[52];
        for (var index = 0; index < words.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(index * 4), words[index]);
        var translation = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(translation, info.Language);
        BinaryPrimitives.WriteUInt16LittleEndian(translation.AsSpan(2), 1200);
        var strings = new List<byte[]>(info.Strings.Count);
        foreach (var (name, value) in info.Strings)
            strings.Add(Block(name, Utf16(value), [], true));
        var table = info.Language.ToString("x4", CultureInfo.InvariantCulture) + "04b0";
        var stringInfo = Block("StringFileInfo", [], [Block(table, [], strings, true)], true);
        var variableInfo = Block("VarFileInfo", [], [Block("Translation", translation, [])], true);
        var data = Block("VS_VERSION_INFO", fixedInfo, [stringInfo, variableInfo]);
        var record = new byte[Align4(32 + data.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), 32);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(8), 0xffff);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(10), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(12), 0xffff);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20), 0x0030);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(22), info.Language);
        data.CopyTo(record, 32);
        return record;
    }

    private static byte[] Block(string key, byte[] value, IReadOnlyList<byte[]> children, bool text = false)
    {
        var keyLength = Encoding.Unicode.GetByteCount(key) + 2;
        var valueOffset = Align4(6 + keyLength);
        var childOffset = Align4(valueOffset + value.Length);
        var length = children.Count == 0 ? valueOffset + value.Length : childOffset;
        foreach (var child in children)
            length = checked(length + Align4(child.Length));
        if (length > ushort.MaxValue)
            throw new InvalidOperationException("Windows 版本资源过大，单个版本块不能超过 65535 字节。");
        var block = new byte[length];
        BinaryPrimitives.WriteUInt16LittleEndian(block, (ushort)length);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(2), (ushort)(text ? value.Length / 2 : value.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(4), text ? (ushort)1 : (ushort)0);
        Encoding.Unicode.GetBytes(key.AsSpan(), block.AsSpan(6, keyLength - 2));
        value.CopyTo(block, valueOffset);
        foreach (var child in children)
        {
            child.CopyTo(block, childOffset);
            childOffset += Align4(child.Length);
        }
        return block;
    }

    private static byte[] Utf16(string value)
    {
        var bytes = new byte[Encoding.Unicode.GetByteCount(value) + 2];
        Encoding.Unicode.GetBytes(value.AsSpan(), bytes.AsSpan(0, bytes.Length - 2));
        return bytes;
    }

    private static ushort[] ParseVersion(string value, string property)
    {
        value = value.Trim();
        if (!NumericVersion.IsMatch(value))
            throw new InvalidOperationException($"{property} 的版本号无效：{value}。必须为 1–4 段数字，每段范围为 0–65535。");
        var parts = value.Split('.');
        var result = new ushort[4];
        for (var index = 0; index < parts.Length; index++)
            result[index] = ParseWord(parts[index], property);
        return result;
    }

    private static ushort ParseWord(string value, string property, bool allowHex = false)
    {
        value = value.Trim();
        var style = NumberStyles.None;
        var digits = value;
        if (allowHex && value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            style = NumberStyles.AllowHexSpecifier;
            digits = value[2..];
        }
        if (!ushort.TryParse(digits, style, CultureInfo.InvariantCulture, out var result))
            throw new InvalidOperationException($"{property} 的值无效：{value}。必须是 0–65535 范围内的整数。");
        return result;
    }

    private static uint VersionDword(ushort high, ushort low) => ((uint)high << 16) | low;
    private static int Align4(int value) => checked((value + 3) & ~3);
    private static InvalidDataException InvalidResource() => new("项目 .res 文件格式无效；已停止编译，原始资源文件未被修改。");
}
