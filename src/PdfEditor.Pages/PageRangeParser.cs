namespace PdfEditor.Pages;

/// <summary>"1-3, 5, 7-8" 같은 1부터 세는 쪽 범위 입력을 0부터 세는 인덱스로 바꾼다.</summary>
public static class PageRangeParser
{
    public static bool TryParse(string text, int pageCount, out int[] zeroBasedIndices)
    {
        zeroBasedIndices = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            zeroBasedIndices = Enumerable.Range(0, pageCount).ToArray();
            return true;
        }

        var result = new List<int>();
        var seen = new HashSet<int>();
        foreach (var raw in text.Split(','))
        {
            var token = raw.Trim();
            if (token.Length == 0) return false;   // "1," ",1" "1,,2"

            int a, b;
            int dash = token.IndexOf('-');
            if (dash < 0)
            {
                if (!TryNumber(token, out a)) return false;
                b = a;
            }
            else
            {
                if (!TryNumber(token[..dash], out a) || !TryNumber(token[(dash + 1)..], out b)) return false;
                if (a > b) return false;
            }
            if (a < 1 || b > pageCount) return false;

            for (int p = a; p <= b; p++)
                if (seen.Add(p)) result.Add(p - 1);
        }
        zeroBasedIndices = result.ToArray();
        return true;
    }

    // 숫자만 허용 (부호·소수점·"1-2"의 두 번째 '-' 거부). 공백은 앞뒤만 허용.
    private static bool TryNumber(string s, out int n)
    {
        n = 0;
        s = s.Trim();
        if (s.Length == 0 || s.Length > 9) return false;
        foreach (var c in s)
            if (c is < '0' or > '9') return false;
        n = int.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }
}
