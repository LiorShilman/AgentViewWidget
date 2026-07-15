using System.Diagnostics;
using System.IO;
using System.Net.Sockets;

namespace AgentLiveWidget;

/// <summary>
/// Ensures the local state server is running, spawning it if the port is dead.
/// Best-effort: if it can't find/launch the server, the widget simply stays
/// "offline" and keeps retrying the WebSocket connection on its own.
/// </summary>
public sealed class ServerLauncher
{
    private const int Port = 4577;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan SpawnCooldown = TimeSpan.FromSeconds(10);

    private DateTime _lastSpawnAttempt = DateTime.MinValue;

    public async Task EnsureRunningAsync()
    {
        if (await IsPortOpenAsync()) return;

        if (DateTime.UtcNow - _lastSpawnAttempt < SpawnCooldown) return;
        _lastSpawnAttempt = DateTime.UtcNow;

        var serverDir = FindServerDirectory();
        if (serverDir is null) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "node",
                Arguments = "index.js",
                WorkingDirectory = serverDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch
        {
            // node missing or spawn failed — widget stays offline, no crash.
        }
    }

    private static async Task<bool> IsPortOpenAsync()
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync("127.0.0.1", Port);
            var completed = await Task.WhenAny(connectTask, Task.Delay(ProbeTimeout));
            return completed == connectTask && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Walks up from the executable looking for a sibling "server/index.js".</summary>
    private static string? FindServerDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "server", "index.js");
            if (File.Exists(candidate))
            {
                return Path.Combine(dir.FullName, "server");
            }
        }
        return null;
    }
}
