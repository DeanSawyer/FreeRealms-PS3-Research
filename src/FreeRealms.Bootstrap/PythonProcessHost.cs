using System.Diagnostics;
using System.IO;

namespace FreeRealms.Bootstrap;

/// <summary>Runs the Python reference server without a console window.</summary>
public sealed class PythonProcessHost : IServerHost
{
    public event Action<string>? Log;
    private Process? process;
    private bool stopping;

    public async Task StartAsync(ServerSettings settings)
    {
        if (process is { HasExited: false })
            throw new InvalidOperationException("Bootstrap already running.");

        var script = Path.Combine(AppContext.BaseDirectory, "bootstrap", "fr_ps3_bootstrap.py");
        if (!File.Exists(script))
            throw new FileNotFoundException("Bundled bootstrap script missing.", script);

        // Resolve Python without launching a PowerShell or cmd.exe window.
        var candidates = new[] { "python.exe", "python", "py.exe" };
        Exception? last = null;
        foreach (var executable in candidates)
        {
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            if (executable == "py.exe") info.ArgumentList.Add("-3");
            info.ArgumentList.Add("-u");
            info.ArgumentList.Add(script);
            info.ArgumentList.Add("--client"); info.ArgumentList.Add(settings.Client);
            info.ArgumentList.Add("--bind"); info.ArgumentList.Add(settings.Bind);
            info.ArgumentList.Add("--advertise"); info.ArgumentList.Add(settings.Advertise);
            info.ArgumentList.Add("--port"); info.ArgumentList.Add(settings.Port.ToString());
            if (!settings.Dns) info.ArgumentList.Add("--no-dns");
            if (settings.MatchLocal) info.ArgumentList.Add("--match-local");

            Process? candidate = null;
            try
            {
                candidate = new Process { StartInfo = info, EnableRaisingEvents = true };
                if (!candidate.Start()) throw new InvalidOperationException("Python process failed to start.");
                process = candidate;
                stopping = false;
                candidate.OutputDataReceived += (_, e) => { if (e.Data is not null) Log?.Invoke(e.Data); };
                candidate.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log?.Invoke(e.Data); };
                candidate.Exited += (_, _) =>
                {
                    if (!stopping) Log?.Invoke($"Python bootstrap exited (code {candidate.ExitCode}).");
                };
                candidate.BeginOutputReadLine();
                candidate.BeginErrorReadLine();
                await Task.Delay(450);
                if (candidate.HasExited)
                    throw new InvalidOperationException($"Python bootstrap exited with code {candidate.ExitCode}. Review the console for details.");
                Log?.Invoke($"Python backend started (PID {candidate.Id}) without a separate console.");
                return;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                last = ex;
                candidate?.Dispose();
                process = null;
            }
            catch
            {
                if (candidate is { HasExited: false }) candidate.Kill(entireProcessTree: true);
                candidate?.Dispose();
                process = null;
                throw;
            }
        }
        throw new InvalidOperationException("Python 3 was not found. Install Python 3 and select 'Add Python to PATH', then restart the manager.", last);
    }

    public void Stop()
    {
        stopping = true;
        var active = process;
        process = null;
        if (active is null) return;
        try
        {
            if (!active.HasExited)
            {
                active.Kill(entireProcessTree: true);
                active.WaitForExit(3000);
            }
        }
        catch (Exception ex) { Log?.Invoke($"Stop warning: {ex.Message}"); }
        finally { active.Dispose(); Log?.Invoke("Python bootstrap stopped."); }
    }
}
