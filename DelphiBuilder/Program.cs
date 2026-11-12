using System.Text;

namespace DelphiBuilder;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message,
            "Delphi 项目管理器", MessageBoxButtons.OK, MessageBoxIcon.Error);
        try
        {
            using var store = new AppStore(Path.Combine(AppContext.BaseDirectory, "delphi-builder.db"));
            Application.Run(new MainForm(store));
        }
        catch (Exception exception)
        {
            MessageBox.Show($"无法启动程序：{exception.Message}\n\n数据库存储在程序目录。请将程序放到当前用户可写的目录。",
                "Delphi 项目管理器", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
