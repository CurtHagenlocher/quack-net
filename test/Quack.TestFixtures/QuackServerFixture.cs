using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Quack.TestFixtures;

// Spawns a DuckDB CLI in interactive mode, loads the quack extension, and
// starts a `quack_serve` listener on a random loopback port. The process is
// kept alive for the lifetime of the fixture; DisposeAsync sends `quack_stop`
// and `.exit` to shut things down cleanly. Shared between Quack.IntegrationTests
// and Quack.Adbc.Tests so we don't pay the duckdb startup cost twice or risk
// the two fixtures drifting in setup details.
public sealed class QuackServerFixture : IAsyncLifetime
{
    public const string DefaultDuckDbExePath = @"C:\src\duckdb\duckdb.exe";

    public string DuckDbExePath { get; init; } = DefaultDuckDbExePath;
    public string Token { get; private set; } = string.Empty;
    public int Port { get; private set; }
    // 127.0.0.1 (vs "localhost") avoids OS getaddrinfo deciding between IPv4
    // and IPv6 differently on the duckdb side and the test client side.
    public string QuackUri => $"quack:127.0.0.1:{Port}";

    private Process? _process;
    private readonly StringBuilder _capturedStdout = new();
    private readonly StringBuilder _capturedStderr = new();

    public string CapturedStdout => _capturedStdout.ToString();
    public string CapturedStderr => _capturedStderr.ToString();

    public async Task InitializeAsync()
    {
        if (!File.Exists(DuckDbExePath))
        {
            throw new FileNotFoundException(
                $"DuckDB executable not found at '{DuckDbExePath}'. Integration tests require a local v1.5+ build with the quack extension available.",
                DuckDbExePath);
        }

        Port = PickFreeLoopbackPort();
        Token = "test-token-" + Guid.NewGuid().ToString("N");

        ProcessStartInfo psi = new()
        {
            FileName = DuckDbExePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // -interactive forces interactive I/O even with redirected stdio,
        // so duckdb processes our commands as they arrive instead of
        // buffering everything until EOF.
        psi.ArgumentList.Add("-interactive");
        psi.ArgumentList.Add(":memory:");

        _process = Process.Start(psi) ?? throw new InvalidOperationException(
            $"Failed to spawn DuckDB at '{DuckDbExePath}'.");

        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_capturedStdout) _capturedStdout.AppendLine(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_capturedStderr) _capturedStderr.AppendLine(e.Data); };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        StreamWriter input = _process.StandardInput;
        await input.WriteLineAsync(".bail on").ConfigureAwait(false);
        await input.WriteLineAsync("INSTALL quack FROM core_nightly;").ConfigureAwait(false);
        await input.WriteLineAsync("LOAD quack;").ConfigureAwait(false);
        // spatial is preinstalled by the user; LOAD is enough. Best-effort
        // INSTALL keeps the fixture usable on machines where it's absent.
        await input.WriteLineAsync("INSTALL spatial;").ConfigureAwait(false);
        await input.WriteLineAsync("LOAD spatial;").ConfigureAwait(false);
        await input.WriteLineAsync($"CALL quack_serve('{QuackUri}', token => '{Token}');").ConfigureAwait(false);
        await input.FlushAsync().ConfigureAwait(false);

        try
        {
            await WaitForServerReadyAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"DuckDB server did not start.\n--- stdout ---\n{CapturedStdout}\n--- stderr ---\n{CapturedStderr}",
                ex);
        }
    }

    public async Task DisposeAsync()
    {
        if (_process is null) return;

        try
        {
            if (!_process.HasExited)
            {
                StreamWriter input = _process.StandardInput;
                try
                {
                    await input.WriteLineAsync($"CALL quack_stop('{QuackUri}');").ConfigureAwait(false);
                    await input.WriteLineAsync(".exit").ConfigureAwait(false);
                    await input.FlushAsync().ConfigureAwait(false);
                    input.Close();
                }
                catch
                {
                    // ignore; we will fall through to kill below
                }

                using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
                try
                {
                    await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { _process.Kill(entireProcessTree: true); } catch { }
                }
            }
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    private async Task WaitForServerReadyAsync(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            if (_process?.HasExited == true)
            {
                throw new InvalidOperationException(
                    $"DuckDB exited prematurely with code {_process.ExitCode}. Stdout: {CapturedStdout}\nStderr: {CapturedStderr}");
            }
            try
            {
                using TcpClient client = new();
                using CancellationTokenSource attemptCts = new(TimeSpan.FromSeconds(2));
                await client.ConnectAsync(IPAddress.Loopback, Port, attemptCts.Token).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                await Task.Delay(150).ConfigureAwait(false);
            }
        }
        throw new TimeoutException(
            $"DuckDB quack server did not become reachable on port {Port} within {timeout}.", lastError);
    }

    private static int PickFreeLoopbackPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
