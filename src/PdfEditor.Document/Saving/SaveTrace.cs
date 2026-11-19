using System.Diagnostics;

namespace PdfEditor.Document.Saving;

/// <summary>
/// 저장 경로 단계별 시간 측정(0.14.0 저장 속도). <see cref="Sink"/>가 null이면 아무것도 하지 않는다(비용 = null 확인 한 번).
/// 시험 하네스(SaveSpeedBenchTests)가 Sink를 걸어 단계 이름과 ms를 모은다. 단계는 겹칠 수 있다(바깥 단계가 안쪽 단계를 포함).
/// </summary>
public static class SaveTrace
{
    /// <summary>(단계 이름, 걸린 ms). 여러 스레드(Pdfium 전용 스레드·스레드 풀)에서 불릴 수 있다 — 받는 쪽이 잠근다.</summary>
    public static Action<string, double>? Sink { get; set; }

    public static Scope Step(string name) => new(Sink is null ? null : name);

    public readonly struct Scope(string? name) : IDisposable
    {
        private readonly long _start = name is null ? 0 : Stopwatch.GetTimestamp();

        public void Dispose()
        {
            if (name is null) return;
            Sink?.Invoke(name, Stopwatch.GetElapsedTime(_start).TotalMilliseconds);
        }
    }
}
