using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CdBurner.SetupBootstrapper;

/// <summary>
/// IgnisDisc-Setup.exe. Unpacks the embedded app (which contains the setup wizard) into a temporary folder, runs the
/// wizard from there with the same command-line arguments, waits for it, and removes the temporary folder.
/// Runs on the .NET Framework 4.8 that ships with Windows 10 (2004+) and 11, so it adds no runtime of its own.
/// </summary>
internal static class Program
{
    private const string PayloadResourceName = "IgnisDisc.Payload.zip";
    private const string WizardExeName = "Uninstall-IgnisDisc.exe"; // the wizard doubles as the installed uninstaller
    private const string DialogTitle = "IgnisDisc Optical Studio Setup";

    [STAThread]
    private static int Main(string[] args)
    {
        bool silent = args.Any(a =>
            a.Equals("--silent", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/S", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/quiet", StringComparison.OrdinalIgnoreCase));

        string tempDir = Path.Combine(Path.GetTempPath(), "IgnisDisc-Setup-" + Guid.NewGuid().ToString("N"));

        try
        {
            if (silent)
            {
                ExtractPayload(tempDir);
            }
            else
            {
                ExtractWithProgressWindow(tempDir);
            }

            string wizardPath = Path.Combine(tempDir, WizardExeName);
            if (!File.Exists(wizardPath))
            {
                throw new FileNotFoundException("The setup wizard is missing from the installer payload.", wizardPath);
            }

            var startInfo = new ProcessStartInfo(wizardPath, JoinArguments(args))
            {
                UseShellExecute = false,
                WorkingDirectory = tempDir
            };

            using (Process wizard = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the setup wizard."))
            {
                wizard.WaitForExit();
                return wizard.ExitCode;
            }
        }
        catch (Exception ex)
        {
            if (silent)
            {
                Console.Error.WriteLine("Setup failed: " + ex.Message);
            }
            else
            {
                MessageBox.Show("Setup could not start:\n\n" + ex.Message, DialogTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return 1;
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private static void ExtractWithProgressWindow(string tempDir)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Exception? failure = null;
        using (var form = new PreparingForm())
        {
            var worker = new Thread(() =>
            {
                try
                {
                    ExtractPayload(tempDir);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    form.BeginInvoke(new Action(form.Close));
                }
            })
            {
                IsBackground = true,
                Name = "IgnisDisc payload extraction"
            };

            form.Shown += (_, _) => worker.Start();
            Application.Run(form);
            worker.Join();
        }

        if (failure != null)
        {
            throw failure;
        }
    }

    /// <summary>
    /// Extracts the embedded payload into <paramref name="destination"/>, refusing entries that would escape it.
    /// </summary>
    internal static void ExtractPayload(string destination)
    {
        using (Stream? payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResourceName))
        {
            if (payload == null)
            {
                throw new FileNotFoundException("This installer was built without its payload (" + PayloadResourceName + ").");
            }

            ExtractZip(payload, destination);
        }
    }

    internal static void ExtractZip(Stream zipStream, string destination)
    {
        string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);

        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // zip-slip guard
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (Stream input = entry.Open())
                using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                {
                    input.CopyTo(output, 81920);
                }
            }
        }
    }

    /// <summary>
    /// Re-quotes arguments with the standard Windows command-line rules so the wizard receives them unchanged.
    /// </summary>
    internal static string JoinArguments(string[] args)
    {
        var sb = new StringBuilder();
        foreach (string arg in args)
        {
            if (sb.Length > 0) sb.Append(' ');

            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            {
                sb.Append(arg);
                continue;
            }

            sb.Append('"');
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    sb.Append('\\', (backslashes * 2) + 1);
                }
                else
                {
                    sb.Append('\\', backslashes);
                }

                backslashes = 0;
                sb.Append(c);
            }

            sb.Append('\\', backslashes * 2);
            sb.Append('"');
        }

        return sb.ToString();
    }

    private static void TryDeleteDirectory(string path)
    {
        for (int attempt = 0; attempt < 10 && Directory.Exists(path); attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(500); // antivirus scanners briefly hold freshly extracted files
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }
    }

    private sealed class PreparingForm : Form
    {
        public PreparingForm()
        {
            Text = DialogTitle;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(380, 96);
            BackColor = Color.FromArgb(18, 22, 31);
            ForeColor = Color.FromArgb(240, 244, 248);
            Font = new Font("Segoe UI", 9.75f);

            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
            }

            Controls.Add(new Label
            {
                Text = "Preparing IgnisDisc setup…",
                AutoSize = false,
                Location = new Point(20, 18),
                Size = new Size(340, 24)
            });

            Controls.Add(new ProgressBar
            {
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30,
                Location = new Point(20, 52),
                Size = new Size(340, 16)
            });
        }
    }
}
