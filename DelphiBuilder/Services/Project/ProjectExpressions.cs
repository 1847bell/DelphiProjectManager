using System.Text.RegularExpressions;

namespace DelphiBuilder;

internal static class ProjectExpressions
{
    private static readonly Regex PropertyReference = new(@"\$\(([^)]+)\)", RegexOptions.CultureInvariant);
    public readonly record struct Expansion(string Value, IReadOnlyList<string> Unresolved);

    public static Expansion Expand(string input, IReadOnlyDictionary<string, string> properties, bool undefinedAsEmpty = true)
    {
        var value = input;
        HashSet<string>? seen = null;
        HashSet<string>? unresolved = null;
        for (var depth = 0; depth < 32 && PropertyReference.IsMatch(value); depth++)
        {
            seen ??= new HashSet<string>(StringComparer.Ordinal);
            if (!seen.Add(value)) break;
            value = PropertyReference.Replace(value, match =>
            {
                var name = match.Groups[1].Value.Trim();
                if (properties.TryGetValue(name, out var replacement)) return replacement;
                (unresolved ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(name);
                return undefinedAsEmpty ? "" : match.Value;
            });
        }
        foreach (Match match in PropertyReference.Matches(value))
            (unresolved ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(match.Groups[1].Value.Trim());
        if (undefinedAsEmpty && PropertyReference.IsMatch(value)) value = PropertyReference.Replace(value, "");
        return new Expansion(value, unresolved?.ToArray() ?? []);
    }

    public static bool HasUnsupportedExpression(string value) => value.Contains("$([", StringComparison.Ordinal)
        || value.Contains("@(", StringComparison.Ordinal) || value.Contains("%(", StringComparison.Ordinal);

    public static bool Condition(string? condition, IReadOnlyDictionary<string, string> properties, ISet<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(condition)) return true;
        try
        {
            if (HasUnsupportedExpression(condition)) throw new FormatException("不支持属性函数、项列表或项元数据");
            return new ConditionParser(Expand(condition, properties).Value).Parse();
        }
        catch (FormatException ex)
        {
            warnings.Add($"无法求值条件，已跳过对应设置：{condition}（{ex.Message}）。仅支持属性引用、字符串比较 == / !=、And / Or 和括号，不执行 MSBuild 函数。");
            return false;
        }
    }

    private enum TokenKind { Value, Equal, NotEqual, And, Or, Left, Right, End }
    private readonly record struct Token(TokenKind Kind, string Value);

    private sealed class ConditionParser(string input)
    {
        private int offset;
        private Token current;

        public bool Parse()
        {
            Advance();
            var result = ParseOr();
            if (current.Kind != TokenKind.End) throw new FormatException($"不支持的标记 {current.Value}");
            return result;
        }

        private bool ParseOr()
        {
            var result = ParseAnd();
            while (current.Kind == TokenKind.Or)
            {
                Advance();
                var right = ParseAnd();
                result |= right;
            }
            return result;
        }

        private bool ParseAnd()
        {
            var result = ParsePrimary();
            while (current.Kind == TokenKind.And)
            {
                Advance();
                var right = ParsePrimary();
                result &= right;
            }
            return result;
        }

        private bool ParsePrimary()
        {
            if (current.Kind == TokenKind.Left)
            {
                Advance();
                var result = ParseOr();
                if (current.Kind != TokenKind.Right) throw new FormatException("缺少右括号");
                Advance();
                return result;
            }
            var left = Operand();
            if (current.Kind is TokenKind.Equal or TokenKind.NotEqual)
            {
                var operation = current.Kind;
                Advance();
                var equal = string.Equals(left, Operand(), StringComparison.OrdinalIgnoreCase);
                return operation == TokenKind.Equal ? equal : !equal;
            }
            return left.Length > 0 && !left.Equals("false", StringComparison.OrdinalIgnoreCase) && left != "0";
        }

        private string Operand()
        {
            if (current.Kind != TokenKind.Value) throw new FormatException($"缺少比较值：{current.Value}");
            var value = current.Value;
            Advance();
            return value;
        }

        private void Advance()
        {
            while (offset < input.Length && char.IsWhiteSpace(input[offset])) offset++;
            if (offset == input.Length) { current = new Token(TokenKind.End, ""); return; }
            var start = offset;
            var character = input[offset++];
            if (character == '(') { current = new Token(TokenKind.Left, "("); return; }
            if (character == ')') { current = new Token(TokenKind.Right, ")"); return; }
            if (character is '=' or '!')
            {
                if (offset == input.Length || input[offset++] != '=') throw new FormatException("只支持 == 和 != 比较");
                current = new Token(character == '=' ? TokenKind.Equal : TokenKind.NotEqual, input[start..offset]);
                return;
            }
            if (character is '\'' or '"')
            {
                var valueStart = offset;
                System.Text.StringBuilder? value = null;
                while (offset < input.Length)
                {
                    if (input[offset++] != character) continue;
                    if (offset < input.Length && input[offset] == character)
                    {
                        value ??= new System.Text.StringBuilder();
                        value.Append(input.AsSpan(valueStart, offset - valueStart));
                        offset++;
                        valueStart = offset;
                        continue;
                    }
                    current = new Token(TokenKind.Value, value == null ? input[valueStart..(offset - 1)]
                        : value.Append(input.AsSpan(valueStart, offset - valueStart - 1)).ToString());
                    return;
                }
                throw new FormatException("字符串引号未闭合");
            }
            while (offset < input.Length && !char.IsWhiteSpace(input[offset]) && input[offset] is not '(' and not ')' and not '=' and not '!') offset++;
            var word = input[start..offset];
            current = new Token(word.Equals("and", StringComparison.OrdinalIgnoreCase) ? TokenKind.And
                : word.Equals("or", StringComparison.OrdinalIgnoreCase) ? TokenKind.Or : TokenKind.Value, word);
        }
    }
}
