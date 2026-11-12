using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DelphiBuilder;

internal sealed class ProjectDocument
{
    internal sealed class ElementSpan
    {
        public required int Start { get; init; }
        public required int OpenEnd { get; init; }
        public required string QualifiedName { get; init; }
        public int CloseStart { get; set; }
        public int End { get; set; }
        public bool Empty { get; init; }
    }

    internal readonly record struct TextChange(int Start, int Length, string Text);

    private readonly byte[] originalBytes;
    private readonly Encoding encoding;
    private readonly byte[] preamble;
    private readonly Dictionary<XElement, ElementSpan> spans = [];

    public string FilePath { get; }
    public string Text { get; }
    public string Newline { get; }
    public XDocument Xml { get; }
    public XElement Root { get; }
    public XNamespace Namespace => Root.Name.Namespace;

    private ProjectDocument(string filePath, byte[] bytes, string? decodedText = null,
        Encoding? originalEncoding = null, byte[]? originalPreamble = null)
    {
        FilePath = filePath;
        originalBytes = bytes;
        (encoding, preamble) = originalEncoding == null ? DetectEncoding(bytes) : (originalEncoding, originalPreamble!);
        try
        {
            Text = decodedText ?? encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("项目文件不符合声明的字符编码；为避免损坏文件，未读取或修改。", ex);
        }
        Newline = Regex.Match(Text, @"\r\n|\n|\r").Value;
        if (Newline.Length == 0) Newline = Environment.NewLine;
        try
        {
            using var reader = XmlReader.Create(new StringReader(Text), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
            Xml = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"项目 XML 无效（第 {ex.LineNumber} 行）：{ex.Message}", ex);
        }
        Root = Xml.Root ?? throw new InvalidDataException("项目文件缺少 Project 根元素。");
        if (Root.Name.LocalName != "Project")
            throw new InvalidDataException("项目文件的根元素必须是 Project。");
        MapSpans();
    }

    public static ProjectDocument Read(string filePath) => new(Path.GetFullPath(filePath), File.ReadAllBytes(filePath));

    public ProjectDocument WithText(string text) => new(FilePath, originalBytes, text, encoding, preamble);

    public IEnumerable<XElement> Children(XElement parent, string name) => parent.Elements(Namespace + name);
    public ElementSpan Span(XElement element) => spans[element];
    public string InnerXml(XElement element)
    {
        var span = Span(element);
        return span.Empty ? "" : Text[span.OpenEnd..span.CloseStart];
    }

    public string Prefix(XElement element)
    {
        var name = Span(element).QualifiedName;
        var colon = name.IndexOf(':');
        return colon < 0 ? "" : name[..(colon + 1)];
    }

    public TextChange RemoveElement(XElement element)
    {
        var span = Span(element);
        var start = span.Start;
        var end = span.End;
        var lineStart = LineStart(start);
        if (IsIndent(Text.AsSpan(lineStart, start - lineStart)))
        {
            var after = end;
            while (after < Text.Length && Text[after] is ' ' or '\t') after++;
            if (after == Text.Length || Text[after] is '\r' or '\n')
            {
                start = lineStart;
                if (after < Text.Length && Text[after] == '\r') after++;
                if (after < Text.Length && Text[after] == '\n') after++;
                end = after;
            }
        }
        return new TextChange(start, end - start, "");
    }

    public string Apply(IEnumerable<TextChange> changes)
    {
        var ordered = changes.OrderByDescending(change => change.Start).ThenByDescending(change => change.Length).ToArray();
        var result = new StringBuilder(Text);
        var previousStart = Text.Length;
        foreach (var change in ordered)
        {
            if (change.Start < 0 || change.Length < 0 || change.Start + change.Length > previousStart)
                throw new InvalidDataException("项目修改范围重叠，已取消保存。");
            result.Remove(change.Start, change.Length).Insert(change.Start, change.Text);
            previousStart = change.Start;
        }
        return result.ToString();
    }

    public int LineStart(int offset)
    {
        while (offset > 0 && Text[offset - 1] is not '\r' and not '\n') offset--;
        return offset;
    }

    public string Indent(XElement element)
    {
        var start = Span(element).Start;
        var lineStart = LineStart(start);
        return IsIndent(Text.AsSpan(lineStart, start - lineStart)) ? Text[lineStart..start] : "";
    }

    public static bool IsIndent(ReadOnlySpan<char> text)
    {
        foreach (var value in text)
            if (value is not ' ' and not '\t') return false;
        return true;
    }

    public void SaveAtomically(string text)
    {
        if (text == Text) return;
        byte[] body;
        try { body = encoding.GetBytes(text); }
        catch (EncoderFallbackException ex)
        {
            throw new InvalidDataException("新内容无法用项目原有编码保存；未修改项目文件。", ex);
        }
        var temporary = Path.Combine(Path.GetDirectoryName(FilePath)!, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                stream.Write(preamble);
                stream.Write(body);
                stream.Flush(true);
            }
            if (!File.ReadAllBytes(FilePath).AsSpan().SequenceEqual(originalBytes))
                throw new IOException("项目文件已被其他程序修改；已取消保存，请重新加载后再试。");
            File.Replace(temporary, FilePath, null);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static (Encoding Encoding, byte[] Preamble) DetectEncoding(byte[] bytes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding selected;
        var bomLength = 0;
        if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0, 0, 254, 255]))
        { selected = new UTF32Encoding(true, false, true); bomLength = 4; }
        else if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[255, 254, 0, 0]))
        { selected = new UTF32Encoding(false, false, true); bomLength = 4; }
        else if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[239, 187, 191]))
        { selected = new UTF8Encoding(false, true); bomLength = 3; }
        else if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[254, 255]))
        { selected = new UnicodeEncoding(true, false, true); bomLength = 2; }
        else if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[255, 254]))
        { selected = new UnicodeEncoding(false, false, true); bomLength = 2; }
        else if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == '<')
            selected = new UTF32Encoding(true, false, true);
        else if (bytes.Length >= 4 && bytes[0] == '<' && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0)
            selected = new UTF32Encoding(false, false, true);
        else if (bytes.Length >= 2 && bytes[0] == 0 && bytes[1] == '<')
            selected = new UnicodeEncoding(true, false, true);
        else if (bytes.Length >= 2 && bytes[0] == '<' && bytes[1] == 0)
            selected = new UnicodeEncoding(false, false, true);
        else
        {
            var declaration = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 1024));
            var match = Regex.Match(declaration, "^\\s*<\\?xml\\b[^?]*\\bencoding\\s*=\\s*['\"]([^'\"]+)['\"]", RegexOptions.IgnoreCase);
            try
            {
                selected = match.Success
                    ? Encoding.GetEncoding(match.Groups[1].Value, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                    : new UTF8Encoding(false, true);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException($"不支持项目声明的编码：{match.Groups[1].Value}。", ex);
            }
        }
        return (selected, bytes[..bomLength]);
    }

    private void MapSpans()
    {
        using var elements = Root.DescendantsAndSelf().GetEnumerator();
        var stack = new Stack<ElementSpan>();
        var position = 0;
        while ((position = Text.IndexOf('<', position)) >= 0)
        {
            if (Text.AsSpan(position).StartsWith("<!--"))
            { position = EndOf("-->", position + 4); continue; }
            if (Text.AsSpan(position).StartsWith("<![CDATA["))
            { position = EndOf("]]>", position + 9); continue; }
            if (Text.AsSpan(position).StartsWith("<?"))
            { position = EndOf("?>", position + 2); continue; }
            if (Text.AsSpan(position).StartsWith("<!"))
                throw new InvalidDataException("不支持包含 DTD 或自定义 XML 实体的项目文件。");
            var end = position + 1;
            var quote = '\0';
            for (; end < Text.Length; end++)
            {
                var character = Text[end];
                if (quote != '\0') { if (character == quote) quote = '\0'; }
                else if (character is '\'' or '"') quote = character;
                else if (character == '>') break;
            }
            if (Text[position + 1] == '/')
            {
                var open = stack.Pop();
                open.CloseStart = position;
                open.End = end + 1;
            }
            else
            {
                if (!elements.MoveNext()) throw new InvalidDataException("无法定位项目 XML 元素。");
                var nameEnd = position + 1;
                while (nameEnd < end && !char.IsWhiteSpace(Text[nameEnd]) && Text[nameEnd] != '/') nameEnd++;
                var empty = Text[end - 1] == '/';
                var span = new ElementSpan
                {
                    Start = position, OpenEnd = end + 1, CloseStart = end + 1, End = end + 1,
                    Empty = empty, QualifiedName = Text[(position + 1)..nameEnd]
                };
                spans.Add(elements.Current, span);
                if (!empty) stack.Push(span);
            }
            position = end + 1;
        }
    }

    private int EndOf(string marker, int start)
    {
        var index = Text.IndexOf(marker, start, StringComparison.Ordinal);
        if (index < 0) throw new InvalidDataException("项目 XML 标记不完整。");
        return index + marker.Length;
    }
}
