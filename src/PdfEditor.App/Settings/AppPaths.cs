using PdfEditor.Document.Recovery;

namespace PdfEditor.App.Settings;

/// <summary>설정 파일과 자동 복구 폴더의 위치. 포터블 표식(portable.txt)이 있으면 실행 폴더 아래 data\ 를 쓴다.</summary>
public sealed record AppPaths(bool IsPortable, string SettingsPath, string RecoveryFolder, bool FellBackFromPortable)
{
    public const string PortableMarker = "portable.txt";

    /// <summary>순서: 환경 변수 → 포터블(표식 + 쓰기 가능) → 표식은 있는데 쓸 수 없으면 일반 경로 + FellBackFromPortable → 일반 경로.</summary>
    public static AppPaths Resolve(string exeFolder, Func<string, bool> canWrite)
    {
        string? envSettings = Environment.GetEnvironmentVariable("PDFEDITOR_SETTINGS_PATH");
        string? envRecovery = Environment.GetEnvironmentVariable("PDFEDITOR_RECOVERY_DIR");
        if (envSettings is { Length: > 0 } || envRecovery is { Length: > 0 })
        {
            // 한쪽만 지정되면 나머지는 일반 경로(M1과 같은 동작)
            return new AppPaths(false,
                envSettings is { Length: > 0 } ? envSettings : NormalSettings(),
                envRecovery is { Length: > 0 } ? envRecovery : NormalRecovery(),
                false);
        }

        bool hasMarker = false;
        try { hasMarker = File.Exists(Path.Combine(exeFolder, PortableMarker)); }
        catch (Exception) { /* 표식을 확인할 수 없으면 일반 모드 */ }

        if (hasMarker)
        {
            string data = Path.Combine(exeFolder, "data");
            bool ok;
            try { ok = canWrite(data); } catch (Exception) { ok = false; }
            if (ok)
                return new AppPaths(true, Path.Combine(data, "settings.json"), Path.Combine(data, "Recovery"), false);
            return new AppPaths(false, NormalSettings(), NormalRecovery(), true);
        }
        return new AppPaths(false, NormalSettings(), NormalRecovery(), false);
    }

    /// <summary>폴더를 만들고 임시 파일을 써 보고 지운다. 어떤 예외든 false.</summary>
    public static bool CanWriteFolder(string folder)
    {
        string? probe = null;
        try
        {
            Directory.CreateDirectory(folder);
            probe = Path.Combine(folder, ".write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(probe, "x");
            return true;
        }
        catch (Exception) { return false; }
        finally
        {
            try { if (probe is not null && File.Exists(probe)) File.Delete(probe); } catch (Exception) { }
        }
    }

    // 옛 PdfEditor 폴더 → SedolPDF 폴더 한 번 옮기기는 DefaultPathIn / DefaultFolderIn 이 맡는다.
    private static string NormalSettings() =>
        SettingsStore.DefaultPathIn(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
    private static string NormalRecovery() =>
        RecoveryStore.DefaultFolderIn(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
}
