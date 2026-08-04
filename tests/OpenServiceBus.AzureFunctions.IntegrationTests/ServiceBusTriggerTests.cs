using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Azure.Messaging.ServiceBus;
using OpenServiceBus.Testing;

namespace OpenServiceBus.AzureFunctions.IntegrationTests;

public class ServiceBusTriggerTests
{
    private const string QueueName = "integration-queue";

    [SkippableFact]
    public async Task ServiceBusTrigger_HundredMessages_AreAllProcessedByIsolatedWorkerAgainstOpenServiceBus()
    {
        // Arrange - environment prereqs
        var funcExe = FuncRuntime.FindFunc();
        Skip.If(funcExe is null,
            "Azure Functions Core Tools (`func`) not on PATH. Install: `brew install azure/functions/azure-functions-core-tools@4` or `npm i -g azure-functions-core-tools@4`.");
        var dotnet8Root = FuncRuntime.DiscoverDotNet8Root();
        Skip.If(dotnet8Root is null,
            "Microsoft.NETCore.App 8.x runtime is required to host the Functions worker. Install via `brew install dotnet@8` or the official .NET 8 installer.");

        var sampleDir = FuncRuntime.ResolveSampleProjectDir();
        var sentinelPath = Path.Combine(Path.GetTempPath(), $"osb-functions-{Guid.NewGuid():N}.log");
        var funcPort = GetFreePort();

        await using var broker = await OpenServiceBusTestHost.StartAsync();
        await broker.CreateQueueAsync(QueueName);

        // The worker reads the broker connection string from local.settings.json.
        // Patch it in-place so func doesn't need to know about test internals.
        var localSettingsPath = Path.Combine(sampleDir, "local.settings.json");
        var originalSettings = File.ReadAllText(localSettingsPath);
        File.WriteAllText(localSettingsPath, BuildLocalSettings(broker.ConnectionString));

        // The parent test build runs on .NET 10 SDK and writes the sample's obj/bin with SDK 10
        // paths baked into project.assets.json / dgspec.json. Func will rebuild under SDK 8 in a
        // moment, and those cached SDK 10 references make MSBuild try to load .NET 10 task DLLs
        // it can't reach. Nuke the per-project caches first so func starts from a clean slate.
        SafeDelete(Path.Combine(sampleDir, "obj"));
        SafeDelete(Path.Combine(sampleDir, "bin"));

        Process? func = null;
        var stdoutBuffer = new StringBuilder();
        var stderrBuffer = new StringBuilder();
        try
        {
            func = StartFunc(sampleDir, funcExe!, dotnet8Root!, sentinelPath, funcPort, stdoutBuffer, stderrBuffer);

            // Act - wait for func host to be ready, then dispatch 100 messages.
            await WaitForFuncReadyAsync(func, stdoutBuffer, stderrBuffer, TimeSpan.FromSeconds(180));

            await using var client = new ServiceBusClient(broker.ConnectionString);
            var sender = client.CreateSender(QueueName);
            var sendBatch = Enumerable.Range(0, 100)
                .Select(i => new ServiceBusMessage($"body-{i}") { MessageId = $"id-{i}" })
                .ToArray();
            foreach (var m in sendBatch)
            {
                await sender.SendMessageAsync(m);
            }

            // Wait for sentinel file to contain 100 lines, or func process to die.
            var processed = await WaitForLinesAsync(sentinelPath, expectedLines: 100, func, TimeSpan.FromSeconds(180));

            // Assert
            processed.Length.ShouldBe(100, $"trigger should have fired 100 times.\nfunc stdout:\n{stdoutBuffer}\nfunc stderr:\n{stderrBuffer}");
            processed.Distinct().Count().ShouldBe(100, "every message id should appear exactly once");
            for (var i = 0; i < 100; i++)
            {
                processed.ShouldContain($"id-{i}");
            }
        }
        finally
        {
            try { if (func is { HasExited: false }) func.Kill(entireProcessTree: true); } catch { /* best effort */ }
            func?.Dispose();
            File.WriteAllText(localSettingsPath, originalSettings);
            try { if (File.Exists(sentinelPath)) File.Delete(sentinelPath); } catch { /* best effort */ }
        }
    }

    [SkippableFact]
    public async Task ServiceBusTrigger_SurvivesQueueDeleteAndRecreate_WithoutDrainTimeout()
    {
        // Regression for issue #36: deleting + recreating an entity while a
        // ServiceBusTrigger stays attached used to strand the trigger's receiver - its
        // close stalled on "The operation did not complete within the allocated time
        // 00:01:00 for object drain" because the broker never detached links whose
        // entity was deleted.
        var funcExe = FuncRuntime.FindFunc();
        Skip.If(funcExe is null,
            "Azure Functions Core Tools (`func`) not on PATH. Install: `brew install azure/functions/azure-functions-core-tools@4` or `npm i -g azure-functions-core-tools@4`.");
        var dotnet8Root = FuncRuntime.DiscoverDotNet8Root();
        Skip.If(dotnet8Root is null,
            "Microsoft.NETCore.App 8.x runtime is required to host the Functions worker. Install via `brew install dotnet@8` or the official .NET 8 installer.");

        var sampleDir = FuncRuntime.ResolveSampleProjectDir();
        var sentinelPath = Path.Combine(Path.GetTempPath(), $"osb-functions-{Guid.NewGuid():N}.log");
        var funcPort = GetFreePort();

        await using var broker = await OpenServiceBusTestHost.StartAsync();
        await broker.CreateQueueAsync(QueueName);

        var localSettingsPath = Path.Combine(sampleDir, "local.settings.json");
        var originalSettings = File.ReadAllText(localSettingsPath);
        File.WriteAllText(localSettingsPath, BuildLocalSettings(broker.ConnectionString));
        SafeDelete(Path.Combine(sampleDir, "obj"));
        SafeDelete(Path.Combine(sampleDir, "bin"));

        Process? func = null;
        var stdoutBuffer = new StringBuilder();
        var stderrBuffer = new StringBuilder();
        try
        {
            func = StartFunc(sampleDir, funcExe!, dotnet8Root!, sentinelPath, funcPort, stdoutBuffer, stderrBuffer);
            await WaitForFuncReadyAsync(func, stdoutBuffer, stderrBuffer, TimeSpan.FromSeconds(180));

            await using var client = new ServiceBusClient(broker.ConnectionString);
            var sender = client.CreateSender(QueueName);

            for (var i = 0; i < 10; i++)
            {
                await sender.SendMessageAsync(new ServiceBusMessage($"round-1-{i}") { MessageId = $"a-{i}" });
            }
            (await WaitForLinesAsync(sentinelPath, expectedLines: 10, func, TimeSpan.FromSeconds(120)))
                .Length.ShouldBe(10, $"first round should be processed before the recreate.\nfunc stdout:\n{stdoutBuffer}\nfunc stderr:\n{stderrBuffer}");

            var admin = new Azure.Messaging.ServiceBus.Administration.ServiceBusAdministrationClient(broker.ConnectionString);
            await admin.DeleteQueueAsync(QueueName);
            await admin.CreateQueueAsync(QueueName);

            var deadline = DateTime.UtcNow.AddSeconds(30);
            for (var i = 0; i < 10; i++)
            {
                while (true)
                {
                    try
                    {
                        await sender.SendMessageAsync(new ServiceBusMessage($"round-2-{i}") { MessageId = $"b-{i}" });
                        break;
                    }
                    catch (ServiceBusException) when (DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(250);
                    }
                }
            }

            var processed = await WaitForLinesAsync(sentinelPath, expectedLines: 20, func, TimeSpan.FromSeconds(120));
            processed.Length.ShouldBe(20,
                $"the trigger should reconnect after the recreate and process round 2.\nfunc stdout:\n{stdoutBuffer}\nfunc stderr:\n{stderrBuffer}");
            for (var i = 0; i < 10; i++)
            {
                processed.ShouldContain($"a-{i}");
                processed.ShouldContain($"b-{i}");
            }

            string stdoutSnapshot;
            string stderrSnapshot;
            lock (stdoutBuffer) stdoutSnapshot = stdoutBuffer.ToString();
            lock (stderrBuffer) stderrSnapshot = stderrBuffer.ToString();
            stdoutSnapshot.ShouldNotContain("for object drain");
            stderrSnapshot.ShouldNotContain("for object drain");
        }
        finally
        {
            try { if (func is { HasExited: false }) func.Kill(entireProcessTree: true); } catch { /* best effort */ }
            func?.Dispose();
            File.WriteAllText(localSettingsPath, originalSettings);
            try { if (File.Exists(sentinelPath)) File.Delete(sentinelPath); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Spawns Azure Functions Core Tools via a /bin/sh wrapper so DOTNET_ROOT and PATH are
    /// scoped to the child (brew's dotnet@8 is keg-only), with `dotnet test`'s injected
    /// MSBuild/SDK-10 environment variables unset so func's SDK-8 build isn't poisoned.
    /// </summary>
    private static Process StartFunc(
        string sampleDir,
        string funcExe,
        string dotnet8Root,
        string sentinelPath,
        int funcPort,
        StringBuilder stdoutBuffer,
        StringBuilder stderrBuffer)
    {
        var existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var shellCommand = "unset MSBuildExtensionsPath MSBuildSDKsPath MSBuildLoadMicrosoftTargetsReadOnly " +
                           "DOTNET_HOST_PATH DOTNET_CLI_TELEMETRY_SESSIONID DOTNET_ADD_GLOBAL_TOOLS_TO_PATH " +
                           "DOTNET_NOLOGO MSBuildToolsPath MSBUILD_EXE_PATH MSBuildBinPath; " +
                           $"export DOTNET_ROOT={ShellQuote(dotnet8Root)}; " +
                           $"export PATH={ShellQuote(dotnet8Root)}:{ShellQuote(existingPath)}; " +
                           $"export OSB_FUNCTIONS_SENTINEL_FILE={ShellQuote(sentinelPath)}; " +
                           $"exec {ShellQuote(funcExe)} start --port {funcPort} --verbose";

        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            WorkingDirectory = sampleDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(shellCommand);

        var func = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to spawn `func` process.");
        func.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdoutBuffer) stdoutBuffer.AppendLine(e.Data); };
        func.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderrBuffer) stderrBuffer.AppendLine(e.Data); };
        func.BeginOutputReadLine();
        func.BeginErrorReadLine();
        return func;
    }

    private static async Task WaitForFuncReadyAsync(Process func, StringBuilder stdout, StringBuilder stderr, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (func.HasExited)
            {
                throw new InvalidOperationException(
                    $"`func` exited prematurely (exit code {func.ExitCode}).\nstdout:\n{stdout}\nstderr:\n{stderr}");
            }
            string snapshot;
            lock (stdout) { snapshot = stdout.ToString(); }
            // The Functions host emits these once the runtime is fully up and triggers are listening.
            if (snapshot.Contains("Host lock lease acquired", StringComparison.Ordinal)
                || snapshot.Contains("Worker process started and initialized", StringComparison.Ordinal)
                || snapshot.Contains("Job host started", StringComparison.OrdinalIgnoreCase))
            {
                // Give it another moment to wire the trigger before sending messages.
                await Task.Delay(500);
                return;
            }
            await Task.Delay(250);
        }
        throw new TimeoutException(
            $"Timed out waiting for `func` to become ready.\nstdout:\n{stdout}\nstderr:\n{stderr}");
    }

    private static async Task<string[]> WaitForLinesAsync(string path, int expectedLines, Process func, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (func.HasExited)
            {
                throw new InvalidOperationException($"`func` process exited (code {func.ExitCode}) while waiting for messages to be processed.");
            }
            if (File.Exists(path))
            {
                var lines = await ReadAllLinesShareReadAsync(path);
                if (lines.Length >= expectedLines)
                {
                    return lines;
                }
            }
            await Task.Delay(200);
        }
        var found = File.Exists(path) ? await ReadAllLinesShareReadAsync(path) : Array.Empty<string>();
        return found;
    }

    private static async Task<string[]> ReadAllLinesShareReadAsync(string path)
    {
        // The worker holds the file open intermittently - use shared-read access.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();
        return content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToArray();
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static void SafeDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* best effort */ }
    }

    private static string BuildLocalSettings(string serviceBusConnection)
    {
        // Embed quotes by JSON-escaping the connection string.
        var escaped = serviceBusConnection.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $$"""
                 {
                   "IsEncrypted": false,
                   "Values": {
                     "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
                     "AzureWebJobsStorage": "",
                     "ServiceBusConnection": "{{escaped}}"
                   }
                 }
                 """;
    }
}
