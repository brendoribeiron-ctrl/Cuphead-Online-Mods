using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class CupheadOnlineLauncher
{
    static string configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CupheadOnline");
    static string gameDirFile = Path.Combine(configDir, "pasta-do-jogo.txt");
    static string lastIpFile = Path.Combine(configDir, "ultimo-ip.txt");
    static string gameDir;
    static string exe;

    [STAThread]
    static void Main()
    {
        try { SetProcessDPIAware(); } catch { }
        Application.EnableVisualStyles();
        if (!FindGame())
        {
            return;
        }

        var form = new Form
        {
            Text = "Cuphead Online",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(340, 256),
            Font = new Font("Segoe UI", 10f)
        };
        try { form.Icon = Icon.ExtractAssociatedIcon(exe); } catch { }

        var title = new Label { Text = "Cuphead Online", Font = new Font("Segoe UI", 16f, FontStyle.Bold), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(10, 8, 320, 36) };
        var host = new Button { Text = "Hospedar partida", Bounds = new Rectangle(20, 52, 300, 36) };
        var ipLabel = new Label { Text = "Codigo da sala ou IP do host:", Bounds = new Rectangle(20, 98, 300, 20) };
        var ip = new TextBox { Bounds = new Rectangle(20, 120, 190, 26) };
        var join = new Button { Text = "Entrar", Bounds = new Rectangle(220, 118, 100, 30) };
        var solo = new Button { Text = "Jogar sem online", Bounds = new Rectangle(20, 172, 300, 36) };
        var logs = new LinkLabel { Text = "Abrir pasta de logs (mande pro Claude se der erro)", AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(10, 222, 320, 22) };
        logs.LinkClicked += delegate { OpenLogs(); };

        try { if (File.Exists(lastIpFile)) ip.Text = File.ReadAllText(lastIpFile).Trim(); } catch { }

        host.Click += delegate { Launch("-host"); form.Close(); };
        solo.Click += delegate { Launch(""); form.Close(); };
        join.Click += delegate
        {
            string target = ip.Text.Trim();
            if (target.Length == 0)
            {
                MessageBox.Show("Digite o codigo da sala ou o IP do host.", "Cuphead Online", MessageBoxButtons.OK, MessageBoxIcon.Information);
                ip.Focus();
                return;
            }
            try { Directory.CreateDirectory(configDir); File.WriteAllText(lastIpFile, target); } catch { }
            Launch("-join " + target);
            form.Close();
        };
        form.AcceptButton = join;

        form.Controls.AddRange(new Control[] { title, host, ipLabel, ip, join, solo, logs });
        Application.Run(form);
    }

    // Procura o Cuphead.exe do lado do launcher, depois na pasta lembrada; se nao achar, pede pra escolher.
    static bool FindGame()
    {
        string saved = null;
        try { if (File.Exists(gameDirFile)) saved = File.ReadAllText(gameDirFile).Trim(); } catch { }
        string here = AppDomain.CurrentDomain.BaseDirectory;
        foreach (string dir in new[] { here, Path.Combine(here, "Cuphead"), saved })
        {
            if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "Cuphead.exe")))
            {
                SetGame(dir);
                return true;
            }
        }

        MessageBox.Show("Nao achei o Cuphead.exe do lado do launcher.\nNa proxima janela, escolha o Cuphead.exe do jogo com o mod.", "Cuphead Online", MessageBoxButtons.OK, MessageBoxIcon.Information);
        var pick = new OpenFileDialog { Title = "Escolha o Cuphead.exe", Filter = "Cuphead.exe|Cuphead.exe|Programas (*.exe)|*.exe", FileName = "Cuphead.exe" };
        if (pick.ShowDialog() != DialogResult.OK || !File.Exists(pick.FileName))
        {
            return false;
        }
        SetGame(Path.GetDirectoryName(pick.FileName));
        try { Directory.CreateDirectory(configDir); File.WriteAllText(gameDirFile, gameDir); } catch { }
        return true;
    }

    static string LogsDir { get { return Path.Combine(configDir, "logs"); } }

    static void OpenLogs()
    {
        try
        {
            Directory.CreateDirectory(LogsDir);
            Process.Start("explorer.exe", "\"" + LogsDir + "\"");
        }
        catch { }
    }

    static void SetGame(string dir)
    {
        gameDir = dir;
        exe = Path.Combine(dir, "Cuphead.exe");
    }

    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    // O launcher leva o Assembly-CSharp.dll do mod dentro dele e instala no jogo se estiver faltando
    // ou desatualizado, guardando o original uma vez como Assembly-CSharp.dll.original.
    static bool InstallMod()
    {
        string dll = Path.Combine(gameDir, @"Cuphead_Data\Managed\Assembly-CSharp.dll");
        byte[] mod;
        using (Stream s = typeof(CupheadOnlineLauncher).Assembly.GetManifestResourceStream("Assembly-CSharp.dll"))
        {
            if (s == null) return true;
            mod = new byte[s.Length];
            int read = 0;
            while (read < mod.Length) read += s.Read(mod, read, mod.Length - read);
        }
        try
        {
            if (File.Exists(dll) && SameBytes(File.ReadAllBytes(dll), mod)) return true;
            if (Process.GetProcessesByName("Cuphead").Length > 0)
            {
                MessageBox.Show("Feche o Cuphead antes, pra eu instalar o mod online.", "Cuphead Online", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            string backup = dll + ".original";
            if (File.Exists(dll) && !File.Exists(backup)) File.Copy(dll, backup);
            File.WriteAllBytes(dll, mod);
            return true;
        }
        catch (Exception e)
        {
            MessageBox.Show("Nao consegui instalar o mod em:\n" + dll + "\n\n" + e.Message, "Cuphead Online", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    static bool SameBytes(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    static void Launch(string args)
    {
        if (!InstallMod()) return;
        // Sempre em tela cheia na resolucao do monitor, mesmo que uma janela pequena tenha ficado salva.
        Rectangle screen = Screen.PrimaryScreen.Bounds;
        string display = string.Format("-screen-fullscreen 1 -screen-width {0} -screen-height {1} ", screen.Width, screen.Height);
        // Log do Unity junto com o log do online (o do online o proprio mod cria na mesma pasta)
        try
        {
            Directory.CreateDirectory(LogsDir);
            string[] old = Directory.GetFiles(LogsDir, "unity-*.log");
            Array.Sort(old);
            for (int i = 0; i < old.Length - 9; i++) File.Delete(old[i]);
            display +="-logFile \"" + Path.Combine(LogsDir, "unity-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".log") + "\" ";
        }
        catch { }
        Process.Start(new ProcessStartInfo(exe, display + args) { WorkingDirectory = gameDir, UseShellExecute = false });
    }
}
