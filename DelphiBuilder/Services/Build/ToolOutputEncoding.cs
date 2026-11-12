using System.Runtime.InteropServices;
using System.Text;

namespace DelphiBuilder;

internal static class ToolOutputEncoding
{
    static ToolOutputEncoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Encoding Get(string setting)
    {
        switch (setting.Trim().ToLowerInvariant())
        {
            case "utf8":
            case "utf-8":
                return new UTF8Encoding(false);
            case "cp936":
                return Encoding.GetEncoding(936);
            case "system":
                // A WinForms process normally has no console; OEMCP matches chcp's default.
                var codePage = GetConsoleOutputCP();
                if (codePage == 0)
                    codePage = GetOEMCP();
                if (codePage == 0)
                    codePage = GetACP();
                try
                {
                    return Encoding.GetEncoding(checked((int)codePage));
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidOperationException($"无法使用系统输出代码页 {codePage}，请在设置中选择 CP936 或 UTF-8。", exception);
                }
            default:
                throw new InvalidOperationException($"不支持的编译输出编码：{setting}。请选择 system、cp936 或 utf8。");
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleOutputCP();

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}
