using System.Windows.Forms;
using DeployManager.Tray;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);
Application.Run(new TrayApplicationContext());
