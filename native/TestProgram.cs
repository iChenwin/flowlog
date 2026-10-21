using System;
using System.IO;
namespace FlowLog.Native
{
    public static class TestProgram
    {
        [STAThread]
        public static int Main()
        {
            try
            {
                int storage = StorageTests.Run();
                Console.WriteLine("PASS native storage: " + storage + " cases");
                int editor = EditorTests.Run();
                Console.WriteLine("PASS native document editor: " + editor + " cases");
                int window = MainWindowTests.Run();
                Console.WriteLine("PASS native window integration: " + window + " cases");
                Console.WriteLine("PASS total: " + (storage + editor + window));
                string results = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "TestResults"));
                Directory.CreateDirectory(results);
                File.WriteAllText(Path.Combine(results, "native-tests.json"), "{\"passed\":true,\"storage\":" + storage + ",\"editor\":" + editor + ",\"window\":" + window + ",\"total\":" + (storage + editor + window) + ",\"method\":\"STA .NET Framework real WPF controls and isolated Markdown fixtures\"}");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
