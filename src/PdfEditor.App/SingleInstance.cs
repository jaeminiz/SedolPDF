using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace PdfEditor.App;

/// <summary>
/// 단일 인스턴스: 먼저 뜬 프로세스가 명명된 파이프로 다른 실행의 명령줄 파일 경로를 받아 새 탭으로 연다.
/// 이름은 사용자 세션 단위(Local\)라 포터블·설치·스토어(MSIX) 모두 같은 사용자 안에서 하나로 합쳐진다.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly string _pipeName;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(string pipeName, Mutex mutex) { _pipeName = pipeName; _mutex = mutex; }

    public static string DefaultName => "SedolPDF-" + Environment.UserName;

    /// <summary>처음 뜬 프로세스면 인스턴스를, 이미 실행 중이면 null.</summary>
    public static SingleInstance? TryBecomePrimary(string name)
    {
        var mutex = new Mutex(false, @"Local\" + name + "-mutex");
        bool owned;
        try { owned = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }
        if (owned) return new SingleInstance(name + "-pipe", mutex);
        mutex.Dispose();
        return null;
    }

    /// <summary>실행 중인 인스턴스에 경로(비어 있으면 창 앞으로만)를 보낸다. 서버가 아직 안 떴으면 timeout까지 기다린다.</summary>
    public static bool TrySend(string name, IReadOnlyList<string> paths, int timeoutMs = 4000)
    {
        try
        {
            AllowSetForegroundWindow(-1);   // 받는 쪽이 창을 앞으로 가져올 수 있게
            using var client = new NamedPipeClientStream(".", name + "-pipe", PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            using var w = new StreamWriter(client, new UTF8Encoding(false));
            foreach (var p in paths) w.WriteLine(p);
            w.Flush();
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>받은 경로 목록을 onReceived에 넘긴다(스레드 풀 스레드에서 호출됨).</summary>
    public void StartServer(Action<IReadOnlyList<string>> onReceived)
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_cts.Token);
                    using var r = new StreamReader(server, new UTF8Encoding(false));
                    var paths = new List<string>();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    timeout.CancelAfter(3000);
                    while (await r.ReadLineAsync(timeout.Token) is { } line)
                        if (line.Length > 0 && paths.Count < 64) paths.Add(line);
                    onReceived(paths);
                }
                catch (OperationCanceledException) { if (_cts.IsCancellationRequested) return; }
                catch (Exception) { await Task.Delay(200); }
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _mutex.ReleaseMutex(); } catch (Exception) { }
        _mutex.Dispose();
        _cts.Dispose();
    }

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
}
