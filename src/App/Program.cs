using System;
using System.IO;
using System.Net;
using System.Windows;

namespace VusaLinkApp
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (s, e) =>
            {
                Log.Info("Unhandled: " + e.Exception);
                MessageBox.Show(e.Exception.Message, "vUSALink App error");
                e.Handled = true;
            };
            app.Run(new MainWindow(args.Length > 0 ? args[0] : null));
        }
    }
}
