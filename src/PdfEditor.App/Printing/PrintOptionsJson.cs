using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdfEditor.App.Printing;

/// <summary>마지막에 쓴 인쇄 옵션의 저장·읽기. 쪽 범위와 프린터는 기억하지 않는다(문서마다 달라서; 프린터별 기억은 0.6 이후).</summary>
public static class PrintOptionsJson
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(PrintOptions o) => JsonSerializer.Serialize(o, Opts);

    /// <summary>깨졌거나 비어 있으면 null. 값은 허용 범위로 맞춘다.</summary>
    public static PrintOptions? TryDeserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<PrintOptions>(json, Opts)?.Normalized(); }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    /// <summary>다음 번 대화상자의 시작값: 쪽 범위는 처음(전체), 프린터는 비움.</summary>
    public static PrintOptions ForRemembering(PrintOptions o) => o with { Range = PrintRangeMode.All, RangeText = "", Parity = PrintParity.All, PrinterName = null };
}
