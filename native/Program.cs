using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Reflection;
using System.Threading;
using System.Security.Principal;

[assembly: AssemblyTitle("FlowLog Native")]
[assembly: AssemblyDescription("Windows native daily outliner")]
[assembly: AssemblyCompany("iChenwin")]
[assembly: AssemblyProduct("FlowLog Native")]
[assembly: AssemblyVersion("0.2.0.0")]
[assembly: AssemblyFileVersion("0.2.0.0")]

namespace FlowLog.Native
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            string identity = WindowsIdentity.GetCurrent().User.Value;
            using (Mutex instance = new Mutex(false, "Local\\FlowLogNative-App-" + identity))
            {
                bool owns;
                try { owns = instance.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
                if (!owns)
                {
                    MessageBox.Show("流记已经运行。请在现有窗口中选择笔记库。", "FlowLog Native", MessageBoxButton.OK, MessageBoxImage.Information);
                    return 2;
                }
                try { return Run(args); }
                finally { instance.ReleaseMutex(); }
            }
        }

        private static int Run(string[] args)
        {
            string root = null;
            try
            {
                for (int i = 0; i < args.Length; i++)
                    if (args[i] == "--vault" && i + 1 < args.Length) root = Path.GetFullPath(args[++i]);
            }
            catch (Exception ex) { MessageBox.Show("笔记库路径无效。\n" + ex.Message, "FlowLog Native"); return 1; }
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
            {
                MessageBox.Show("操作未完成，当前窗口将保留。\n" + e.Exception.Message,
                    "FlowLog Native", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            try { return app.Run(new MainWindow(root)); }
            catch (Exception ex)
            {
                MessageBox.Show("无法启动 FlowLog Native。\n" + ex.Message,
                    "FlowLog Native", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
    }
}
